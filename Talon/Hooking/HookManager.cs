using System.Threading;

namespace Talon.Hooking;

/// <summary>
/// Serializes hook patching and teardown. Hook creation can occur from multiple
/// VCE session threads, while each backend mutates shared process code or tables.
/// </summary>
internal static class HookManager
{
    internal static Lock HookEnableSyncRoot { get; } = new();
}
