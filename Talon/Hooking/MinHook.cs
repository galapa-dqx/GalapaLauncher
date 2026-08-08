namespace Talon.Hooking;

internal sealed class MinHook<T> : Hook<T> where T : Delegate
{
    private readonly MinSharp.Hook<T> implementation;

    public MinHook(nint address, T detour) : base(address)
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        implementation = new MinSharp.Hook<T>(address, detour, 0);
    }

    public override T Original
    {
        get
        {
            CheckDisposed();
            return implementation.Original;
        }
    }

    public override bool IsEnabled => !IsDisposed && implementation.Enabled;
    public override string BackendName => "MinHook";

    public override void Enable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        CheckDisposed();
        if (!implementation.Enabled) implementation.Enable();
    }

    public override void Disable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (!IsDisposed && implementation.Enabled) implementation.Disable();
    }

    public override void Dispose()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (IsDisposed) return;
        implementation.Dispose();
        base.Dispose();
    }
}
