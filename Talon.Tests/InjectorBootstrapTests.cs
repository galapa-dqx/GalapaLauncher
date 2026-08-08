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
            0x66666666);

        Assert.Equal(68, thunk.Length);
        Assert.Equal(0x11111111u, BitConverter.ToUInt32(thunk, 4));
        Assert.Equal(0x44444444u, BitConverter.ToUInt32(thunk, 9));
        Assert.Equal(0x33333333u, BitConverter.ToUInt32(thunk, 20));
        Assert.Equal(0x55555555u, BitConverter.ToUInt32(thunk, 26));
        Assert.Equal(0x22222222u, BitConverter.ToUInt32(thunk, 37));
        Assert.Equal(new byte[] { 0x83, 0xC4, 0x04 }, thunk[43..46]);
        Assert.Equal(new byte[] { 0x5D, 0xC2, 0x04, 0x00 }, thunk[50..54]);
        Assert.Equal(0x66666666u, BitConverter.ToUInt32(thunk, 61));

        Assert.Equal(54, ShortBranchTarget(thunk, 18));
        Assert.Equal(54, ShortBranchTarget(thunk, 35));
        Assert.Equal(54, ShortBranchTarget(thunk, 49));
        Assert.Equal(
            new byte[] { 0x85, 0xC0, 0x75, 0x01, 0x40, 0x50, 0xB8 },
            thunk[54..61]);
    }

    private static int ShortBranchTarget(byte[] code, int operand) =>
        operand + 1 + (sbyte)code[operand];
}
