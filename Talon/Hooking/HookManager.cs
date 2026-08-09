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
    internal static Lock HookEnableSyncRoot { get; } = new();
}
