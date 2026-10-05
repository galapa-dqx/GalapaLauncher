using Avalonia;
using Avalonia.VisualTree;

namespace Galapa.Launcher.Input;

/// <summary>
/// Attached property that declares which <see cref="NavigationContext"/> a control (and its
/// descendants) belongs to. Attach it to a frame's root, or to controls that logically belong
/// to a frame but live elsewhere in the visual tree, such as title-bar tabs.
/// </summary>
public static class NavigationScope
{
    public static readonly AttachedProperty<NavigationContext?> ContextProperty =
        AvaloniaProperty.RegisterAttached<Visual, NavigationContext?>("Context", typeof(NavigationScope));

    public static NavigationContext? GetContext(Visual element) => element.GetValue(ContextProperty);

    public static void SetContext(Visual element, NavigationContext? value) => element.SetValue(ContextProperty, value);

    /// <summary>
    /// Finds the nearest declared context starting at <paramref name="element"/> and walking
    /// up through visual parents (falling back to logical parents across popup boundaries).
    /// </summary>
    public static NavigationContext? Find(Visual? element)
    {
        for (var current = element; current != null; current = ParentOf(current))
        {
            var context = GetContext(current);
            if (context != null)
                return context;
        }

        return null;
    }

    private static Visual? ParentOf(Visual element)
    {
        return element.GetVisualParent() ?? (element as StyledElement)?.Parent as Visual;
    }
}
