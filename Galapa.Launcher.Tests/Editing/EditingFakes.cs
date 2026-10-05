using Galapa.Core.Configuration;
using Galapa.Launcher.Services;

namespace Galapa.Launcher.Tests.Editing;

/// <summary>
///     Records saves without touching the disk; can be told to fail.
/// </summary>
internal sealed class FakeSettingsPersistence : ISettingsPersistence
{
    public int SaveCount { get; private set; }
    public List<string?> SavedFolders { get; } = [];
    public Exception? Failure { get; set; }

    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        this.SaveCount++;
        this.SavedFolders.Add(settings.GameFolderPath);
        return this.Failure is null ? Task.CompletedTask : Task.FromException(this.Failure);
    }
}

/// <summary>
///     Returns a canned folder (or null, as if cancelled) and records how it was asked.
/// </summary>
internal sealed class FakeFolderPicker(string? result) : IFolderPicker
{
    public string? Result { get; set; } = result;
    public int Calls { get; private set; }
    public string? LastStartLocation { get; private set; }

    public Task<string?> PickFolderAsync(
        string title,
        string? startLocation,
        CancellationToken cancellationToken = default)
    {
        this.Calls++;
        this.LastStartLocation = startLocation;
        return Task.FromResult(this.Result);
    }
}
