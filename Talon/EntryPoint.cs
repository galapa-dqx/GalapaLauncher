using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Talon;

/// <summary>Provides the unmanaged entry point resolved by Talon.Boot.</summary>
public static partial class EntryPoint
{
    /// <summary>Matches the callback signature requested through hostfxr.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void InitDelegate(
        nint startInfoJson,
        nint mainThreadContinueEvent,
        nint initializationCancelledEvent,
        nint initializationState);

    /// <summary>Starts managed Talon and always releases the native unpack barrier.</summary>
    public static void Initialize(
        nint startInfoJson,
        nint mainThreadContinueEvent,
        nint initializationCancelledEvent,
        nint initializationState)
    {
        RuntimeHost.PreparedRuntime? prepared = null;
        using var cancellation = new NativeInitializationCancellation(
            initializationCancelledEvent);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var json = Marshal.PtrToStringUTF8(startInfoJson)
                ?? throw new InvalidOperationException("Native bootstrap supplied null start-info JSON.");
            var startInfo = JsonSerializer.Deserialize<TalonStartInfo>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Could not deserialize Talon start info.");
            if (startInfo.Version != 1)
                throw new NotSupportedException($"Unsupported Talon start-info version {startInfo.Version}.");

            Log.Open();
            Log.Info($"managed runtime initialized ({RuntimeInformation.FrameworkDescription})");
            prepared = RuntimeHost.Prepare(startInfo, cancellation.Token);
            cancellation.ThrowIfCancellationRequested();
            if (!TryCommitInitialization(initializationState))
                throw new OperationCanceledException(
                    "Native startup cancelled before managed hooks could commit.",
                    cancellation.Token);
            RuntimeHost.Commit(prepared);
            prepared = null;
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            Log.Warning("managed initialization cancelled; prepared hooks were rolled back");
            FailureNotifier.ShowOnce(
                "the managed runtime before its startup deadline",
                exception);
        }
        catch (Exception exception)
        {
            CancelInitialization(initializationState, initializationCancelledEvent);
            Log.Error("managed initialization failed", exception);
            FailureNotifier.ShowOnce("the managed runtime", exception);
        }
        finally
        {
            prepared?.Dispose();
            SetEvent(mainThreadContinueEvent);
        }
    }

    internal static unsafe bool TryCommitInitialization(nint stateAddress)
    {
        if (stateAddress == 0) return false;
        ref var state = ref Unsafe.AsRef<int>((void*)stateAddress);
        return Interlocked.CompareExchange(ref state, 1, 0) == 0;
    }

    private static unsafe void CancelInitialization(nint stateAddress, nint cancelledEvent)
    {
        if (stateAddress != 0)
        {
            ref var state = ref Unsafe.AsRef<int>((void*)stateAddress);
            Interlocked.CompareExchange(ref state, 2, 0);
        }
        if (cancelledEvent != 0) SetEvent(cancelledEvent);
    }

    private sealed class NativeInitializationCancellation : IDisposable
    {
        private readonly EventWaitHandle cancelledEvent = new(false, EventResetMode.ManualReset);
        private readonly CancellationTokenSource source = new();
        private readonly RegisteredWaitHandle registration;

        public NativeInitializationCancellation(nint handle)
        {
            if (handle == 0)
                throw new ArgumentException("Native cancellation event is null.", nameof(handle));
            cancelledEvent.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
            if (cancelledEvent.WaitOne(0)) source.Cancel();
            registration = ThreadPool.RegisterWaitForSingleObject(
                cancelledEvent,
                static (state, _) =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                },
                source,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        public CancellationToken Token => source.Token;

        public bool IsCancellationRequested =>
            source.IsCancellationRequested || cancelledEvent.WaitOne(0);

        public void ThrowIfCancellationRequested()
        {
            if (cancelledEvent.WaitOne(0) && !source.IsCancellationRequested)
                source.Cancel();
            source.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            registration.Unregister(null);
            cancelledEvent.Dispose();
            source.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);
}
