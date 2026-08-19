using Galapa.TestUtilities;
using Xunit.Sdk;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class GoldenImageInfrastructureTests
{
    [Fact]
    public async Task FixtureUpdatesAreRejectedInCiBeforeWriting()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "must-not-exist.png");
        var previousUpdate = Environment.GetEnvironmentVariable("GALAPA_UPDATE_RENDER_FIXTURES");
        var previousCi = Environment.GetEnvironmentVariable("CI");
        try
        {
            Environment.SetEnvironmentVariable("GALAPA_UPDATE_RENDER_FIXTURES", "1");
            Environment.SetEnvironmentVariable("CI", "true");
            await Assert.ThrowsAsync<XunitException>(() =>
                GoldenImage.AssertMatchesOrUpdate(target, [1, 2, 3], "guard", "Infrastructure"));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GALAPA_UPDATE_RENDER_FIXTURES", previousUpdate);
            Environment.SetEnvironmentVariable("CI", previousCi);
        }
    }
}
