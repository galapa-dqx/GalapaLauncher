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
            0x55555555);

        Assert.Equal(50, thunk.Length);
        Assert.Equal(0x11111111u, BitConverter.ToUInt32(thunk, 4));
        Assert.Equal(0x44444444u, BitConverter.ToUInt32(thunk, 9));
        Assert.Equal(0x33333333u, BitConverter.ToUInt32(thunk, 20));
        Assert.Equal(0x55555555u, BitConverter.ToUInt32(thunk, 26));
        Assert.Equal(0x22222222u, BitConverter.ToUInt32(thunk, 37));
        Assert.Equal(new byte[] { 0x83, 0xC4, 0x04 }, thunk[43..46]);
        Assert.Equal(new byte[] { 0x5D, 0xC2, 0x04, 0x00 }, thunk[46..]);
    }
}
