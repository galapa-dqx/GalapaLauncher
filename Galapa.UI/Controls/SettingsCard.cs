using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Galapa.UI.Controls;

/// <summary>
/// A card control for displaying settings items with an icon, header, description, and action content.
/// </summary>
public class SettingsCard : TemplatedControl
{
    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Icon));

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Description));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(ActionContent));

    public static readonly StyledProperty<bool> IsClickEnabledProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsClickEnabled));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SettingsCard, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(CommandParameter));

    /// <summary>
    /// Gets or sets the icon displayed on the left side of the card.
    /// Can be a PathIcon Data string, or any control.
    /// </summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// Gets or sets the header text.
    /// </summary>
    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>
    /// Gets or sets the description text displayed below the header.
    /// </summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// Gets or sets the content displayed on the right side (e.g., a button, toggle, or icon).
    /// </summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the entire card is clickable.
    /// </summary>
    public bool IsClickEnabled
    {
        get => GetValue(IsClickEnabledProperty);
        set => SetValue(IsClickEnabledProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to execute when the card is clicked.
    /// </summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the parameter to pass to the command.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (IsClickEnabled && Command?.CanExecute(CommandParameter) == true)
        {
            Command.Execute(CommandParameter);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsClickEnabledProperty)
        {
            PseudoClasses.Set(":clickable", IsClickEnabled);
        }
    }
}
