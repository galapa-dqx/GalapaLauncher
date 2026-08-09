using System.Threading;

// The process-wide patch lock follows Dalamud.Hooking.Internal.HookManager;
// see THIRD_PARTY_NOTICES.md.

namespace Talon.Hooking;

/// <summary>
/// Serializes hook patching and teardown. Hook creation can occur from multiple
/// game network threads executing VCE callbacks, while each backend mutates
/// shared process code or tables.
/// </summary>
internal static class HookManager
{
    private static readonly HashSet<object> EnabledHooks = [];

    internal static Lock HookEnableSyncRoot { get; } = new();

    // Native code retains only a function pointer. Root enabled hook objects so
    // an extension cannot collect its reverse delegate wrapper by accident.
    internal static void TrackEnabled(object hook) => EnabledHooks.Add(hook);
    internal static void TrackDisabled(object hook) => EnabledHooks.Remove(hook);
}
