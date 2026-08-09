namespace Talon.Network;

// A VCE session is the library's native per-connection object. Keep it alive
// during replay and reject completions after that connection is destroyed.
internal sealed class SessionLifetimeRegistry
{
    private readonly object statesLock = new();
    private readonly Dictionary<nint, SessionState> states = [];
    private long nextGeneration;

    internal int StateCount
    {
        get { lock (statesLock) return states.Count; }
    }

    public long GetGeneration(nint session)
    {
        var state = GetState(session);
        lock (state.Sync) return state.Generation;
    }

    public IDisposable? TryAcquireReplay(nint session, long generation)
    {
        var state = TryGetState(session);
        if (state is null) return null;
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
        // Zero is never assigned to a live connection. It invalidates every
        // completion while the native destructor owns this gate.
        state.Generation = 0;
        return new DestructionLease(this, session, state);
    }

    private SessionState GetState(nint session)
    {
        lock (statesLock)
        {
            if (!states.TryGetValue(session, out var state))
                states.Add(session, state = new SessionState(++nextGeneration));
            return state;
        }
    }

    private SessionState? TryGetState(nint session)
    {
        lock (statesLock)
            return states.GetValueOrDefault(session);
    }

    private void ReleaseDestruction(nint session, SessionState state)
    {
        lock (statesLock)
        {
            if (states.TryGetValue(session, out var current) &&
                ReferenceEquals(current, state))
                states.Remove(session);
        }
        Monitor.Exit(state.Sync);
    }

    private sealed class SessionState(long generation)
    {
        public object Sync { get; } = new();
        public long Generation { get; set; } = generation;
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

    private sealed class DestructionLease(
        SessionLifetimeRegistry owner,
        nint session,
        SessionState state) : IDisposable
    {
        private SessionLifetimeRegistry? heldOwner = owner;

        public void Dispose()
        {
            var releasedOwner = Interlocked.Exchange(ref heldOwner, null);
            if (releasedOwner is not null)
                releasedOwner.ReleaseDestruction(session, state);
        }
    }
}
