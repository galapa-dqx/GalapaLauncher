namespace Talon.Injector;

// Keep this wire model in sync with Talon.TalonStartInfo.
internal sealed class TalonStartInfo
{
    public int Version { get; init; }
    public string? OverrideDirectory { get; init; }
    public string? PacketCapturePath { get; init; }
    public bool NetworkSmokeTest { get; init; }
    public bool VfsCensus { get; init; }
}
