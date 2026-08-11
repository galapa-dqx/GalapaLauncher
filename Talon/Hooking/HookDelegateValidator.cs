using System.Reflection;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X86;
using ReloadedCallingConventions = Reloaded.Hooks.Definitions.X86.CallingConventions;

namespace Talon.Hooking;

internal static class HookDelegateValidator
{
    public static void Validate<T>() where T : Delegate
    {
        var type = typeof(T);
        var unmanaged = type.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()
            ?? throw new ArgumentException(
                $"Hook delegate {type.FullName} must declare UnmanagedFunctionPointerAttribute.");
        var function = type.GetCustomAttributesData().SingleOrDefault(
            attribute => attribute.AttributeType == typeof(FunctionAttribute))
            ?? throw new ArgumentException(
                $"Hook delegate {type.FullName} must declare Reloaded FunctionAttribute.");
        var reloaded = (ReloadedCallingConventions)Convert.ToInt32(
            AssertSingleConvention(function, type).Value);
        var expected = unmanaged.CallingConvention switch
        {
            CallingConvention.Cdecl => ReloadedCallingConventions.Cdecl,
            CallingConvention.StdCall => ReloadedCallingConventions.Stdcall,
            CallingConvention.ThisCall => ReloadedCallingConventions.MicrosoftThiscall,
            CallingConvention.FastCall => ReloadedCallingConventions.Fastcall,
            _ => throw new ArgumentException(
                $"Hook delegate {type.FullName} uses unsupported convention " +
                $"{unmanaged.CallingConvention}.")
        };
        if (reloaded != expected)
            throw new ArgumentException(
                $"Hook delegate {type.FullName} declares mismatched conventions: " +
                $"Reloaded={reloaded}, unmanaged={unmanaged.CallingConvention}.");
    }

    private static CustomAttributeTypedArgument AssertSingleConvention(
        CustomAttributeData attribute,
        Type delegateType)
    {
        if (attribute.ConstructorArguments.Count != 1)
            throw new ArgumentException(
                $"Hook delegate {delegateType.FullName} has an invalid FunctionAttribute.");
        return attribute.ConstructorArguments[0];
    }
}
