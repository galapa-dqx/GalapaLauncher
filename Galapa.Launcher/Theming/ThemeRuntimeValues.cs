using Avalonia.Media;

namespace Galapa.Launcher.Theming;

public static class ThemeTypography
{
    public static FontStyle ToFontStyle(string? value) => value switch
    {
        "italic" => FontStyle.Italic,
        "oblique" => FontStyle.Oblique,
        _ => FontStyle.Normal
    };

    public static string ToTransform(string? value) => value switch
    {
        "uppercase" => "Upper",
        "lowercase" => "Lower",
        "capitalize" => "Title",
        _ => "Original"
    };
}

public static class ThemePaint
{
    public static Color Color(ThemeColor value) =>
        Avalonia.Media.Color.FromArgb(value.A, value.R, value.G, value.B);

    public static IBrush? Brush(ThemeColor? value) =>
        value is { } color ? new SolidColorBrush(Color(color)) : null;
}
