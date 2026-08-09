namespace Talon.Hooking;

// Adapted from Dalamud.Hooking.Internal.MinHookHook<T>; see THIRD_PARTY_NOTICES.md.

internal sealed class MinHook<T> : Hook<T> where T : Delegate
{
    private const int FirstHookIdentifier = 0;
    private readonly MinSharp.Hook<T> implementation;

    public MinHook(nint address, T detour) : base(address)
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        implementation = new MinSharp.Hook<T>(address, detour, FirstHookIdentifier);
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
