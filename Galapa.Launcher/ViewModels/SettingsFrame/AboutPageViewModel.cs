using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
///     Represents a third-party library shown on the About page.
/// </summary>
/// <param name="Name">The display name of the library.</param>
/// <param name="Url">The URL to the library's homepage or repository.</param>
public record LibraryInfo(string Name, string Url);

/// <summary>
///     ViewModel for the About page, showing version info, project links, and credits.
/// </summary>
public partial class AboutPageViewModel : SettingsFramePageViewModel
{
    public const string RepositoryUrl = "https://github.com/dqx-tools/galapalauncher";

    public override string Title => "About";
    public override string Icon => "/Assets/Icons/info-circle.svg";

    /// <summary>
    ///     The application version, e.g. "1.0.0".
    /// </summary>
    public string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown";

    /// <summary>
    ///     The short git commit the build was produced from, if available.
    /// </summary>
    public string Commit { get; } = ThisAssembly.Git.Commit;

    /// <summary>
    ///     The combined version line shown to the user.
    /// </summary>
    public string VersionDisplay => $"Version: {Version} ({Commit})";

    /// <summary>
    ///     Text copied to the clipboard when the user clicks "Copy".
    /// </summary>
    public string CopyText => $"GalapaLauncher {Version} (commit {Commit})";

    /// <summary>
    ///     The third-party libraries used by the launcher.
    /// </summary>
    public IReadOnlyList<LibraryInfo> Libraries { get; } =
    [
        new("Avalonia", "https://avaloniaui.net"),
        new("CommunityToolkit.Mvvm", "https://github.com/CommunityToolkit/dotnet"),
        new("DryIoc", "https://github.com/dadhi/DryIoc"),
        new("SDL3-CS", "https://github.com/edwardgushchin/SDL3-CS"),
        new("Sentry", "https://sentry.io"),
        new("Serilog", "https://serilog.net"),
        new("Svg.Skia", "https://github.com/wieslawsoltes/Svg.Skia"),
        new("Velopack", "https://velopack.io"),
        new("Vortice.Windows", "https://github.com/amerkoleci/Vortice.Windows")
    ];

    /// <summary>
    ///     Opens a URL in the user's default browser.
    /// </summary>
    [RelayCommand]
    private void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    ///     Opens the launcher's data folder in the file explorer.
    /// </summary>
    [RelayCommand]
    private void OpenLogLocation()
    {
        Process.Start(new ProcessStartInfo(Paths.AppData) { UseShellExecute = true });
    }
}
