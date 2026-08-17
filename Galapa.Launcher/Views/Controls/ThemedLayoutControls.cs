using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Galapa.Launcher.Views.Controls;

/// <summary>The common two-column settings layout, including its real scrollbar.</summary>
public sealed class SettingsPageShell : ContentControl
{
    public static readonly StyledProperty<object?> HelpContentProperty =
        AvaloniaProperty.Register<SettingsPageShell, object?>(nameof(HelpContent));

    public object? HelpContent
    {
        get => GetValue(HelpContentProperty);
        set => SetValue(HelpContentProperty, value);
    }
}

/// <summary>The single app-owned spelling of a compiled themed panel.</summary>
public sealed class ThemedPanel : ThemePart;

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
