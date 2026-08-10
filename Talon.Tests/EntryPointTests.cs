using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Version\":1}")]
    public unsafe void InitializationFailureAlwaysReleasesGameThread(string json)
    {
        using var cancellationEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        var jsonPointer = Marshal.StringToCoTaskMemUTF8(json);
        var state = 0;
        var continueEvent = (nint)0x1234;
        var signalled = new List<nint>();
        try
        {
            EntryPoint.InitializeCore(
                jsonPointer,
                continueEvent,
                cancellationEvent.SafeWaitHandle.DangerousGetHandle(),
                (nint)(&state),
                static (_, _) => throw new InvalidOperationException("test preparation failure"),
                static (_, _) => { },
                signalled.Add);

            Assert.Equal(2, state);
            Assert.Equal(continueEvent, signalled[^1]);
            Assert.Single(signalled, handle => handle == continueEvent);
        }
        finally { Marshal.FreeCoTaskMem(jsonPointer); }
    }

    [Fact]
    public unsafe void NativeCancellationAlwaysReleasesGameThread()
    {
        using var cancellationEvent = new EventWaitHandle(true, EventResetMode.ManualReset);
        var jsonPointer = Marshal.StringToCoTaskMemUTF8("{\"Version\":1}");
        var state = 2;
        var continueEvent = (nint)0x5678;
        var signalled = new List<nint>();
        try
        {
            EntryPoint.InitializeCore(
                jsonPointer,
                continueEvent,
                cancellationEvent.SafeWaitHandle.DangerousGetHandle(),
                (nint)(&state),
                static (_, _) => throw new InvalidOperationException("must not prepare"),
                static (_, _) => { },
                signalled.Add);

            Assert.Equal(2, state);
            Assert.Equal([continueEvent], signalled);
        }
        finally { Marshal.FreeCoTaskMem(jsonPointer); }
    }
}
