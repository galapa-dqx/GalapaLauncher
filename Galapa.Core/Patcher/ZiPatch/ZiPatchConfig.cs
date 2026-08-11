using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch;

/// <summary>
/// Mutable state threaded through a patch apply. A handful of chunks write into it
/// (TargetInfo sets <see cref="Platform"/>, ApplyOption sets the ignore flags); the
/// rest read <see cref="GamePath"/> and <see cref="Store"/> to do their work.
/// </summary>
public sealed class ZiPatchConfig(string gamePath)
{
    public enum PlatformId : ushort
    {
        Win32 = 0,  // .win32
        Cafe = 1,   // .cafe  (Wii U) - id unconfirmed
        Orbis = 2,  // .orbis (PS4)   - id unconfirmed
        Unknown = 3,
    }

    public string GamePath { get; } = gamePath;
    public PlatformId Platform { get; set; }
    public bool IgnoreMissing { get; set; }
    public bool IgnoreOldMismatch { get; set; }
    public SqexFileStreamStore? Store { get; set; }
}
