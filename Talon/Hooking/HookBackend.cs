namespace Talon.Hooking;

/// <summary>Selects the native patching engine for a hook.</summary>
public enum HookBackend
{
    /// <summary>Uses Talon's default backend.</summary>
    Automatic,

    /// <summary>Uses Reloaded.Hooks.</summary>
    Reloaded,

    /// <summary>Uses MinHook.</summary>
    MinHook,
}
