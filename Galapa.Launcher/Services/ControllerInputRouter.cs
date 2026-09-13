using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Services;

/// <summary>
/// Routes controller actions to the UI in three stages:
/// <list type="number">
/// <item>Control-local handlers: the focused element and its visual ancestors that
/// implement <see cref="IControllerInputHandler"/>.</item>
/// <item>The active <see cref="NavigationContext"/> and its ancestors, resolved by the
/// <see cref="NavigationContextService"/> from the focused control's
/// <see cref="NavigationScope"/> and the logical activation chain.</item>
/// <item>Default behaviour: XY focus navigation for the d-pad, Enter/Escape for Confirm/Decline.</item>
/// </list>
/// </summary>
public class ControllerInputRouter : IDisposable
{
    private readonly ILogger<ControllerInputRouter> _logger;
    private readonly IControllerActionSource _actionSource;
    private readonly NavigationContextService _navigation;
    private TopLevel? _topLevel;
    private Visual? _trackedFocus;

    public ControllerInputRouter(
        ILogger<ControllerInputRouter> logger,
        IControllerActionSource actionSource,
        NavigationContextService navigation)
    {
        this._logger = logger;
        this._actionSource = actionSource;
        this._navigation = navigation;
    }

    /// <summary>
    /// Attaches the router to a top-level window.
    /// </summary>
    public void Attach(TopLevel topLevel)
    {
        if (this._topLevel != null)
            this.Detach();

        this._topLevel = topLevel;
        this._actionSource.ActionTriggered += this.OnActionTriggered;
        this._actionSource.ActionRepeated += this.OnActionRepeated;
        topLevel.AddHandler(InputElement.GotFocusEvent, this.OnFocusChanged, RoutingStrategies.Bubble, true);
        topLevel.AddHandler(InputElement.LostFocusEvent, this.OnFocusChanged, RoutingStrategies.Bubble, true);
        this.SyncFocusedContext();
        this._logger.LogDebug("Attached to {TopLevel}", topLevel.GetType().Name);
    }

    /// <summary>
    /// Detaches the router from the current top-level window.
    /// </summary>
    public void Detach()
    {
        if (this._topLevel == null)
            return;

        this._actionSource.ActionTriggered -= this.OnActionTriggered;
        this._actionSource.ActionRepeated -= this.OnActionRepeated;
        this._topLevel.RemoveHandler(InputElement.GotFocusEvent, this.OnFocusChanged);
        this._topLevel.RemoveHandler(InputElement.LostFocusEvent, this.OnFocusChanged);
        this.TrackFocusedVisual(null);
        this._navigation.SetFocusedContext(null);
        this._logger.LogDebug("Detached from {TopLevel}", this._topLevel.GetType().Name);
        this._topLevel = null;
    }

    /// <summary>
    /// Routes a single action through local handlers, navigation contexts, and defaults.
    /// </summary>
    /// <returns>True if a local handler or navigation context consumed the action.</returns>
    public bool Route(ControllerAction action, bool isRepeat)
    {
        if (this._topLevel == null)
        {
            this._logger.LogWarning("Route called but no TopLevel attached");
            return false;
        }

        var focused = this.GetFocusedVisual();

        this._logger.LogInformation("Routing action {Action} (repeat={IsRepeat}), focused={Focused}",
            action, isRepeat, focused?.GetType().Name ?? "none");

        // Stage 1: control-local handlers, from the focused element upward.
        for (var current = focused ?? this._topLevel; current != null; current = current.GetVisualParent())
        {
            if (current is IControllerInputHandler handler && handler.HandleControllerInput(action, isRepeat))
            {
                this._logger.LogDebug("Action {Action} handled locally by {Handler}", action, current.GetType().Name);
                return true;
            }
        }

        // Stage 2: the active navigation context and its ancestors.
        this._navigation.SetFocusedContext(NavigationScope.Find(focused));
        if (this._navigation.Dispatch(action, isRepeat))
        {
            this._logger.LogDebug("Action {Action} handled by navigation context {Context}",
                action, this._navigation.ActiveContext.Name);
            return true;
        }

        // Stage 3: default behaviour.
        this.HandleDefault(action, isRepeat);
        return false;
    }

    private void OnActionTriggered(object? sender, ControllerActionEventArgs e)
    {
        this.Route(e.Action, isRepeat: false);
    }

