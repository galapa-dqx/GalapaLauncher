using Talon;

namespace Talon.Tests;

public sealed class EntryPointTests
{
    [Fact]
    public unsafe void CommitsOnlyPendingNativeInitialization()
    {
        var pending = 0;
        var cancelled = 2;

        Assert.True(EntryPoint.TryCommitInitialization((nint)(&pending)));
        Assert.Equal(1, pending);
        Assert.False(EntryPoint.TryCommitInitialization((nint)(&pending)));
        Assert.False(EntryPoint.TryCommitInitialization((nint)(&cancelled)));
        Assert.Equal(2, cancelled);
    }
}
