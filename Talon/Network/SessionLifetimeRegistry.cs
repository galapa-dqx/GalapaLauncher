namespace Talon.Network;

// Prevents a VCE session from being destroyed while a held packet uses it.
internal sealed class SessionLifetimeRegistry
{
    private readonly object statesLock = new();
    private readonly Dictionary<nint, SessionState> states = [];

    public long GetGeneration(nint session)
    {
        var state = GetState(session);
        lock (state.Sync) return state.Generation;
    }

    public IDisposable? TryAcquireReplay(nint session, long generation)
    {
        var state = GetState(session);
        Monitor.Enter(state.Sync);
        if (state.Generation == generation)
            return new SessionLease(state.Sync);

        Monitor.Exit(state.Sync);
        return null;
    }

    public IDisposable AcquireDestruction(nint session)
    {
        var state = GetState(session);
        Monitor.Enter(state.Sync);
        state.Generation++;
        return new SessionLease(state.Sync);
    }

    private SessionState GetState(nint session)
    {
        lock (statesLock)
        {
            if (!states.TryGetValue(session, out var state))
                states.Add(session, state = new SessionState());
            return state;
        }
    }

    private sealed class SessionState
    {
        public object Sync { get; } = new();
        public long Generation { get; set; } = 1;
    }

    private sealed class SessionLease(object sync) : IDisposable
    {
        private object? heldSync = sync;

        public void Dispose()
        {
            var releasedSync = Interlocked.Exchange(ref heldSync, null);
            if (releasedSync is not null) Monitor.Exit(releasedSync);
        }
    }
}
