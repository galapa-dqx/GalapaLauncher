using System.Diagnostics.CodeAnalysis;

namespace Galapa.Core.Configuration;

/// <summary>
///     The folder where DQX keeps its configuration: dqxPlayerList.xml, <c>save\Conf</c>, <c>work\*.ini</c>,
///     <c>Players\NN</c>, <c>save\PadConf</c> and so on.
/// </summary>
/// <remarks>
///     The game hardcodes this as <c>Documents\My Games\Dragon Quest X</c>, so it is derived rather than configured and
///     never shown to users. <see cref="Location" /> can be overridden for tests and developer tooling only.
///     Not to be confused with <see cref="InstallRoot" />, where the game executable lives.
/// </remarks>
public static class SaveRoot
{
    private static string? _override;

    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games",
        "Dragon Quest X");

    /// <summary>
    ///     The DQX save folder. Assign a path to redirect it (tests, tooling) or <c>null</c> to restore <see cref="Default" />.
    /// </summary>
    public static string Location
    {
        get => _override ?? Default;
        [param: AllowNull] set => _override = value;
    }
}
