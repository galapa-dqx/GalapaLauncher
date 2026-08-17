using Avalonia.Media;
using Galapa.Launcher.ViewModels.LoginFrame;

namespace Galapa.Launcher.ViewModels.AppFrame;

public class HomePageViewModel(LoginFrameViewModel loginFrameViewModel) : AppPageViewModel
{
    public LoginFrameViewModel LoginFrameViewModel { get; set; } = loginFrameViewModel;

    public IReadOnlyList<HomeNewsItem> News { get; } =
    [
        new("events", "[Super Convenient Tool] Astoltia Birthday Celebration: Summer Special Campaign 2026", "Aug 1", "#a96beb"),
        new("events", "Chatty Drackyma's Astoltia 14th Anniversary Countdown!", "Jul 31", "#a96beb"),
        new("updates", "[DQX Shop] 14th Anniversary Grand Thanksgiving!", "July 30", "#20ae77"),
        new("maintenance", "Notice Regarding Payment Service Restoration (7/30)", "July 29", "#e12b3f"),
        new("news", "[DQXTV] Notice Regarding the Postponement of the \"Super Dragon Quest X TV 14th Anniversary Eve Festival\"", "July 28", "#5e63e2"),
        new("updates", "[iOS] Dragon Quest X Adventurer's Handy Tool (Ver. 8.0.1)", "July 27", "#20ae77"),
        new("events", "Super Summer Scoop-Off! Screenshot Contest Now Open", "July 26", "#a96beb"),
        new("maintenance", "Scheduled Maintenance Completion Notice (7/25)", "July 25", "#e12b3f")
    ];
}

public sealed record HomeNewsItem(string Category, string Title, string Date, string MarkerColor)
{
    public IBrush MarkerBrush { get; } = new SolidColorBrush(Color.Parse(MarkerColor));
}
