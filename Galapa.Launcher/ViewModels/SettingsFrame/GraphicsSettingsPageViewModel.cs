using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

public sealed partial class GraphicsSettingItem(
    string label,
    string? value,
    bool isToggle,
    bool isChecked,
    string helpTitle,
    string helpBody) : ObservableObject
{
    public string Label { get; } = label;
    public string? Value { get; } = value;
    public bool IsToggle { get; } = isToggle;
    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private bool _isSelected;
    public string HelpTitle { get; } = helpTitle;
    public string HelpBody { get; } = helpBody;
}

public sealed partial class GraphicsSettingsPageViewModel : SettingsFramePageViewModel
{
    [ObservableProperty] private GraphicsSettingItem _selectedSetting;

    public GraphicsSettingsPageViewModel()
    {
        Settings =
        [
            new("Screen Mode", "Borderless Windowed", false, false, "Screen Mode",
                "Controls how the game occupies your screen — whether it takes exclusive control of the display or runs as a window managed by your desktop.\n\nFullscreen — The game takes exclusive control of the display and can set its own resolution and refresh rate. Lowest input latency and most reliable G-Sync/FreeSync support, but alt-tabbing is slow and overlays may not work.\n\nBorderless Windowed (Recommended) — A window with no borders filling the screen, locked to your desktop's resolution and refresh rate. Instant alt-tabbing and reliable overlays, with a slight performance cost on older systems.\n\nWindowed — A standard resizable window on your desktop. Easiest for multitasking, but you lose screen space to the title bar and desktop."),
            new("Screen Resolution", "1920x1080", false, false, "Screen Resolution",
                "The resolution the game renders at in fullscreen mode. In borderless and windowed modes the game follows your desktop resolution.\n\nHigher values are sharper but cost GPU time; use your monitor's native resolution unless you need the extra headroom."),
            new("Screen Brightness", "100%", false, false, "Screen Brightness",
                "Adjusts the brightness of the game image only — your monitor and desktop are unaffected.\n\nCalibrate so the darkest symbol on the calibration screen is just barely visible."),
            new("Vsync", null, true, true, "Vsync",
                "Synchronizes frame delivery with your monitor's refresh rate to eliminate tearing.\n\nIt adds a small amount of input latency. If you have a G-Sync or FreeSync display, you may prefer to cap the framerate instead."),
            new("Ignore power settings while running", null, true, false, "Ignore power settings while running",
                "Keeps the game running at full performance when your device switches to a power-saving plan.\n\nThis uses more battery, but prevents sudden framerate drops."),
            new("Text Rendering", "IMM32", false, false, "Text Rendering",
                "Selects the input method framework used for chat and text entry.\n\nIMM32 offers broad compatibility; TSF enables richer language features on modern systems."),
            new("Framerate Limit", "60 fps", false, false, "Framerate Limit",
                "Caps how many frames the game renders per second.\n\nMatching your monitor's refresh rate keeps frame pacing smooth and reduces heat and fan noise.")
        ];
        _selectedSetting = Settings[0];
        _selectedSetting.IsSelected = true;
    }

    public IReadOnlyList<GraphicsSettingItem> Settings { get; }
    public string HelpTitle => SelectedSetting.HelpTitle;
    public string HelpBody => SelectedSetting.HelpBody;

    [RelayCommand]
    private void Select(GraphicsSettingItem setting)
    {
        if (ReferenceEquals(setting, SelectedSetting)) return;
        SelectedSetting.IsSelected = false;
        setting.IsSelected = true;
        SelectedSetting = setting;
        OnPropertyChanged(nameof(HelpTitle));
        OnPropertyChanged(nameof(HelpBody));
    }
}
