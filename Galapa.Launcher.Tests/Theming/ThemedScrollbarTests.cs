using Avalonia.Controls.Primitives;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

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
    }
}
