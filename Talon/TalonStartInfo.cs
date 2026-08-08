namespace Talon;

/// <summary>Contains versioned startup options supplied by Talon.Injector.</summary>
public sealed class TalonStartInfo
{
    /// <summary>Gets the bootstrap schema version.</summary>
    public int Version { get; init; }
    /// <summary>Gets the root directory for loose VFS replacements.</summary>
    public string? OverrideDirectory { get; init; }
    /// <summary>Gets whether VFS path census logging is enabled.</summary>
    public bool VfsCensus { get; init; }
}
