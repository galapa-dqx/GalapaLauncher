using System.ComponentModel.DataAnnotations;

namespace Galapa.Core.Configuration;

/// <summary>
///     The folder Dragon Quest X is installed in: the parent of its <c>Boot</c> and <c>Game</c> folders. This is the only
///     DQX path a user chooses, and every path into the installation is derived from it here.
/// </summary>
/// <remarks>
///     Not to be confused with <see cref="SaveRoot" />, where DQX keeps its configuration. The install root is only read
///     when the game is launched.
/// </remarks>
public sealed record InstallRoot(string Location)
{
    public const string ExecutableName = "DQXGame.exe";

    /// <summary>
    ///     The <c>Game</c> folder, which holds the game executable and is the game process's working directory.
    /// </summary>
    public string GameDirectory => Path.Combine(this.Location, "Game");

    /// <summary>
    ///     The game executable the launcher starts.
    /// </summary>
    public string ExecutablePath => Path.Combine(this.GameDirectory, ExecutableName);

    /// <summary>
    ///     The executable's path relative to the install root, for display and help text.
    /// </summary>
    public static string RelativeExecutablePath => Path.Combine("Game", ExecutableName);

    /// <summary>
    ///     Checks that <paramref name="location" /> exists and contains the game executable.
    /// </summary>
    public static ValidationResult Validate(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
            return new ValidationResult("Folder does not exist");
        if (!File.Exists(new InstallRoot(location).ExecutablePath))
            return new ValidationResult($"{ExecutableName} does not exist");

        return ValidationResult.Success!;
    }
}
