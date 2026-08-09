using Talon.Hooking;

namespace Talon.Tests;

public sealed class GameInteropProviderTests
{
    [Fact]
    public unsafe void ZeroOriginalFirstThunkDoesNotEndImportTable()
    {
        uint* descriptor = stackalloc uint[5];
        new Span<uint>(descriptor, 5).Clear();
        descriptor[3] = 0x1234;
        descriptor[4] = 0x5678;

        Assert.False(GameInteropProvider.IsNullImportDescriptor((nint)descriptor));
    }

    [Fact]
    public unsafe void AllZeroImportDescriptorEndsImportTable()
    {
        uint* descriptor = stackalloc uint[5];
        new Span<uint>(descriptor, 5).Clear();

        Assert.True(GameInteropProvider.IsNullImportDescriptor((nint)descriptor));
    }

    [Fact]
    public unsafe void FindsResolvedAddressWhenOriginalThunkIsAbsent()
    {
        uint* iat = stackalloc uint[] { 0x10203040, 0x50607080, 0 };

        Assert.Equal(
            (nint)(iat + 1),
            GameInteropProvider.FindResolvedImport((nint)iat, 0x50607080));
    }
}
