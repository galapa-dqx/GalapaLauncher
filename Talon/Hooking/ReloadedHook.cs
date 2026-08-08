using Reloaded.Hooks;

namespace Talon.Hooking;

internal sealed class ReloadedHook<T> : Hook<T> where T : Delegate
{
    private readonly Reloaded.Hooks.Definitions.IHook<T> implementation;

    public ReloadedHook(nint address, T detour) : base(address)
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        implementation = ReloadedHooks.Instance.CreateHook(detour, address.ToInt64());
        implementation.Activate();
        implementation.Disable();
    }

    public override T Original
    {
        get
        {
            CheckDisposed();
            return implementation.OriginalFunction;
        }
    }

    public override bool IsEnabled => !IsDisposed && implementation.IsHookEnabled;
    public override string BackendName => "Reloaded";

    public override void Enable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        CheckDisposed();
        if (!implementation.IsHookEnabled) implementation.Enable();
    }

    public override void Disable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (!IsDisposed && implementation.IsHookActivated && implementation.IsHookEnabled)
            implementation.Disable();
    }

    public override void Dispose()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (IsDisposed) return;
        Disable();
        base.Dispose();
    }
}
