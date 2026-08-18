using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Galapa.Launcher.Views.Controls;

/// <summary>The common two-column content/aside layout, including its real scrollbar.</summary>
public class SplitPageShell : ContentControl
{
    public static readonly StyledProperty<object?> HelpContentProperty =
        AvaloniaProperty.Register<SplitPageShell, object?>(nameof(HelpContent));
    public static readonly StyledProperty<Thickness> ShellMarginProperty =
        AvaloniaProperty.Register<SplitPageShell, Thickness>(nameof(ShellMargin));
    public static readonly StyledProperty<Thickness> AsidePaddingProperty =
        AvaloniaProperty.Register<SplitPageShell, Thickness>(nameof(AsidePadding), new Thickness(15));
    public static readonly StyledProperty<bool> UseThemeAsidePaddingProperty =
        AvaloniaProperty.Register<SplitPageShell, bool>(nameof(UseThemeAsidePadding), true);
    public static readonly StyledProperty<double> AsideWidthProperty =
        AvaloniaProperty.Register<SplitPageShell, double>(nameof(AsideWidth), 280);

    public object? HelpContent
    {
        get => GetValue(HelpContentProperty);
        set => SetValue(HelpContentProperty, value);
    }

    public Thickness ShellMargin
    {
        get => GetValue(ShellMarginProperty);
        set => SetValue(ShellMarginProperty, value);
    }

    public Thickness AsidePadding
    {
        get => GetValue(AsidePaddingProperty);
        set => SetValue(AsidePaddingProperty, value);
    }

    public bool UseThemeAsidePadding
    {
        get => GetValue(UseThemeAsidePaddingProperty);
        set => SetValue(UseThemeAsidePaddingProperty, value);
    }

    public double AsideWidth
    {
        get => GetValue(AsideWidthProperty);
        set => SetValue(AsideWidthProperty, value);
    }
}

/// <summary>The settings spelling of the shared split-page shell.</summary>
public sealed class SettingsPageShell : SplitPageShell;

/// <summary>The single app-owned spelling of a compiled themed panel.</summary>
public sealed class ThemedPanel : ThemePart;

/// <summary>The single focus-indicator surface used by themed templates.</summary>
public sealed class ThemeFocusRing : Border;

/// <summary>The ornamented login action shared by both credential pages.</summary>
public sealed class PlayRow : ContentControl
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<PlayRow, ICommand?>(nameof(Command));
    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<PlayRow, object?>(nameof(CommandParameter));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
