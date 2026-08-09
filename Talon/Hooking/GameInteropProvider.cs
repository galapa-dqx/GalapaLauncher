using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Talon.Interop;

// API shape adapted from Dalamud's GameInteropProvider and SignatureHelper;
// see THIRD_PARTY_NOTICES.md.

namespace Talon.Hooking;

/// <summary>Provides the public hook creation surface used by Talon extensions.</summary>
public sealed partial class GameInteropProvider(ISigScanner scanner) : IGameInteropProvider
{
    /// <inheritdoc />
    public void InitializeFromAttributes(object self)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var members = self.GetType().GetMembers(flags)
            .OrderBy(member => member.MetadataToken)
            .Select(member => (Member: member, Attribute: member.GetCustomAttribute<SignatureAttribute>()))
            .Where(entry => entry.Attribute is not null)
            .Select((entry, index) => (
                entry.Member,
                Attribute: entry.Attribute!,
                Query: new SignatureQuery(
                    $"{self.GetType().FullName}.{entry.Member.Name}.{index}",
                    entry.Attribute!.Signature)))
            .ToArray();
        var matches = scanner.ScanTextBatch(members.Select(entry => entry.Query).ToArray());

        var initialized = new List<(MemberInfo Member, object? Previous, IDisposable? Created)>();
        try
        {
            foreach (var entry in members)
            {
                var candidates = matches.GetMatches(entry.Query.Name);
                nint address;
                try
                {
                    if (candidates.Count != 1)
                        throw new KeyNotFoundException(
                            $"Signature '{entry.Attribute.Signature}' expected one match " +
                            $"but found {candidates.Count}.");
                    address = entry.Attribute.ScanType == SignatureScanType.StaticAddress
                        ? scanner.GetStaticAddressFromMatch(candidates[0])
                        : scanner.ResolveTextMatch(candidates[0]);
                }
                catch (KeyNotFoundException exception) when (entry.Attribute.Fallibility)
                {
                    Log.Warning(
                        $"fallible signature '{entry.Attribute.Signature}' failed: " +
                        exception.Message);
                    continue;
                }

                var previous = GetMemberValue(self, entry.Member);
                var value = CreateMemberValue(self, entry.Member, entry.Attribute, address);
                initialized.Add((entry.Member, previous, value as IDisposable));
                SetMemberValue(self, entry.Member, value);
            }
        }
        catch
        {
            for (var index = initialized.Count - 1; index >= 0; index--)
            {
                var assignment = initialized[index];
                try { assignment.Created?.Dispose(); }
                catch (Exception exception)
                {
                    Log.Error($"signature hook rollback failed for {assignment.Member.Name}", exception);
                }
                try { SetMemberValue(self, assignment.Member, assignment.Previous); }
                catch (Exception exception)
                {
                    Log.Error(
                        $"signature member rollback failed for {assignment.Member.Name}",
                        exception);
                }
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour)
        where T : Delegate
    {
        HookDelegateValidator.Validate<T>();
        return new FunctionPointerVariableHook<T>(address, detour);
    }

    /// <inheritdoc />
    public Hook<T> HookFromImport<T>(
        ProcessModule? module,
        string moduleName,
        string functionName,
        uint hintOrOrdinal,
        T detour) where T : Delegate =>
        HookFromFunctionPointerVariable(
            FindImport(module ?? GetMainModule(),
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
        HookBackend backend = HookBackend.Automatic) where T : Delegate
    {
        HookDelegateValidator.Validate<T>();
        return backend switch
        {
            HookBackend.Automatic or HookBackend.Reloaded =>
                new ReloadedHook<T>(procAddress, detour),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
    }

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

    private object CreateMemberValue(
        object target,
        MemberInfo member,
        SignatureAttribute attribute,
        nint address)
    {
        var memberType = member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new NotSupportedException($"Unsupported signature member {member.Name}."),
        };

        var use = attribute.UseFlags == SignatureUseFlags.Auto
            ? InferUse(memberType)
            : attribute.UseFlags;

        return use switch
        {
            SignatureUseFlags.Pointer => CreatePointer(memberType, address),
            SignatureUseFlags.Hook => CreateHook(target, member, memberType, address, attribute),
            SignatureUseFlags.Offset => ReadOffset(memberType, address, attribute.Offset),
            _ => throw new NotSupportedException(
                $"Signature member {member.Name} has unsupported type {memberType}."),
        };
    }

    private static object? GetMemberValue(object target, MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => throw new NotSupportedException($"Unsupported signature member {member.Name}."),
    };

    private static void SetMemberValue(object target, MemberInfo member, object? value)
    {
        if (member is FieldInfo field) field.SetValue(target, value);
        else if (member is PropertyInfo property) property.SetValue(target, value);
        else throw new NotSupportedException($"Unsupported signature member {member.Name}.");
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
        var pointer = address + offset;
        return Type.GetTypeCode(memberType) switch
        {
            TypeCode.Byte => (object)Marshal.ReadByte(pointer),
            TypeCode.SByte => unchecked((sbyte)Marshal.ReadByte(pointer)),
            TypeCode.Int16 => Marshal.ReadInt16(pointer),
            TypeCode.UInt16 => unchecked((ushort)Marshal.ReadInt16(pointer)),
            TypeCode.Int32 => Marshal.ReadInt32(pointer),
            TypeCode.UInt32 => unchecked((uint)Marshal.ReadInt32(pointer)),
            TypeCode.Int64 => Marshal.ReadInt64(pointer),
            TypeCode.UInt64 => unchecked((ulong)Marshal.ReadInt64(pointer)),
            TypeCode.Single => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer)),
            TypeCode.Double => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pointer)),
            _ => throw new NotSupportedException(
                $"Signature offset use requires a fixed-width primitive, not {memberType}.")
        };
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
        if (*(ushort*)optional != 0x010B)
            throw new BadImageFormatException("Import hooking requires a PE32 image.");
        var importRva = *(uint*)(optional + 96 + 8);
        if (importRva == 0) throw new MissingMethodException("The module has no imports.");

        for (var descriptor = image + importRva;
             !IsNullImportDescriptor((nint)descriptor);
             descriptor += 20)
        {
            var importedModule = Marshal.PtrToStringAnsi((nint)(image + *(uint*)(descriptor + 12)));
            if (!string.Equals(importedModule, moduleName, StringComparison.OrdinalIgnoreCase))
                continue;

            var lookupRva = *(uint*)descriptor;
            var iatRva = *(uint*)(descriptor + 16);
            if (lookupRva == 0)
            {
                // Bound images can omit OriginalFirstThunk. Their IAT already
                // contains resolved addresses, so compare those addresses with
                // the export instead of interpreting them as image RVAs.
                var importedHandle = GetModuleHandle(importedModule!);
                if (importedHandle == 0) throw new DllNotFoundException(importedModule);
                var target = !string.IsNullOrEmpty(functionName)
                    ? GetProcAddress(importedHandle, functionName)
                    : hintOrOrdinal is > 0 and <= ushort.MaxValue
                        ? GetProcAddress(importedHandle, (nint)hintOrOrdinal)
                        : 0;
                if (target == 0)
                    throw new MissingMethodException($"{moduleName}!{functionName}");
                return FindResolvedImport((nint)(image + iatRva), unchecked((uint)target));
            }
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

    internal static unsafe nint FindResolvedImport(nint iat, uint target)
    {
        var entries = (uint*)iat;
        for (var index = 0; entries[index] != 0; index++)
            if (entries[index] == target)
                return (nint)(entries + index);
        throw new MissingMethodException($"No IAT entry resolves to 0x{target:X8}.");
    }

    internal static unsafe bool IsNullImportDescriptor(nint descriptor)
    {
        var fields = (uint*)descriptor;
        return fields[0] == 0 &&
               fields[1] == 0 &&
               fields[2] == 0 &&
               fields[3] == 0 &&
               fields[4] == 0;
    }

    private static ProcessModule GetMainModule()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule
            ?? throw new InvalidOperationException("No main module.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string exportName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    private static partial nint GetProcAddress(nint module, nint ordinal);
}
