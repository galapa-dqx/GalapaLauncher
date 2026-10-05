using Galapa.Core.Configuration;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Services;

[Collection("Sequential")]
public class SettingsPersistenceTests : IDisposable
{
    private readonly TempDirectory _appData = new();
    private readonly FakeGameInstall _install = new();

    public SettingsPersistenceTests()
    {
        Paths.AppData = this._appData.Path;
    }

    public void Dispose()
    {
        Paths.AppData = null;
        this._appData.Dispose();
        this._install.Dispose();
    }

    private Settings ValidSettings() => new() { GameFolderPath = this._install.Path, ErrorReporting = false };

    [Fact]
    public async Task BurstOfSaves_ProducesOneWriteOfTheLatestValues()
    {
        var settings = this.ValidSettings();
        var persistence = new RecordingPersistence();

        settings.ErrorReporting = true;
        var first = persistence.SaveAsync(settings);
        settings.ErrorReporting = false;
        var second = persistence.SaveAsync(settings);
        settings.ErrorReporting = true;
        var third = persistence.SaveAsync(settings);

        await Task.WhenAll(first, second, third);

        Assert.Equal([true], persistence.Snapshots);
    }

    [Fact]
    public async Task SavesDuringAWrite_CoalesceIntoOneFollowingWrite()
    {
        var settings = this.ValidSettings();
        var gate = new TaskCompletionSource();
        var persistence = new RecordingPersistence { Gate = gate.Task };

        var first = persistence.SaveAsync(settings);
        await persistence.FirstWriteStarted.Task;

        settings.ErrorReporting = true;
        var second = persistence.SaveAsync(settings);
        var third = persistence.SaveAsync(settings);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal([false, true], persistence.Snapshots);
    }

    [Fact]
    public async Task FailedWrite_FaultsItsCallersButNotTheNextSave()
    {
        var settings = this.ValidSettings();
        var persistence = new RecordingPersistence { Failure = new IOException("disk full") };

        var failure = await Assert.ThrowsAsync<IOException>(() => persistence.SaveAsync(settings));
        Assert.Equal("disk full", failure.Message);

        persistence.Failure = null;
        await persistence.SaveAsync(settings);

        Assert.Equal(2, persistence.Snapshots.Count);
    }

    [Fact]
    public async Task InvalidSettings_AreRefusedAndNotWritten()
    {
        var settings = this.ValidSettings();
        settings.GameFolderPath = Path.Combine(this._appData.Path, "Missing");

        await Assert.ThrowsAsync<InvalidSettingsException>(() => new SettingsPersistence().SaveAsync(settings));

        Assert.False(File.Exists(Paths.Settings));
    }

    [Fact]
    public async Task ValidSettings_AreWritten()
    {
        await new SettingsPersistence().SaveAsync(this.ValidSettings());

        Assert.Equal(this._install.Path, Settings.Load().GameFolderPath);
    }

    private sealed class RecordingPersistence : SettingsPersistence
    {
        public List<bool?> Snapshots { get; } = [];
        public Task Gate { get; init; } = Task.CompletedTask;
        public Exception? Failure { get; set; }

        public TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task WriteAsync(Settings settings)
        {
            lock (this.Snapshots)
            {
                this.Snapshots.Add(settings.ErrorReporting);
            }

            FirstWriteStarted.TrySetResult();
            return this.CompleteAsync();
        }

        private async Task CompleteAsync()
        {
            await this.Gate;
            if (this.Failure is not null) throw this.Failure;
        }
    }
}
