using Talon.Network;

namespace Talon.Tests;

public sealed class NetworkHooksTests
{
    [Theory]
    [InlineData(false, true, 0, 1, true)]
    [InlineData(true, true, 0, 1, false)]
    [InlineData(false, false, 0, 1, false)]
    [InlineData(false, true, 1, 1, false)]
    [InlineData(false, true, 0, 0, false)]
    public void PayloadHoldRequiresLiveTypeZeroParserFrame(
        bool replaying,
        bool parsing,
        byte frameType,
        int length,
        bool expected)
    {
        Assert.Equal(
            expected,
            NetworkHooks.ShouldHoldPayload(replaying, parsing, frameType, (nint)1, length));
    }
}
