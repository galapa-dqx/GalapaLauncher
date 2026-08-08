using System.Runtime.InteropServices;
using System.Text.Json;

namespace Talon;

/// <summary>Provides the unmanaged entry point resolved by Talon.Boot.</summary>
public static partial class EntryPoint
{
    /// <summary>Matches the callback signature requested through hostfxr.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void InitDelegate(nint startInfoJson, nint mainThreadContinueEvent);

    /// <summary>Starts managed Talon and always releases the native unpack barrier.</summary>
    public static void Initialize(nint startInfoJson, nint mainThreadContinueEvent)
    {
        try
        {
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
            RuntimeHost.Initialize(startInfo);
        }
        catch (Exception exception)
        {
            Log.Error("managed initialization failed", exception);
        }
        finally
        {
            SetEvent(mainThreadContinueEvent);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);
}
