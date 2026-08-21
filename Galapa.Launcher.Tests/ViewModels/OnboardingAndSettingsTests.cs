using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.OnboardingFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Tests.Theming;
using Galapa.TestUtilities;

namespace Galapa.Launcher.Tests.ViewModels;

[Collection("Sequential")]
public sealed class OnboardingAndSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public OnboardingAndSettingsTests() => Paths.AppData = _temp.Path;

    public void Dispose()
    {
        Paths.AppData = null;
        _temp.Dispose();
    }

    [Fact]
    public void AppFrame_HidesNormalNavigationWhenGameFolderIsInvalid()
    {
        var settings = new Settings { GameFolderPath = Path.Combine(_temp.Path, "missing") };
        var onboarding = new OnboardingFrameViewModel(settings, new SettingsPersistence());
        var vm = new AppFrameViewModel(settings, new Lazy<HomePageViewModel>(() => null!),
            new Lazy<SettingsPageViewModel>(() => null!), onboarding);

        Assert.True(vm.IsOnboarding);
        Assert.Equal(2, vm.Pages.Count);
    }

    [Fact]
    public async Task CompletingOnboardingPersistsFolderAndRevealsNavigation()
    {
        var folder = Path.Combine(_temp.Path, "DQX");
        Directory.CreateDirectory(Path.Combine(folder, "Game"));
        File.WriteAllText(Path.Combine(folder, "Game", "DQXGame.exe"), string.Empty);
        var settings = new Settings { SaveFolderPath = _temp.Path, ErrorReporting = false };
        var onboarding = new OnboardingFrameViewModel(settings, new SettingsPersistence());
        var app = new AppFrameViewModel(settings, new Lazy<HomePageViewModel>(() => null!),
            new Lazy<SettingsPageViewModel>(() => null!), onboarding);

        onboarding.GameFolderPath = folder;
        await onboarding.CompleteCommand.ExecuteAsync(null);

        Assert.False(app.IsOnboarding);
        Assert.Equal(folder, Settings.Load().GameFolderPath);
    }

    [Fact]
    public async Task OnboardingSaveFailureRetainsThePreviousFolderAndDoesNotComplete()
    {
        var folder = Path.Combine(_temp.Path, "DQX");
        Directory.CreateDirectory(Path.Combine(folder, "Game"));
        File.WriteAllText(Path.Combine(folder, "Game", "DQXGame.exe"), string.Empty);
        var previous = Path.Combine(_temp.Path, "previous");
        var settings = new Settings { GameFolderPath = previous };
        var onboarding = new OnboardingFrameViewModel(settings, new SettingsPersistence()) { GameFolderPath = folder };
        var completed = false;
        onboarding.Completed += (_, _) => completed = true;
        var blockedSettingsPath = Path.Combine(_temp.Path, "not-a-directory");
        File.WriteAllText(blockedSettingsPath, string.Empty);
        Paths.AppData = blockedSettingsPath;

        await onboarding.CompleteCommand.ExecuteAsync(null);

        Assert.False(completed);
        Assert.Equal(previous, settings.GameFolderPath);
        Assert.Contains("could not save", onboarding.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsFrameDoesNotConstructPagesToReadNavigationMetadata()
    {
        var launcherConstructed = 0;
        var gameConstructed = 0;
        var graphicsConstructed = 0;
        var aboutConstructed = 0;
        var vm = new SettingsFrameViewModel(
            new Lazy<GeneralSettingsPageViewModel>(() => { launcherConstructed++; return null!; }),
            new Lazy<GameSettingsPageViewModel>(() => { gameConstructed++; return null!; }),
            new Lazy<GraphicsSettingsPageViewModel>(() => { graphicsConstructed++; return null!; }),
            new Lazy<AboutPageViewModel>(() => { aboutConstructed++; return null!; }));

        Assert.Equal(new[] { "Launcher", "Game", "Players", "Graphics", "Controls", "Sound", "Clarity", "About" },
            vm.Pages.Select(page => page.Title));
        Assert.Equal(0, launcherConstructed + gameConstructed + graphicsConstructed + aboutConstructed);

        _ = vm.SelectedPage!.ViewModel.Value;
        Assert.Equal(1, launcherConstructed);
        Assert.Equal(0, gameConstructed + graphicsConstructed + aboutConstructed);
    }

    [Fact]
    public void GameFolderHelpUsesTheConfiguredExecutableDisplayPath()
    {
        var vm = new GameSettingsPageViewModel(new Settings(), new SettingsPersistence());

        Assert.Contains(Settings.GameExecutableDisplayPath, vm.GameExecutableHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameFolderCommandUsesAsyncSettingsPersistenceAndSurfacesFailures()
    {
        var recording = new RecordingSettingsPersistence();
        var successful = new GameSettingsPageViewModel(new Settings(), recording);
        await successful.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, recording.SaveCount);
        Assert.Null(successful.SaveError);

        var failing = new GameSettingsPageViewModel(new Settings(),
            new FailingSettingsPersistence(new IOException("blocked")));
        await failing.SaveCommand.ExecuteAsync(null);
        Assert.Contains("blocked", failing.SaveError, StringComparison.Ordinal);
    }
}
