using Talon.Network;

namespace Talon.Tests;

public sealed class SessionLifetimeRegistryTests
{
    [Fact]
    public void ConnectionRemainsReplayableUntilDestruction()
    {
        var registry = new SessionLifetimeRegistry();
        const nint session = 0x1234;
        var generation = registry.GetGeneration(session);

        using var firstReplay = registry.TryAcquireReplay(session, generation);
        Assert.NotNull(firstReplay);
        firstReplay.Dispose();

        using var secondReplay = registry.TryAcquireReplay(session, generation);
        Assert.NotNull(secondReplay);
    }

    [Fact]
    public async Task DestructionWaitsForActiveReplayAndInvalidatesItsGeneration()
    {
        var registry = new SessionLifetimeRegistry();
        const nint session = 0x1234;
        var generation = registry.GetGeneration(session);
        using var replay = registry.TryAcquireReplay(session, generation);
        Assert.NotNull(replay);

        using var attemptingDestruction = new ManualResetEventSlim();
        using var destroyed = new ManualResetEventSlim();
        var destruction = Task.Run(() =>
        {
            attemptingDestruction.Set();
            using var lease = registry.AcquireDestruction(session);
            destroyed.Set();
        });

        Assert.True(attemptingDestruction.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(destroyed.Wait(TimeSpan.FromMilliseconds(100)));

        replay.Dispose();
        Assert.True(destroyed.Wait(TimeSpan.FromSeconds(1)));
        await destruction;

        Assert.Equal(0, registry.StateCount);
        Assert.Null(registry.TryAcquireReplay(session, generation));
        Assert.Equal(0, registry.StateCount);
        Assert.NotEqual(generation, registry.GetGeneration(session));
    }
}
