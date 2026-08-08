using System.Diagnostics;
using System.Runtime.InteropServices;
using Talon.Hooking;
using Talon.Interop;

namespace Talon.Network;

// Connects VCE frame parsing to managed packet handlers and safe replay.
internal sealed class NetworkHooks(
    ISigScanner scanner,
    IGameInteropProvider interop,
    TalonStartInfo startInfo) : IDisposable
{
    private const int MaximumPumpPackets = 32;
    private static readonly TimeSpan MaximumPumpTime = TimeSpan.FromMilliseconds(1);
    private readonly PacketHandlerService handlers = new();
    private readonly object sessionHookLock = new();
    private readonly Dictionary<nint, long> generations = [];
    private Hook<FrameParserDelegate>? parserHook;
    private Hook<PollerDelegate>? pollerHook;
    private Hook<ProcessPayloadDelegate>? processPayloadHook;
    private Hook<SessionDestructorDelegate>? destructorHook;
    private PcapNgWriter? capture;
    private bool sessionHookInstallQueued;

    // ProcessPayload runs inside the parser call. TLS carries that frame's type
    // without sharing state between VCE threads.
    [ThreadStatic]
    private static byte currentFrameType;

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

        var resolver = new VceResolver(scanner);
        var parser = resolver.ResolveFrameParser();
        var poller = resolver.ResolveSelectPoller();
        parserHook = interop.HookFromAddress(
            parser,
            (FrameParserDelegate)FrameParserDetour);
        pollerHook = interop.HookFromAddress(
            poller,
            (PollerDelegate)PollerDetour);
        parserHook.Enable();
        pollerHook.Enable();
        Log.Info($"VCE parser hook enabled at 0x{parser:X8}");
        Log.Info($"VCE poller hook enabled at 0x{poller:X8}");
    }

    public void Dispose()
    {
        destructorHook?.Dispose();
        processPayloadHook?.Dispose();
        pollerHook?.Dispose();
        parserHook?.Dispose();
        capture?.Dispose();
    }

    private int FrameParserDetour(nint session, nint frame, int length)
    {
        // The parser supplies the live session object and calls its payload slot.
        QueueSessionHookInstall(session);
        var previous = currentFrameType;
        currentFrameType = frame != 0 && length > 0
            ? (byte)(Marshal.ReadByte(frame) >> 4)
            : byte.MaxValue;
        try
        {
            return parserHook!.Original(session, frame, length);
        }
        finally
        {
            currentFrameType = previous;
        }
    }

    private void ProcessPayloadDetour(nint session, nint payload, int length)
    {
        // Only normal type-0 data frames enter the translation path. VCE control
        // traffic and recursive replay remain synchronous.
        if (replaying || currentFrameType != 0 || payload == 0 || length <= 0)
        {
            processPayloadHook!.Original(session, payload, length);
            return;
        }

        long generation;
        lock (generations)
        {
            if (!generations.TryGetValue(session, out generation))
                generations[session] = generation = 1;
        }

        unsafe
        {
            if (!handlers.TryHold(session, generation, new ReadOnlySpan<byte>((void*)payload, length)))
                processPayloadHook!.Original(session, payload, length);
        }
    }

    private nint SessionDestructorDetour(nint session, uint flags)
    {
        // A reused session address gets a new generation. Late completions from
        // the old connection can then be discarded safely.
        lock (generations)
            generations[session] = generations.GetValueOrDefault(session) + 1;
        return destructorHook!.Original(session, flags);
    }

    private nint PollerDetour()
    {
        var result = pollerHook!.Original();
        // Drain completed work on VCE's own thread. Completion order is deliberate:
        // a slow packet does not block a later packet that is ready to replay.
        var stopwatch = Stopwatch.StartNew();
        for (var count = 0;
             count < MaximumPumpPackets && stopwatch.Elapsed < MaximumPumpTime &&
             handlers.TryDequeue(out var packet);
             count++)
        {
            long generation;
            lock (generations) generation = generations.GetValueOrDefault(packet.Session);
            if (generation != packet.Generation)
            {
                capture?.Write(packet, PacketCaptureEvent.Cancelled);
                continue;
            }

            unsafe
            {
                fixed (byte* data = packet.Data)
                {
                    replaying = true;
                    try
                    {
                        processPayloadHook?.Original(
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
        return result;
    }

    private void QueueSessionHookInstall(nint session)
    {
        if (session == 0 || sessionHookInstallQueued || processPayloadHook is not null) return;
        lock (sessionHookLock)
        {
            if (sessionHookInstallQueued || processPayloadHook is not null) return;
            sessionHookInstallQueued = true;
        }
        try
        {
            var vtable = Marshal.ReadIntPtr(session);
            // DQX's VCE iSession has its destructor at slot 0 and ProcessPayload
            // at byte offset 0x5C. Reject pointers outside game code before hooking.
            var destructor = Marshal.ReadIntPtr(vtable);
            var processPayload = Marshal.ReadIntPtr(vtable + 0x5C);
            if (!IsInText(destructor) || !IsInText(processPayload))
                throw new InvalidOperationException("VCE session vtable targets are outside .text.");
            // Patch from a worker instead of modifying the active parser stack.
            _ = Task.Run(() => InstallSessionHooks(session, processPayload, destructor));
        }
        catch (Exception exception)
        {
            lock (sessionHookLock) sessionHookInstallQueued = false;
            Log.Error("VCE session hook discovery failed", exception);
        }
    }

    private void InstallSessionHooks(nint session, nint processPayload, nint destructor)
    {
        try
        {
            lock (sessionHookLock)
            {
                processPayloadHook = interop.HookFromAddress(
                    processPayload,
                    (ProcessPayloadDelegate)ProcessPayloadDetour);
                destructorHook = interop.HookFromAddress(
                    destructor,
                    (SessionDestructorDelegate)SessionDestructorDetour);
                processPayloadHook.Enable();
                destructorHook.Enable();
            }
            lock (generations) generations[session] = 1;
            Log.Info(
                $"VCE session hooks enabled: payload=0x{processPayload:X8}, destructor=0x{destructor:X8}");
        }
        catch (Exception exception)
        {
            lock (sessionHookLock) sessionHookInstallQueued = false;
            Log.Error("VCE session hook installation failed", exception);
        }
    }

    private bool IsInText(nint address) =>
        address >= scanner.TextSectionBase &&
        address < scanner.TextSectionBase + scanner.TextSectionSize;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int FrameParserDelegate(nint session, nint frame, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ProcessPayloadDelegate(nint session, nint payload, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint SessionDestructorDelegate(nint session, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PollerDelegate();
}
