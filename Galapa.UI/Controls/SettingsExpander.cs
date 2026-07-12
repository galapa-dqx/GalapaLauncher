using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Galapa.UI.Controls;

/// <summary>
/// A settings card that can be expanded to show additional content.
/// </summary>
public class SettingsExpander : SettingsCard
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsExpander, bool>(nameof(IsExpanded));

    public static readonly StyledProperty<object?> ExpanderContentProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(ExpanderContent));

    /// <summary>
    /// Gets or sets whether the expander is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>
    /// Gets or sets the content shown when the expander is expanded.
    /// </summary>
    public object? ExpanderContent
    {
        get => GetValue(ExpanderContentProperty);
        set => SetValue(ExpanderContentProperty, value);
    }

    private ToggleButton? _expandButton;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _expandButton = e.NameScope.Find<ToggleButton>("PART_ExpandButton");
        if (_expandButton != null)
        {
            _expandButton.IsChecked = IsExpanded;
            _expandButton.Click += (_, _) => IsExpanded = _expandButton.IsChecked ?? false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandedProperty)
        {
            PseudoClasses.Set(":expanded", IsExpanded);
            if (_expandButton != null)
            {
                _expandButton.IsChecked = IsExpanded;
            }
        }
    }
}
