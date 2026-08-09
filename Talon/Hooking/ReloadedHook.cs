using Reloaded.Hooks;

// Adapted from Dalamud.Hooking.Internal.ReloadedHook<T>; see THIRD_PARTY_NOTICES.md.

namespace Talon.Hooking;

internal sealed class ReloadedHook<T> : Hook<T> where T : Delegate
{
    private readonly Reloaded.Hooks.Definitions.IHook<T> implementation;

    public ReloadedHook(nint address, T detour) : base(address)
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        implementation = ReloadedHooks.Instance.CreateHook(detour, address.ToInt64());
    }

    public override T Original
    {
        get
        {
            CheckDisposed();
            return implementation.OriginalFunction;
        }
    }

    public override T OriginalDisposeSafe => implementation.OriginalFunction;
    public override bool IsEnabled => !IsDisposed && implementation.IsHookEnabled;
    public override string BackendName => "Reloaded";

    public override void Enable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        CheckDisposed();
        if (!implementation.IsHookActivated) implementation.Activate();
        else if (!implementation.IsHookEnabled) implementation.Enable();
        HookManager.TrackEnabled(this);
    }

    public override void Disable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (!IsDisposed && implementation.IsHookActivated && implementation.IsHookEnabled)
        {
            implementation.Disable();
            HookManager.TrackDisabled(this);
        }
    }

    public override void Dispose()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (IsDisposed) return;
        Disable();
        HookManager.TrackDisabled(this);
        base.Dispose();
    }
}
