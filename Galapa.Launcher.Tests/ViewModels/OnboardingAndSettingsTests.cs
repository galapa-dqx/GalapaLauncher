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
    public void SettingsFrameContainsAllEightDestinations()
    {
        var vm = new SettingsFrameViewModel(
            new Lazy<GeneralSettingsPageViewModel>(() => null!),
            new Lazy<GameSettingsPageViewModel>(() => null!),
            new Lazy<AboutPageViewModel>(() => null!));

        Assert.Equal(8, vm.Pages.Count);
        Assert.Equal(new[] { "Players", "Graphics", "Controls", "Sound", "Clarity" },
            vm.Pages.Skip(2).Take(5).Select(x => x.Title));
        Assert.Same(vm.Pages[0], vm.SelectedPage);
    }
}
