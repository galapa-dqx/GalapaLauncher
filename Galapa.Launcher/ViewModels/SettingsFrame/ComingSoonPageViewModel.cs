namespace Galapa.Launcher.ViewModels.SettingsFrame;

public sealed class ComingSoonPageViewModel(string title, string description, string icon) : SettingsFramePageViewModel
{
    public override string Title { get; } = title;
    public override string Icon { get; } = icon;
    public string Description { get; } = description;
}
