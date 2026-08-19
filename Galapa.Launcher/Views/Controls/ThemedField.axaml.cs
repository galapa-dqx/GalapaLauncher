using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Galapa.Launcher.Views.Controls;

public partial class ThemedField : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ThemedField, string?>(nameof(Label));
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ThemedField, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<ThemedField, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<char> PasswordCharProperty =
        AvaloniaProperty.Register<ThemedField, char>(nameof(PasswordChar));

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public char PasswordChar { get => GetValue(PasswordCharProperty); set => SetValue(PasswordCharProperty, value); }

    public ThemedField()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, FocusEditorFromFrame, RoutingStrategies.Tunnel);
    }

    private void FocusEditorFromFrame(object? sender, PointerPressedEventArgs args)
    {
        if (!IsEffectivelyEnabled || args.Source is not Visual source) return;
        var editor = this.FindControl<TextBox>("PART_Editor");
        if (editor is null || ReferenceEquals(source, editor) || source.FindAncestorOfType<TextBox>() == editor) return;
        editor.Focus(NavigationMethod.Pointer);
    }
}
