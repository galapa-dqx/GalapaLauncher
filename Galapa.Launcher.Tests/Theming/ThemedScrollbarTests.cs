using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

public sealed record ScrollbarInspection(
    double InitialMaximum,
    double InitialViewport,
    double FirstOffset,
    double ValueAfterTargetScroll,
    double ReplacementMaximum,
    double ValueAfterOldTargetScroll,
    double DetachedMaximum,
    bool DetachedIsEnabled,
    bool HasRangeAutomation,
    bool IsDirectionReversed,
    double ValueAfterPageUp,
    double ValueAfterPageDown);

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemedScrollbarTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task UsesAvaloniasScrollbarBehavior()
    {
        await skia.DispatchAsync(() =>
        {
            Assert.IsAssignableFrom<ScrollBar>(new ThemedScrollbar());
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task SynchronizesTargetsUnsubscribesAndRetainsRangeAutomation()
    {
        var result = await skia.InspectScrollbarAsync();

        Assert.Equal(300, result.InitialMaximum, 3);
        Assert.Equal(100, result.InitialViewport, 3);
        Assert.Equal(45, result.FirstOffset, 3);
        Assert.Equal(80, result.ValueAfterTargetScroll, 3);
        Assert.Equal(150, result.ReplacementMaximum, 3);
        Assert.Equal(0, result.ValueAfterOldTargetScroll, 3);
        Assert.Equal(0, result.DetachedMaximum, 3);
        Assert.False(result.DetachedIsEnabled);
        Assert.True(result.HasRangeAutomation);
        Assert.True(result.IsDirectionReversed);
        Assert.True(result.ValueAfterPageUp < 150);
        Assert.True(result.ValueAfterPageDown > result.ValueAfterPageUp);
    }
}

internal static class ThemedScrollbarScenarios
{
    public static Task<ScrollbarInspection> InspectScrollbarAsync(this SkiaHeadlessFixture skia) =>
        skia.DispatchAsync(InspectScrollbar);

    private static ScrollbarInspection InspectScrollbar()
    {
        _ = SkiaHeadlessFixture.EnsureThemeStyles();
        var first = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 100, Height = 400 }
        };
        var second = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 100, Height = 250 }
        };
        var bar = new ThemedScrollbar { Height = 100, Target = first };
        var host = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("100,100,20"),
            Children = { first, second, bar }
        };
        Grid.SetColumn(second, 1);
        Grid.SetColumn(bar, 2);
        return SkiaHeadlessFixture.WithWindow(host, new Size(220, 100), window =>
        {
            var initialMaximum = bar.Maximum;
            var initialViewport = bar.ViewportSize;
            var track = bar.GetVisualDescendants().OfType<Track>().Single();
            var pageUp = bar.GetVisualDescendants().OfType<RepeatButton>()
                .Single(button => button.Name == "PART_PageUpButton");
            var pageDown = bar.GetVisualDescendants().OfType<RepeatButton>()
                .Single(button => button.Name == "PART_PageDownButton");
            bar.Value = 150;
            pageUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterPageUp = bar.Value;
            pageDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterPageDown = bar.Value;
            bar.Value = 45;
            window.UpdateLayout();
            var firstOffset = first.Offset.Y;
            first.Offset = new Vector(0, 80);
            window.UpdateLayout();
            var valueAfterTargetScroll = bar.Value;
            bar.Target = second;
            window.UpdateLayout();
            var replacementMaximum = bar.Maximum;
            first.Offset = new Vector(0, 120);
            window.UpdateLayout();
            var valueAfterOldTargetScroll = bar.Value;
            var peer = ControlAutomationPeer.CreatePeerForElement(bar);
            var hasRangeAutomation = peer is IRangeValueProvider;
            bar.Target = null;
            window.UpdateLayout();
            var detachedMaximum = bar.Maximum;
            var detachedIsEnabled = bar.IsEnabled;
            return new ScrollbarInspection(initialMaximum, initialViewport, firstOffset, valueAfterTargetScroll,
                replacementMaximum, valueAfterOldTargetScroll, detachedMaximum, detachedIsEnabled, hasRangeAutomation,
                track.IsDirectionReversed, afterPageUp, afterPageDown);
        });
    }
}
