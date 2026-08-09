namespace Talon.Hooking;

// Backend selection follows Dalamud's game interop provider; see THIRD_PARTY_NOTICES.md.

/// <summary>Selects the native patching engine for a hook.</summary>
public enum HookBackend
{
    /// <summary>Uses Talon's default backend.</summary>
    Automatic,

    /// <summary>Uses Reloaded.Hooks.</summary>
    Reloaded,
}
