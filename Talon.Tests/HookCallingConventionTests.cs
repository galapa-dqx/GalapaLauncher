using System.Reflection;
using Reloaded.Hooks.Definitions.X86;

namespace Talon.Tests;

public sealed class HookCallingConventionTests
{
    [Theory]
    [InlineData(typeof(Talon.Vfs.VfsHooks), "VfsLoadResourceDelegate")]
    [InlineData(typeof(Talon.Network.NetworkHooks), "FrameParserDelegate")]
    [InlineData(typeof(Talon.Network.NetworkHooks), "ProcessPayloadDelegate")]
    [InlineData(typeof(Talon.Network.NetworkHooks), "SessionDestructorDelegate")]
    [InlineData(typeof(Talon.Network.NetworkHooks), "PollerDelegate")]
    public void X86HookDelegateDeclaresReloadedCallingConvention(
        Type owner,
        string delegateName)
    {
        var delegateType = owner.GetNestedType(
            delegateName,
            BindingFlags.NonPublic);

        Assert.NotNull(delegateType);
        Assert.NotNull(delegateType.GetCustomAttribute<FunctionAttribute>());
    }

    [Fact]
    public void NetworkPollerDelegatePreservesThisPointer()
    {
        var delegateType = typeof(Talon.Network.NetworkHooks).GetNestedType(
            "PollerDelegate",
            BindingFlags.NonPublic)!;
        var invoke = delegateType.GetMethod("Invoke")!;

        var parameter = Assert.Single(invoke.GetParameters());
        Assert.Equal(typeof(nint), parameter.ParameterType);
    }
}
