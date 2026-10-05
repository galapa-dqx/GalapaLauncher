using Galapa.Core.Configuration;
using Galapa.Core.Game;
using Galapa.Launcher.ViewModels.Editing;
using Galapa.Launcher.ViewModels.OnboardingFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Galapa.TestUtilities;

namespace Galapa.Launcher.Tests.Editing;

public class InstallFolderEditorTests : IDisposable
{
    private const string SessionId = "0123456789abcdef0123456789abcdef0123456789abcdef01234567";

    private readonly FakeGameInstall _original = new();
    private readonly FakeGameInstall _replacement = new();
    private readonly TempDirectory _notAnInstall = new();
    private readonly FakeSettingsPersistence _persistence = new();
    private readonly Settings _settings;

    public InstallFolderEditorTests()
    {
        this._settings = new Settings { GameFolderPath = this._original.Path, ErrorReporting = false };
    }

    public void Dispose()
    {
        this._original.Dispose();
        this._replacement.Dispose();
        this._notAnInstall.Dispose();
    }

    private InstallFolderEditor CreateEditor(FakeFolderPicker picker) => new(this._settings, this._persistence, picker);

    [Fact]
    public async Task PickingAValidFolder_CommitsItImmediately()
    {
        var picker = new FakeFolderPicker(this._replacement.Path);
        using var editor = this.CreateEditor(picker);

        await editor.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(this._replacement.Path, this._settings.GameFolderPath);
        Assert.Equal(this._replacement.Path, editor.Field.Draft);
        Assert.Equal(1, this._persistence.SaveCount);
    }

    [Fact]
    public async Task PickerOpensAtTheCommittedFolder()
    {
        var picker = new FakeFolderPicker(null);
        using var editor = this.CreateEditor(picker);

        await editor.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(this._original.Path, picker.LastStartLocation);
    }

    [Fact]
    public async Task CancellingThePicker_ChangesNothing()
    {
        var picker = new FakeFolderPicker(null);
        using var editor = this.CreateEditor(picker);

        await editor.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(this._original.Path, editor.Field.Draft);
        Assert.Equal(this._original.Path, this._settings.GameFolderPath);
        Assert.Equal(0, this._persistence.SaveCount);
    }

    [Fact]
    public async Task PickingAnInvalidFolder_IsRejectedLikeTypedInput()
    {
        var picker = new FakeFolderPicker(this._notAnInstall.Path);
        using var editor = this.CreateEditor(picker);

        await editor.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("DQXGame.exe does not exist", editor.Field.Error);
        Assert.Equal(this._notAnInstall.Path, editor.Field.Draft);
        Assert.Equal(this._original.Path, this._settings.GameFolderPath);
        Assert.Equal(0, this._persistence.SaveCount);
    }

    [Fact]
    public async Task CommittedFolder_UpdatesEveryDependentPath()
    {
        var picker = new FakeFolderPicker(this._replacement.Path);
        using var editor = this.CreateEditor(picker);
        using var tracker = new PropertyChangedTracker(editor);
        var process = new GameProcess(this._settings) { SessionId = SessionId };

        await editor.BrowseCommand.ExecuteAsync(null);

        var expectedExecutable = Path.Combine(this._replacement.Path, "Game", "DQXGame.exe");
        Assert.True(tracker.WasPropertyChanged(nameof(InstallFolderEditor.ExecutablePath)));
        Assert.Equal(expectedExecutable, editor.ExecutablePath);
        Assert.Equal(expectedExecutable, this._settings.InstallRoot!.ExecutablePath);
        Assert.Equal(Path.Combine(this._replacement.Path, "Game"), process.WorkingDirectory);
        Assert.StartsWith($"\"{expectedExecutable}\"", process.BuildCommandLine());
    }

    [Fact]
    public async Task InvalidDraft_DoesNotAffectLaunching()
    {
        var picker = new FakeFolderPicker(this._notAnInstall.Path);
        using var editor = this.CreateEditor(picker);
        var process = new GameProcess(this._settings) { SessionId = SessionId };

        await editor.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(this._original.Path, "Game"), process.WorkingDirectory);
    }

    [Fact]
    public void GameSettingsPage_EditsTheInstallFolder()
    {
        var page = new GameSettingsPageViewModel(this._settings, this._persistence, new FakeFolderPicker(null));

        Assert.Equal(this._original.Path, page.InstallFolder.Field.Draft);
        Assert.Contains(@"Game\DQXGame.exe", page.InstallFolder.Help);
    }

    [Fact]
    public async Task OnboardingPage_CanContinueOnlyWithAValidCommittedFolder()
    {
        var settings = new Settings { ErrorReporting = false };
        var page = new GameFolderPageViewModel(settings, this._persistence, new FakeFolderPicker(this._replacement.Path));
        Assert.False(page.CanContinue);

        await page.InstallFolder.BrowseCommand.ExecuteAsync(null);

        Assert.True(page.CanContinue);
    }
}
