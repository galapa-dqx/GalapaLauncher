using System.Runtime.InteropServices;

namespace Talon.Tests;

public sealed class CoreClrLayoutTests
{
    [Fact]
    public void CoLocatedX86HostFxrLoadsAndExportsHostingApi()
    {
        var directory = Path.GetDirectoryName(typeof(Talon.EntryPoint).Assembly.Location)!;
        var path = Path.Combine(directory, "hostfxr.dll");

        var hostFxr = NativeLibrary.Load(path);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(
                hostFxr,
                "hostfxr_initialize_for_runtime_config",
                out _));
            Assert.True(NativeLibrary.TryGetExport(
                hostFxr,
                "hostfxr_get_runtime_delegate",
                out _));
            Assert.True(NativeLibrary.TryGetExport(hostFxr, "hostfxr_close", out _));
        }
        finally
        {
            NativeLibrary.Free(hostFxr);
        }
    }
}
