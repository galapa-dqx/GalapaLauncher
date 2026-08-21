using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace Galapa.UI.Controls;

/// <summary>
/// Displays a view-model string through a theme-selected culture-aware casing transform.
/// Editable controls intentionally do not use this class.
/// </summary>
public sealed class ThemedTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<ThemedTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<string> TextTransformProperty =
        AvaloniaProperty.Register<ThemedTextBlock, string>(nameof(TextTransform), "Original");

    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string TextTransform { get => GetValue(TextTransformProperty); set => SetValue(TextTransformProperty, value); }

    static ThemedTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<ThemedTextBlock>((control, _) => control.UpdateText());
        TextTransformProperty.Changed.AddClassHandler<ThemedTextBlock>((control, _) => control.UpdateText());
    }

    private void UpdateText()
    {
        var value = SourceText ?? string.Empty;
        var culture = CultureInfo.CurrentCulture;
        Text = TextTransform switch
        {
            "Upper" => value.ToUpper(culture),
            "Lower" => value.ToLower(culture),
            "Title" => culture.TextInfo.ToTitleCase(value.ToLower(culture)),
            _ => value
        };
    }
}
