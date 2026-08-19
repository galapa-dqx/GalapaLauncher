using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.OnboardingFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
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
        var onboarding = new OnboardingFrameViewModel(settings);
        var vm = new AppFrameViewModel(settings, new Lazy<HomePageViewModel>(() => null!),
            new Lazy<SettingsPageViewModel>(() => null!), onboarding);

        Assert.True(vm.IsOnboarding);
        Assert.Equal(2, vm.Pages.Count);
    }

    [Fact]
    public void CompletingOnboardingPersistsFolderAndRevealsNavigation()
    {
        var folder = Path.Combine(_temp.Path, "DQX");
        Directory.CreateDirectory(Path.Combine(folder, "Game"));
        File.WriteAllText(Path.Combine(folder, "Game", "DQXGame.exe"), string.Empty);
        var settings = new Settings { SaveFolderPath = _temp.Path, ErrorReporting = false };
        var onboarding = new OnboardingFrameViewModel(settings);
        var app = new AppFrameViewModel(settings, new Lazy<HomePageViewModel>(() => null!),
            new Lazy<SettingsPageViewModel>(() => null!), onboarding);

        onboarding.GameFolderPath = folder;
        onboarding.CompleteCommand.Execute(null);

        Assert.False(app.IsOnboarding);
        Assert.Equal(folder, Settings.Load().GameFolderPath);
    }

    [Fact]
    public void OnboardingSaveFailureRetainsThePreviousFolderAndDoesNotComplete()
    {
        var folder = Path.Combine(_temp.Path, "DQX");
        Directory.CreateDirectory(Path.Combine(folder, "Game"));
        File.WriteAllText(Path.Combine(folder, "Game", "DQXGame.exe"), string.Empty);
        var previous = Path.Combine(_temp.Path, "previous");
        var settings = new Settings { GameFolderPath = previous };
        var onboarding = new OnboardingFrameViewModel(settings) { GameFolderPath = folder };
        var completed = false;
        onboarding.Completed += (_, _) => completed = true;
        var blockedSettingsPath = Path.Combine(_temp.Path, "not-a-directory");
        File.WriteAllText(blockedSettingsPath, string.Empty);
        Paths.AppData = blockedSettingsPath;

        onboarding.CompleteCommand.Execute(null);

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
}
