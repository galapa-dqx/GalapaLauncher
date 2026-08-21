using Avalonia;

namespace Galapa.Launcher.Theming;

internal static class ThemeAvaloniaValues
{
    public static Thickness ToThickness(this ThemeEdges value) =>
        new(value.Left, value.Top, value.Right, value.Bottom);

    public static Thickness ToThickness(this ThemeThickness value) =>
        new(value.Left, value.Top, value.Right, value.Bottom);
}
