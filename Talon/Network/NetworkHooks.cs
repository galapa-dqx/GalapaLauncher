using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X86;
using Talon.Hooking;
using Talon.Interop;

namespace Talon.Network;

// Connects VCE frame parsing to managed packet handlers and safe replay.
internal sealed class NetworkHooks(
    ISigScanner scanner,
    SignatureScanResult signatures,
    IGameInteropProvider interop,
    TalonStartInfo startInfo) : IDisposable
{
    private const int MaximumPumpPackets = 32;
    private static readonly TimeSpan MaximumPumpTime = TimeSpan.FromMilliseconds(1);
    private readonly PacketHandlerService handlers = new();
    private readonly SessionLifetimeRegistry sessionLifetimes = new();
    private readonly object sessionHookLock = new();
    private volatile Hook<FrameParserDelegate>? parserHook;
    private volatile Hook<PollerDelegate>? pollerHook;
    private volatile Hook<ProcessPayloadDelegate>? processPayloadHook;
    private volatile Hook<SessionDestructorDelegate>? destructorHook;
    private volatile PcapNgWriter? capture;
    private volatile FrameParserDelegate? parserOriginal;
    private volatile PollerDelegate? pollerOriginal;
    private volatile ProcessPayloadDelegate? processPayloadOriginal;
    private volatile SessionDestructorDelegate? destructorOriginal;
    private volatile bool sessionHookInstallQueued;
    private volatile bool disposed;

    // ProcessPayload runs inside the parser call. TLS carries that frame's type
    // without sharing state between VCE threads.
    [ThreadStatic]
    private static byte currentFrameType;

    [ThreadStatic]
    private static bool parsingFrame;

    // Reinjected packets call ProcessPayload directly and must not be held again.
    [ThreadStatic]
    private static bool replaying;

    public void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(startInfo.PacketCapturePath))
        {
            capture = new PcapNgWriter(startInfo.PacketCapturePath);
            handlers.Register((IInboundPacketObserver)capture);
        }
        if (startInfo.NetworkSmokeTest)
            handlers.Register(new DialogueReplaySmokeInterceptor());

        var resolver = new VceResolver(scanner, signatures);
        var parser = resolver.ResolveFrameParser();
        var poller = resolver.ResolveSelectPoller();
        parserHook = interop.HookFromAddress(
            parser,
            (FrameParserDelegate)FrameParserDetour);
        pollerHook = interop.HookFromAddress(
            poller,
            (PollerDelegate)PollerDetour);
        parserOriginal = parserHook.OriginalDisposeSafe;
        pollerOriginal = pollerHook.OriginalDisposeSafe;
        parserHook.Enable();
        pollerHook.Enable();
        Log.Info($"VCE parser hook enabled at 0x{parser:X8}");
        Log.Info($"VCE poller hook enabled at 0x{poller:X8}");
    }

    public void Dispose()
    {
        lock (sessionHookLock)
        {
            if (disposed) return;
            disposed = true;
            destructorHook?.Dispose();
            processPayloadHook?.Dispose();
            pollerHook?.Dispose();
            parserHook?.Dispose();
            capture?.Dispose();
        }
    }

    private int FrameParserDetour(nint session, nint frame, int length)
    {
        var original = parserOriginal;
        if (original is null) return 0;
        var previous = currentFrameType;
        var wasParsing = parsingFrame;
        try
        {
            try
            {
                // The parser supplies the live session object and calls its payload slot.
                QueueSessionHookInstall(session);
                parsingFrame = true;
                currentFrameType = frame != 0 && length > 0
                    ? (byte)(Marshal.ReadByte(frame) >> 4)
                    : byte.MaxValue;
            }
            catch (Exception exception)
            {
                // Hook bookkeeping must never prevent VCE from parsing the frame.
                Log.Error("VCE parser setup failed open", exception);
            }
            return original(session, frame, length);
        }
        catch (Exception exception)
        {
            Log.Error("VCE parser original failed", exception);
            return 0;
        }
        finally
        {
            currentFrameType = previous;
            parsingFrame = wasParsing;
        }
    }

    private void ProcessPayloadDetour(nint session, nint payload, int length)
    {
        var original = processPayloadOriginal;
        if (original is null) return;
        var held = false;
        try
        {
            // Only normal type-0 data frames enter the translation path. VCE control
            // traffic and recursive replay remain synchronous.
            if (ShouldHoldPayload(replaying, parsingFrame, currentFrameType, payload, length) &&
                sessionLifetimes.TryGetGeneration(session, out var generation))
            {
                unsafe
                {
                    held = handlers.TryHold(
                        session,
                        generation,
                        new ReadOnlySpan<byte>((void*)payload, length));
                }
            }
        }
        catch (Exception exception) { Log.Error("VCE payload detour failed open", exception); }
        if (!held) TryCallPayloadOriginal(original, session, payload, length);
    }

    private nint SessionDestructorDetour(nint session, uint flags)
    {
        var original = destructorOriginal;
        if (original is null) return 0;
        IDisposable? destruction = null;
        try
        {
            // Binary Ninja confirms slot 0 is the most-derived scalar deleting
            // destructor. Invalidate after active replay drains, then release the
            // gate before native teardown so VCE cannot invert a managed lock.
            destruction = sessionLifetimes.AcquireDestruction(session);
        }
        catch (Exception exception)
        {
            // Lifetime bookkeeping is protective, but native destruction must run.
            Log.Error("VCE destruction tracking failed open", exception);
        }

        try { return original(session, flags); }
        catch (Exception exception)
        {
            Log.Error("VCE destructor original failed", exception);
            return 0;
        }
        finally
        {
            try { destruction?.Dispose(); }
            catch (Exception exception)
            {
                Log.Error("VCE destruction tracking cleanup failed", exception);
            }
        }
    }

    private nint PollerDetour(nint poller)
    {
        // NormalSelectPoller is a member function. Preserve ECX when the
        // detour calls the original implementation.
        var pollerCall = pollerOriginal;
        if (pollerCall is null) return 0;
        nint result;
        try { result = pollerCall(poller); }
        catch (Exception exception)
        {
            Log.Error("VCE poller original failed", exception);
            return 0;
        }
        try
        {
            // Drain completed work on VCE's own thread. Completion order is deliberate:
            // a slow packet does not block a later packet that is ready to replay.
            var pumpStart = Stopwatch.GetTimestamp();
            for (var count = 0;
                 count < MaximumPumpPackets &&
                 Stopwatch.GetElapsedTime(pumpStart) < MaximumPumpTime &&
                 handlers.TryDequeue(out var packet);
                 count++)
            {
                using var replayLease = sessionLifetimes.TryAcquireReplay(
                    packet.Session,
                    packet.Generation);
                if (replayLease is null)
                {
                    capture?.Write(packet, PacketCaptureEvent.ConnectionClosed);
                    continue;
                }

                unsafe
                {
                    fixed (byte* data = packet.Data)
                    {
                        replaying = true;
                        try
                        {
                            var payloadCall = processPayloadOriginal;
                            if (payloadCall is null) continue;
                            TryCallPayloadOriginal(
                                payloadCall,
                                packet.Session,
                                (nint)data,
                                packet.Data.Length);
                            capture?.Write(packet, PacketCaptureEvent.Reinject);
                        }
                        finally
                        {
                            replaying = false;
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Polling already succeeded; an auxiliary replay failure must not alter it.
            Log.Error("VCE replay pump failed open", exception);
        }
        return result;
    }

    private void QueueSessionHookInstall(nint session)
    {
        if (session == 0 || disposed || sessionHookInstallQueued || processPayloadHook is not null)
            return;
        lock (sessionHookLock)
        {
            if (disposed || sessionHookInstallQueued || processPayloadHook is not null) return;
            sessionHookInstallQueued = true;
        }
        try
        {
            var vtable = Marshal.ReadIntPtr(session);
            // DQX 8.0 base and observed derived VCE vtables use a scalar deleting
            // destructor in slot 0. ProcessPayload is at byte offset 0x5C.
            var destructorSlot = vtable;
            var processPayloadSlot = vtable + 0x5C;
            var destructor = Marshal.ReadIntPtr(destructorSlot);
            var processPayload = Marshal.ReadIntPtr(processPayloadSlot);
            if (!IsInText(destructor) || !IsInText(processPayload))
                throw new InvalidOperationException("VCE session vtable targets are outside .text.");
            InstallSessionHooks(
                session,
                processPayloadSlot,
                processPayload,
                destructorSlot,
                destructor);
        }
        catch (Exception exception)
        {
            lock (sessionHookLock) sessionHookInstallQueued = false;
            Log.Error("VCE session hook discovery failed", exception);
        }
    }

    private void InstallSessionHooks(
        nint session,
        nint processPayloadSlot,
        nint processPayload,
        nint destructorSlot,
        nint destructor)
    {
        Hook<ProcessPayloadDelegate>? newPayloadHook = null;
        Hook<SessionDestructorDelegate>? newDestructorHook = null;
        try
        {
            lock (sessionHookLock)
            {
                if (disposed) return;
                // VCE dispatches both methods through this shared vtable. Replace
                // each aligned x86 pointer atomically instead of patching code that
                // another parser thread may currently be executing.
                newPayloadHook = interop.HookFromFunctionPointerVariable(
                    processPayloadSlot,
                    (ProcessPayloadDelegate)ProcessPayloadDetour);
                newDestructorHook = interop.HookFromFunctionPointerVariable(
                    destructorSlot,
                    (SessionDestructorDelegate)SessionDestructorDetour);
                processPayloadOriginal = newPayloadHook.OriginalDisposeSafe;
                destructorOriginal = newDestructorHook.OriginalDisposeSafe;
                processPayloadHook = newPayloadHook;
                destructorHook = newDestructorHook;
                newPayloadHook = null;
                newDestructorHook = null;
                // Publish both hook objects before either detour can run. Enable
                // the payload hook last because it is the active traffic path.
                destructorHook.Enable();
                processPayloadHook.Enable();
            }
            _ = sessionLifetimes.GetGeneration(session);
            Log.Info(
                $"VCE session hooks enabled: payload=0x{processPayload:X8}, destructor=0x{destructor:X8}");
        }
        catch (Exception exception)
        {
            lock (sessionHookLock)
            {
                DisposeFailedHook(newPayloadHook, "new VCE payload");
                DisposeFailedHook(newDestructorHook, "new VCE destructor");
                DisposeFailedHook(processPayloadHook, "VCE payload");
                DisposeFailedHook(destructorHook, "VCE destructor");
                processPayloadHook = null;
                destructorHook = null;
                processPayloadOriginal = null;
                destructorOriginal = null;
                sessionHookInstallQueued = false;
            }
            Log.Error("VCE session hook installation failed", exception);
        }
    }

    private static void DisposeFailedHook<T>(Hook<T>? hook, string name) where T : Delegate
    {
        try { hook?.Dispose(); }
        catch (Exception exception) { Log.Error($"{name} hook cleanup failed", exception); }
    }

    private static void TryCallPayloadOriginal(
        ProcessPayloadDelegate original,
        nint session,
        nint payload,
        int length)
    {
        try { original(session, payload, length); }
        catch (Exception exception) { Log.Error("VCE payload original failed", exception); }
    }

    internal static bool ShouldHoldPayload(
        bool isReplaying,
        bool isParsingFrame,
        byte frameType,
        nint payload,
        int length) =>
        !isReplaying && isParsingFrame && frameType == 0 && payload != 0 && length > 0;

    private bool IsInText(nint address) =>
        address >= scanner.TextSectionBase &&
        address < scanner.TextSectionBase + scanner.TextSectionSize;

    [Function(CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int FrameParserDelegate(nint session, nint frame, int length);

    [Function(CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ProcessPayloadDelegate(nint session, nint payload, int length);

    [Function(CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint SessionDestructorDelegate(nint session, uint flags);

    [Function(CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint PollerDelegate(nint poller);
}
