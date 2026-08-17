namespace Galapa.Launcher.ViewModels.SettingsFrame;

public sealed class ComingSoonPageViewModel(string title, string description) : SettingsFramePageViewModel
{
    public string Title { get; } = title;
    public string Description { get; } = description;
}
