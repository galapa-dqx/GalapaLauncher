using System.Reflection;
using System.Runtime.InteropServices;
using Talon.Hooking;
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
        var attribute = Assert.Single(
            delegateType.GetCustomAttributesData(),
            data => data.AttributeType == typeof(FunctionAttribute));
        var convention = Assert.Single(attribute.ConstructorArguments);
        Assert.Equal(
            (int)Reloaded.Hooks.Definitions.X86.CallingConventions.MicrosoftThiscall,
            Convert.ToInt32(convention.Value));
        var unmanaged = delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(unmanaged);
        Assert.Equal(CallingConvention.ThisCall, unmanaged.CallingConvention);
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

    [Fact]
    public void RejectsDelegateWithoutReloadedConvention() =>
        Assert.Throws<ArgumentException>(() =>
            HookDelegateValidator.Validate<UnmanagedOnlyDelegate>());

    [Fact]
    public void RejectsMismatchedDelegateConventions() =>
        Assert.Throws<ArgumentException>(() =>
            HookDelegateValidator.Validate<MismatchedDelegate>());

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UnmanagedOnlyDelegate();

    [Function(Reloaded.Hooks.Definitions.X86.CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MismatchedDelegate();
}
