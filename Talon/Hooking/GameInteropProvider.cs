using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Talon.Interop;

namespace Talon.Hooking;

/// <summary>Provides the public hook creation surface used by Talon extensions.</summary>
public sealed partial class GameInteropProvider(ISigScanner scanner) : IGameInteropProvider
{
    /// <inheritdoc />
    public void InitializeFromAttributes(object self)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var member in self.GetType().GetMembers(flags))
        {
            var attribute = member.GetCustomAttribute<SignatureAttribute>();
            if (attribute is null) continue;
            try
            {
                InitializeMember(self, member, attribute);
            }
            catch (Exception exception) when (attribute.Fallibility)
            {
                Log.Warning($"fallible signature '{attribute.Signature}' failed: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour)
        where T : Delegate =>
        new FunctionPointerVariableHook<T>(address, detour);

    /// <inheritdoc />
    public Hook<T> HookFromImport<T>(
        ProcessModule? module,
        string moduleName,
        string functionName,
        uint hintOrOrdinal,
        T detour) where T : Delegate =>
        HookFromFunctionPointerVariable(
            FindImport(module ?? Process.GetCurrentProcess().MainModule
                ?? throw new InvalidOperationException("No main module."),
                moduleName,
                functionName,
                hintOrOrdinal),
            detour);

    /// <inheritdoc />
    public Hook<T> HookFromSymbol<T>(
        string moduleName,
        string exportName,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate
    {
        var module = GetModuleHandle(moduleName);
        if (module == 0) throw new DllNotFoundException(moduleName);
        var address = GetProcAddress(module, exportName);
        if (address == 0) throw new MissingMethodException($"{moduleName}!{exportName}");
        return HookFromAddress(address, detour, backend);
    }

    /// <inheritdoc />
    public Hook<T> HookFromAddress<T>(
        nint procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate =>
        backend switch
        {
            HookBackend.MinHook => new MinHook<T>(procAddress, detour),
            HookBackend.Automatic or HookBackend.Reloaded =>
                new ReloadedHook<T>(procAddress, detour),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };

    /// <inheritdoc />
    public Hook<T> HookFromAddress<T>(
        nuint procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate =>
        HookFromAddress((nint)procAddress, detour, backend);

    /// <inheritdoc />
    public unsafe Hook<T> HookFromAddress<T>(
        void* procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate =>
        HookFromAddress((nint)procAddress, detour, backend);

    /// <inheritdoc />
    public Hook<T> HookFromSignature<T>(
        string signature,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate =>
        HookFromAddress(scanner.ScanText(signature), detour, backend);

    private void InitializeMember(
        object target,
        MemberInfo member,
        SignatureAttribute attribute)
    {
        var address = attribute.ScanType == SignatureScanType.StaticAddress
            ? scanner.GetStaticAddressFromSig(attribute.Signature)
            : scanner.ScanText(attribute.Signature);
        var memberType = member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new NotSupportedException($"Unsupported signature member {member.Name}."),
        };

        var use = attribute.UseFlags == SignatureUseFlags.Auto
            ? InferUse(memberType)
            : attribute.UseFlags;

        object value = use switch
        {
            SignatureUseFlags.Pointer => CreatePointer(memberType, address),
            SignatureUseFlags.Hook => CreateHook(target, member, memberType, address, attribute),
            SignatureUseFlags.Offset => ReadOffset(memberType, address, attribute.Offset),
            _ => throw new NotSupportedException(
                $"Signature member {member.Name} has unsupported type {memberType}."),
        };

        if (member is FieldInfo fieldInfo) fieldInfo.SetValue(target, value);
        else ((PropertyInfo)member).SetValue(target, value);
    }

    private static SignatureUseFlags InferUse(Type memberType)
    {
        if (memberType == typeof(nint) || memberType == typeof(IntPtr) ||
            typeof(Delegate).IsAssignableFrom(memberType))
            return SignatureUseFlags.Pointer;
        if (memberType.IsGenericType &&
            memberType.GetGenericTypeDefinition() == typeof(Hook<>))
            return SignatureUseFlags.Hook;
        if (memberType.IsPrimitive)
            return SignatureUseFlags.Offset;
        throw new NotSupportedException(
            $"Cannot infer signature use for member type {memberType}.");
    }

    private static object CreatePointer(Type memberType, nint address)
    {
        if (memberType == typeof(nint) || memberType == typeof(IntPtr))
            return address;
        if (typeof(Delegate).IsAssignableFrom(memberType))
            return Marshal.GetDelegateForFunctionPointer(address, memberType);
        throw new NotSupportedException(
            $"Signature pointer use does not support member type {memberType}.");
    }

    private object CreateHook(
        object target,
        MemberInfo member,
        Type memberType,
        nint address,
        SignatureAttribute attribute)
    {
        if (!memberType.IsGenericType ||
            memberType.GetGenericTypeDefinition() != typeof(Hook<>))
            throw new NotSupportedException(
                $"Signature hook use requires Hook<T>, not {memberType}.");

        var delegateType = memberType.GenericTypeArguments[0];
        var detourName = attribute.DetourName ?? $"{member.Name}Detour";
        var detourMethod = target.GetType().GetMethod(
            detourName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, detourName);
        var detour = detourMethod.CreateDelegate(delegateType, target);
        var hookMethod = GetType().GetMethods()
            .Single(method => method.Name == nameof(HookFromAddress) &&
                              method.IsGenericMethodDefinition &&
                              method.GetParameters()[0].ParameterType == typeof(nint));
        return hookMethod.MakeGenericMethod(delegateType)
            .Invoke(this, [address, detour, HookBackend.Automatic])!;
    }

    private static object ReadOffset(Type memberType, nint address, int offset)
    {
        if (!memberType.IsPrimitive)
            throw new NotSupportedException(
                $"Signature offset use requires a primitive member, not {memberType}.");
        return Marshal.PtrToStructure(address + offset, memberType)
            ?? throw new InvalidOperationException(
                $"Could not read {memberType} at 0x{address + offset:X8}.");
    }

    private static unsafe nint FindImport(
        ProcessModule module,
        string moduleName,
        string functionName,
        uint hintOrOrdinal)
    {
        var image = (byte*)module.BaseAddress;
        var nt = image + *(int*)(image + 0x3C);
        var optional = nt + 24;
        var importRva = *(uint*)(optional + 96 + 8);
        if (importRva == 0) throw new MissingMethodException("The module has no imports.");

        for (var descriptor = image + importRva; *(uint*)descriptor != 0; descriptor += 20)
        {
            var importedModule = Marshal.PtrToStringAnsi((nint)(image + *(uint*)(descriptor + 12)));
            if (!string.Equals(importedModule, moduleName, StringComparison.OrdinalIgnoreCase))
                continue;

            var lookupRva = *(uint*)descriptor;
            var iatRva = *(uint*)(descriptor + 16);
            if (lookupRva == 0) lookupRva = iatRva;
            for (var index = 0; ; index++)
            {
                var lookup = *(uint*)(image + lookupRva + index * 4);
                if (lookup == 0) break;
                var byOrdinal = (lookup & 0x80000000) != 0;
                var matches = byOrdinal
                    ? hintOrOrdinal != 0 && (lookup & 0xFFFF) == hintOrOrdinal
                    : string.Equals(
                        Marshal.PtrToStringAnsi((nint)(image + lookup + 2)),
                        functionName,
                        StringComparison.Ordinal);
                if (matches) return (nint)(image + iatRva + index * 4);
            }
        }
        throw new MissingMethodException($"{moduleName}!{functionName}");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string exportName);
}
