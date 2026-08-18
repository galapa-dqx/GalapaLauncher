namespace Talon.Tests;

public sealed class InjectorBootstrapTests
{
    [Fact]
    public void X86ApcThunkCarriesExpectedPointersAndCallingConventionCleanup()
    {
        var thunk = Talon.Injector.Injector.BuildBootstrapThunk(
            0x11111111,
            0x22222222,
            0x33333333,
            0x44444444,
            0x55555555,
            0x66666666,
            0x77777777,
            0x88888888,
            0x99999999);

        Assert.Equal(Talon.Injector.Injector.BootstrapCodeLength, thunk.Length);
        Assert.Equal(0x44444444u + Talon.Injector.Injector.BootstrapThunkLength,
            BitConverter.ToUInt32(thunk, 5));
        Assert.Equal(0x88888888u, BitConverter.ToUInt32(thunk, 12));
        Assert.Equal(0x11111111u, BitConverter.ToUInt32(thunk, 21));
        Assert.Equal(0x66666666u, BitConverter.ToUInt32(thunk, 26));
        Assert.Equal(0x33333333u, BitConverter.ToUInt32(thunk, 37));
        Assert.Equal(0x77777777u, BitConverter.ToUInt32(thunk, 43));
        Assert.Equal(0x22222222u, BitConverter.ToUInt32(thunk, 54));
        Assert.Equal(0x99999999u, BitConverter.ToUInt32(thunk, 73));
        Assert.Equal(new byte[] { 0x5B, 0x5D, 0xC2, 0x04, 0x00 }, thunk[79..84]);

        Assert.Equal(79, ShortBranchTarget(thunk, 35));
        Assert.Equal(79, ShortBranchTarget(thunk, 52));
        Assert.Equal(79, ShortBranchTarget(thunk, 66));
        Assert.Equal(79, ShortBranchTarget(thunk, 70));
        Assert.DoesNotContain((byte)0xCC, thunk);
    }

    [Fact]
    public void FallbackVehClearsOnlyTheExpectedEntrypointDr0Trap()
    {
        var code = Talon.Injector.Injector.BuildBootstrapThunk(
            0x11111111,
            0x22222222,
            0x33333333,
            0x44444444,
            0x55555555,
            0x66666666,
            0x77777777,
            0x88888888,
            0x99999999);

        Assert.Equal(0x80000004u, BitConverter.ToUInt32(code, 102));
        Assert.Equal(0x55555555u, BitConverter.ToUInt32(code, 111));
        Assert.Equal(new byte[] { 0xC7, 0x42, 0x04, 0, 0, 0, 0 }, code[133..140]);
        Assert.Equal(
            new byte[] { 0x81, 0x62, 0x18, 0xFC, 0xFF, 0xF0, 0xFF },
            code[140..147]);
        Assert.Equal(new byte[] { 0xC7, 0x42, 0x14, 0, 0, 0, 0 }, code[147..154]);
        Assert.Equal(
            new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0x5D, 0xC2, 0x04, 0 },
            code[154..163]);
        Assert.Equal(new byte[] { 0x33, 0xC0, 0x5D, 0xC2, 0x04, 0 }, code[163..169]);

        foreach (var operand in new[] { 93, 99, 107, 116, 123, 132 })
            Assert.Equal(163, ShortBranchTarget(code, operand));
    }

    private static int ShortBranchTarget(byte[] code, int operand) =>
        operand + 1 + (sbyte)code[operand];
}
