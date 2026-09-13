using System;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.Input;

/// <summary>
/// Owns the navigation-context tree and decides which context receives controller
/// actions.
/// </summary>
/// <remarks>
/// The active context is resolved deterministically from two inputs:
/// <list type="number">
/// <item>
/// The <b>focused context</b>: the nearest <see cref="NavigationScope"/> attached to the
/// focused control or one of its ancestors. If that context (or an ancestor of it) is
/// disabled, the nearest reachable ancestor is used instead. This lets controls that live
/// outside a frame's visual tree (such as title-bar tabs) route into that frame's context.
/// </item>
/// <item>
/// The <b>logical activation chain</b>: starting from the focused context (or the root when
/// nothing scoped is focused), the chain of enabled <see cref="NavigationContext.ActiveChild"/>
/// links is followed to the innermost context.
/// </item>
/// </list>
/// Actions are dispatched to the active context first and then to each ancestor until one
/// consumes the action.
/// </remarks>
public sealed class NavigationContextService
{
    private NavigationContext? _focusedContext;

    public NavigationContextService()
    {
        this.Root = new NavigationContext("Root");
        this.ActiveContext = this.Root;
        this.Root.Changed += (_, _) => this.Refresh();
    }

    /// <summary>
    /// The root of the context tree. Never disabled and never has a handler of its own.
    /// </summary>
    public NavigationContext Root { get; }

    /// <summary>
    /// The context declared by the focused control's nearest <see cref="NavigationScope"/>,
    /// or null when no scoped control is focused.
    /// </summary>
    public NavigationContext? FocusedContext => this._focusedContext;

    /// <summary>
    /// The context that receives controller actions first.
    /// </summary>
    public NavigationContext ActiveContext { get; private set; }

    /// <summary>
    /// Raised when <see cref="ActiveContext"/> changes, whether from a focus change or
    /// from a change in the context tree.
    /// </summary>
    public event EventHandler<NavigationContextChangedEventArgs>? ActiveContextChanged;

    /// <summary>
    /// Updates the focused context. Called by the input router when keyboard focus moves.
    /// </summary>
    public void SetFocusedContext(NavigationContext? context)
    {
        if (ReferenceEquals(this._focusedContext, context))
            return;

        this._focusedContext = context;
        this.Refresh();
    }

    /// <summary>
    /// Computes the context that should currently receive actions.
    /// </summary>
    public NavigationContext Resolve()
    {
        var start = this._focusedContext;
        while (start != null && !(start.IsReachable && start.IsSelfOrDescendantOf(this.Root)))
            start = start.Parent;

        return (start ?? this.Root).ActiveLeaf;
    }

    /// <summary>
    /// Dispatches an action to the active context and then up through its ancestors.
    /// </summary>
    /// <returns>True if a context consumed the action.</returns>
    public bool Dispatch(ControllerAction action, bool isRepeat)
    {
        this.Refresh();

        foreach (var context in this.ActiveContext.SelfAndAncestors())
        {
            if (context.Handle(action, isRepeat))
                return true;
        }

        return false;
    }

    private void Refresh()
    {
        var next = this.Resolve();
        if (ReferenceEquals(next, this.ActiveContext))
            return;

        var previous = this.ActiveContext;
        this.ActiveContext = next;
        this.ActiveContextChanged?.Invoke(this, new NavigationContextChangedEventArgs(previous, next));
    }
}

/// <summary>
/// Event arguments for <see cref="NavigationContextService.ActiveContextChanged"/>.
/// </summary>
public sealed class NavigationContextChangedEventArgs(NavigationContext previous, NavigationContext current)
    : EventArgs
{
    public NavigationContext Previous { get; } = previous;
    public NavigationContext Current { get; } = current;
}
