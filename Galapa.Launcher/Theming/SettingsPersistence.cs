using System.Threading;
using System.Threading.Tasks;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.Theming;

/// <summary>
/// Persists <see cref="Settings"/> to disk; injectable so callers can be tested without touching the file system.
/// </summary>
public interface ISettingsPersistence
{
    /// <summary>
    /// Saves a snapshot of <paramref name="settings"/> taken at call time, atomically replacing the settings file.
    /// </summary>
    Task SaveAsync(Settings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="ISettingsPersistence"/> that delegates to <see cref="Settings.SaveAsync"/>.
/// </summary>
public sealed class SettingsPersistence : ISettingsPersistence
{
    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(cancellationToken);
}
