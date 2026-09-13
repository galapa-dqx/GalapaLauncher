using System;
using System.Collections.Generic;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.Input;

/// <summary>
/// Handles a controller action on behalf of a <see cref="NavigationContext"/>.
/// </summary>
/// <param name="action">The semantic action to handle.</param>
/// <param name="isRepeat">Whether this is a repeat event from holding the button.</param>
/// <returns>True if the action was consumed and should not propagate to parent contexts.</returns>
public delegate bool NavigationActionHandler(ControllerAction action, bool isRepeat);

/// <summary>
/// A logical navigation scope. Contexts form a tree that mirrors the application's
/// logical navigation structure (window → app shell → settings → ...), independently of
/// where the corresponding controls sit in the visual tree.
/// </summary>
/// <remarks>
/// <para>
/// Each context tracks which of its children is logically active (for example, the
/// app shell activates the context of its selected page). The chain of active children
/// from the root yields the <see cref="ActiveLeaf"/>, which is where controller actions
/// are dispatched first before propagating to ancestors.
/// </para>
/// <para>
/// A disabled context (<see cref="IsEnabled"/> = false) and its entire subtree are
/// skipped both for activation and for dispatch. This is how onboarding suppresses
/// normal Launcher/Settings navigation.
/// </para>
/// </remarks>
public sealed class NavigationContext
{
    private readonly List<NavigationContext> _children = [];
    private NavigationContext? _activeChild;
    private bool _isEnabled = true;

    public NavigationContext(string name, NavigationActionHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Name = name;
        this.Handler = handler;
    }

    /// <summary>
    /// Human-readable name used for logging and diagnostics.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The handler invoked when an action reaches this context.
    /// </summary>
    public NavigationActionHandler? Handler { get; set; }

    /// <summary>
    /// The parent context, or null for a root or detached context.
    /// </summary>
    public NavigationContext? Parent { get; private set; }

    /// <summary>
    /// The attached child contexts in attachment order.
    /// </summary>
    public IReadOnlyList<NavigationContext> Children => this._children;

    /// <summary>
    /// Whether this context participates in activation and dispatch.
    /// Disabling a context also disables its subtree.
    /// </summary>
    public bool IsEnabled
    {
        get => this._isEnabled;
        set
        {
            if (this._isEnabled == value)
                return;

            this._isEnabled = value;
            this.RaiseChanged();
        }
    }

    /// <summary>
    /// True when this context and every ancestor are enabled.
    /// </summary>
    public bool IsReachable => this._isEnabled && (this.Parent?.IsReachable ?? true);

    /// <summary>
    /// The logically active child. Setting a context that is not yet a child attaches it.
    /// </summary>
    public NavigationContext? ActiveChild
    {
        get => this._activeChild;
        set
        {
            if (value != null && !ReferenceEquals(value.Parent, this))
                this.AttachChild(value);

            if (ReferenceEquals(this._activeChild, value))
                return;

            this._activeChild = value;
            this.RaiseChanged();
        }
    }

    /// <summary>
    /// Follows the chain of enabled active children down to the innermost context.
    /// </summary>
    public NavigationContext ActiveLeaf
    {
        get
        {
            var current = this;
            while (current._activeChild is { IsEnabled: true } next)
                current = next;

            return current;
        }
    }

    /// <summary>
    /// Raised when this context or any descendant changes structure, enabled state,
    /// or active child. Bubbles up to ancestors.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Attaches <paramref name="child"/> under this context, detaching it from any
    /// previous parent first.
    /// </summary>
    public void AttachChild(NavigationContext child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (ReferenceEquals(child, this))
            throw new InvalidOperationException($"Cannot attach navigation context '{this.Name}' to itself.");

        if (this.IsSelfOrDescendantOf(child))
            throw new InvalidOperationException(
                $"Cannot attach navigation context '{child.Name}' under its own descendant '{this.Name}'.");

        if (ReferenceEquals(child.Parent, this))
            return;

        child.Parent?.DetachChild(child);
        this._children.Add(child);
        child.Parent = this;
        this.RaiseChanged();
    }

    /// <summary>
    /// Detaches <paramref name="child"/> from this context.
    /// </summary>
    /// <returns>True if the child was attached to this context.</returns>
    public bool DetachChild(NavigationContext child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!ReferenceEquals(child.Parent, this))
            return false;

        this._children.Remove(child);
        child.Parent = null;
        if (ReferenceEquals(this._activeChild, child))
            this._activeChild = null;

        this.RaiseChanged();
        return true;
    }

    /// <summary>
    /// Makes this context the active child of each ancestor in turn, so that it becomes
    /// the root's <see cref="ActiveLeaf"/> (provided the chain is enabled).
    /// </summary>
    public void Activate()
    {
        var current = this;
        while (current.Parent is { } parent)
        {
            parent.ActiveChild = current;
            current = parent;
        }
    }

    /// <summary>
    /// Enumerates this context followed by each ancestor up to the root.
    /// </summary>
    public IEnumerable<NavigationContext> SelfAndAncestors()
    {
        for (var current = this; current != null; current = current.Parent)
            yield return current;
    }

    /// <summary>
    /// True when <paramref name="other"/> is this context or one of its ancestors.
    /// </summary>
    public bool IsSelfOrDescendantOf(NavigationContext other)
    {
        foreach (var context in this.SelfAndAncestors())
        {
            if (ReferenceEquals(context, other))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Invokes this context's handler if the context is enabled.
    /// </summary>
    public bool Handle(ControllerAction action, bool isRepeat)
    {
        return this._isEnabled && this.Handler?.Invoke(action, isRepeat) == true;
    }

    public override string ToString() => this.Name;

    private void RaiseChanged()
    {
        for (var current = this; current != null; current = current.Parent)
            current.Changed?.Invoke(this, EventArgs.Empty);
    }
}