    private void OnActionRepeated(object? sender, ControllerActionEventArgs e)
    {
        this.Route(e.Action, isRepeat: true);
    }

    private void OnFocusChanged(object? sender, RoutedEventArgs e)
    {
        this.SyncFocusedContext();
    }

    private void OnFocusedDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // A control removed from the tree (e.g. by a page switch) loses focus without a
        // LostFocus event reaching the window, so re-evaluate the focused context here.
        this.SyncFocusedContext(ignore: sender as Visual);
    }

    private void SyncFocusedContext(Visual? ignore = null)
    {
        var focused = this.GetFocusedVisual();
        if (ReferenceEquals(focused, ignore))
            focused = null;

        this.TrackFocusedVisual(focused);
        this._navigation.SetFocusedContext(NavigationScope.Find(focused));
    }

    private void TrackFocusedVisual(Visual? focused)
    {
        if (ReferenceEquals(this._trackedFocus, focused))
            return;

        if (this._trackedFocus != null)
            this._trackedFocus.DetachedFromVisualTree -= this.OnFocusedDetached;

        this._trackedFocus = focused;

        if (focused != null)
            focused.DetachedFromVisualTree += this.OnFocusedDetached;
    }

    /// <summary>
    /// The focused element, or null when nothing is focused or the focused element has been
    /// removed from the visual tree.
    /// </summary>
    private Visual? GetFocusedVisual()
    {
        var focused = this._topLevel?.FocusManager?.GetFocusedElement() as Visual;
        return focused != null && focused.IsAttachedToVisualTree() ? focused : null;
    }

    private void HandleDefault(ControllerAction action, bool isRepeat)
    {
        if (this._topLevel == null)
            return;

        var focusManager = this._topLevel.FocusManager;
        var focused = focusManager?.GetFocusedElement() as InputElement;

        switch (action)
        {
            case ControllerAction.Up:
            case ControllerAction.Down:
            case ControllerAction.Left:
            case ControllerAction.Right:
                this.NavigateXYFocus(action);
                break;

            case ControllerAction.Confirm:
                if (focused != null)
                {
                    // Simulate Enter key press
                    var keyArgs = new KeyEventArgs
                    {
                        Key = Key.Enter,
                        RoutedEvent = InputElement.KeyDownEvent
                    };
                    focused.RaiseEvent(keyArgs);
                }

                break;

            case ControllerAction.Decline:
                if (focused != null)
                {
                    // Simulate Escape key press
                    var keyArgs = new KeyEventArgs
                    {
                        Key = Key.Escape,
                        RoutedEvent = InputElement.KeyDownEvent
                    };
                    focused.RaiseEvent(keyArgs);
                }

                break;

            // Bumpers and triggers have no default action
            case ControllerAction.BumperLeft:
            case ControllerAction.BumperRight:
            case ControllerAction.TriggerLeft:
            case ControllerAction.TriggerRight:
                break;
        }
    }

    private void NavigateXYFocus(ControllerAction action)
    {
        if (this._topLevel == null)
            return;

        var direction = action switch
        {
            ControllerAction.Up => NavigationDirection.Up,
            ControllerAction.Down => NavigationDirection.Down,
            ControllerAction.Left => NavigationDirection.Left,
            ControllerAction.Right => NavigationDirection.Right,
            _ => NavigationDirection.Next
        };

        var focusManager = this._topLevel.FocusManager;
        var focused = focusManager?.GetFocusedElement() as InputElement;

        if (focused != null)
        {
            // Use Avalonia's focus navigation
            var next = KeyboardNavigationHandler.GetNext(focused, direction);
            if (next != null)
            {
                this._logger.LogDebug("Navigating from {From} to {To}", focused.GetType().Name, next.GetType().Name);
                next.Focus(NavigationMethod.Directional);
            }
        }
        else
        {
            // Nothing focused - find first focusable element using Tab navigation
            var first = KeyboardNavigationHandler.GetNext(this._topLevel, NavigationDirection.Next);
            if (first != null)
            {
                this._logger.LogInformation("Nothing focused, focusing first element: {Element}", first.GetType().Name);
                first.Focus(NavigationMethod.Directional);
            }
            else
            {
                this._logger.LogWarning("No focusable elements found");
            }
        }
    }

    public void Dispose()
    {
        this.Detach();
    }
}
