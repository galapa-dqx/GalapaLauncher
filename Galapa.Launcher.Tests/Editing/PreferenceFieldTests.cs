using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.Editing;
using Galapa.TestUtilities;

namespace Galapa.Launcher.Tests.Editing;

[Collection("Sequential")]
public class PreferenceFieldTests : IDisposable
{
    private readonly TempDirectory _appData = new();
    private readonly FakeGameInstall _original = new();
    private readonly FakeGameInstall _replacement = new();
    private readonly FakeSettingsPersistence _persistence = new();
    private readonly Settings _settings;

    public PreferenceFieldTests()
    {
        // Only the refusing-writer test uses real persistence, but never let anything reach the real AppData.
        Paths.AppData = this._appData.Path;
        this._settings = new Settings { GameFolderPath = this._original.Path, ErrorReporting = false };
    }

    public void Dispose()
    {
        Paths.AppData = null;
        this._appData.Dispose();
        this._original.Dispose();
        this._replacement.Dispose();
    }

    private string MissingFolder => Path.Combine(this._appData.Path, "Missing");

    private PreferenceField<string?> CreateField(ISettingsPersistence? persistence = null, TimeSpan? debounce = null) =>
        new(this._settings,
            persistence ?? this._persistence,
            nameof(Settings.GameFolderPath),
            s => s.GameFolderPath,
            (s, value) => s.GameFolderPath = value,
            debounce ?? TimeSpan.Zero);

    [Fact]
    public void StartsFromTheModel()
    {
        using var field = this.CreateField();

        Assert.Equal(this._original.Path, field.Draft);
        Assert.Equal(this._original.Path, field.Committed);
        Assert.False(field.HasError);
    }

    [Fact]
    public async Task InvalidLoadedValue_IsShownOnTheRowUntilFixed()
    {
        this._settings.GameFolderPath = this.MissingFolder;
        using var field = this.CreateField();

        Assert.Equal("Folder does not exist", field.Error);

        await field.SubmitAsync(this._replacement.Path);

        Assert.Null(field.Error);
        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
    }

    [Fact]
    public void ExternalChangeToAnInvalidValue_IsShownOnTheRow()
    {
        using var field = this.CreateField();

        this._settings.GameFolderPath = this.MissingFolder;

        Assert.Equal("Folder does not exist", field.Error);
    }

    [Fact]
    public async Task ValidSubmit_CommitsIntoSettingsAndSaves()
    {
        using var field = this.CreateField();

        await field.SubmitAsync(this._replacement.Path);

        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
        Assert.Equal(this._replacement.Path, field.Committed);
        Assert.Equal([this._replacement.Path], this._persistence.SavedFolders);
        Assert.Null(field.Error);
        Assert.False(field.IsSaving);
    }

    [Fact]
    public async Task InvalidSubmit_NeverReachesSettings()
    {
        using var field = this.CreateField();

        await field.SubmitAsync(this.MissingFolder);

        Assert.Equal(this.MissingFolder, field.Draft);
        Assert.Equal("Folder does not exist", field.ValidationError);
        Assert.True(field.HasError);
        Assert.Equal(this._original.Path, this._settings.GameFolderPath);
        Assert.Equal(this._original.Path, field.Committed);
        Assert.False(this._settings.HasErrors);
        Assert.Equal(0, this._persistence.SaveCount);
    }

    [Fact]
    public async Task EmptySubmit_UsesTheRequiredRuleFromSettings()
    {
        using var field = this.CreateField();

        await field.SubmitAsync("");

        Assert.Equal("Choose the folder Dragon Quest X is installed in", field.ValidationError);
        Assert.Equal(this._original.Path, this._settings.GameFolderPath);
    }

    [Fact]
    public async Task ValidSubmitAfterInvalid_ClearsTheError()
    {
        using var field = this.CreateField();
        await field.SubmitAsync(this.MissingFolder);

        await field.SubmitAsync(this._replacement.Path);

        Assert.Null(field.Error);
        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
    }

    [Fact]
    public async Task SubmittingTheCommittedValue_DoesNotSave()
    {
        using var field = this.CreateField();

        await field.SubmitAsync(this._original.Path);

        Assert.Equal(0, this._persistence.SaveCount);
    }

    [Fact]
    public async Task TypedInput_IsDebouncedBeforeItCommits()
    {
        using var field = this.CreateField(debounce: TimeSpan.FromMilliseconds(50));

        field.Draft = this._replacement.Path;

        Assert.True(field.IsValidating);
        Assert.Equal(this._original.Path, this._settings.GameFolderPath);

        await WaitUntil(() => this._settings.GameFolderPath == this._replacement.Path);
        Assert.False(field.IsValidating);
        Assert.Equal(1, this._persistence.SaveCount);
    }

    [Fact]
    public async Task ImmediateSubmit_SupersedesPendingTypedInput()
    {
        using var field = this.CreateField(debounce: TimeSpan.FromMilliseconds(50));

        field.Draft = this.MissingFolder;
        await field.SubmitAsync(this._replacement.Path);
        await Task.Delay(150);

        Assert.Equal(this._replacement.Path, field.Draft);
        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
        Assert.Null(field.Error);
        Assert.False(field.IsValidating);
        Assert.Equal(1, this._persistence.SaveCount);
    }

    [Fact]
    public async Task PersistenceFailure_KeepsTheCommittedValueAndShowsTheError()
    {
        this._persistence.Failure = new IOException("The disk is full");
        using var field = this.CreateField();

        await field.SubmitAsync(this._replacement.Path);

        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
        Assert.Equal(this._replacement.Path, field.Committed);
        Assert.Equal("The disk is full", field.PersistenceError);
        Assert.Equal("The disk is full", field.Error);
        Assert.False(field.IsSaving);
    }

    [Fact]
    public async Task ResubmittingAfterAPersistenceFailure_RetriesTheSave()
    {
        this._persistence.Failure = new IOException("The disk is full");
        using var field = this.CreateField();
        await field.SubmitAsync(this._replacement.Path);

        this._persistence.Failure = null;
        await field.SubmitAsync(this._replacement.Path);

        Assert.Equal(2, this._persistence.SaveCount);
        Assert.Null(field.Error);
    }

    [Fact]
    public async Task WriterRefusal_IsShownOnTheRow()
    {
        // ErrorReporting was never set, so the settings are invalid as a whole even though the folder is fine.
        var settings = new Settings { GameFolderPath = this._original.Path };
        using var field = new PreferenceField<string?>(
            settings,
            new SettingsPersistence(),
            nameof(Settings.GameFolderPath),
            s => s.GameFolderPath,
            (s, value) => s.GameFolderPath = value,
            TimeSpan.Zero);

        await field.SubmitAsync(this._replacement.Path);

        Assert.Null(field.ValidationError);
        Assert.Contains("not saved", field.PersistenceError);
        Assert.False(File.Exists(Paths.Settings));
    }

    [Fact]
    public async Task ExternalModelChange_ReplacesTheDraftAndPendingEdits()
    {
        using var field = this.CreateField(debounce: TimeSpan.FromMilliseconds(50));
        field.Draft = this.MissingFolder;

        this._settings.GameFolderPath = this._replacement.Path;
        await Task.Delay(150);

        Assert.Equal(this._replacement.Path, field.Draft);
        Assert.Equal(this._replacement.Path, field.Committed);
        Assert.Null(field.Error);
        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
        Assert.Equal(0, this._persistence.SaveCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
