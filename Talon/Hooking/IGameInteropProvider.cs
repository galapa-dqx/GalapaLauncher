using System.Diagnostics;
using Talon.Interop;

namespace Talon.Hooking;

/// <summary>Creates hooks for game code, imports, exports, and signatures.</summary>
public interface IGameInteropProvider
{
    /// <summary>Initializes members marked with <see cref="SignatureAttribute"/>.</summary>
    void InitializeFromAttributes(object self);

    /// <summary>Hooks a function pointer stored at <paramref name="address"/>.</summary>
    Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate;

    /// <summary>Hooks an entry in a module's import address table.</summary>
    Hook<T> HookFromImport<T>(
        ProcessModule? module,
        string moduleName,
        string functionName,
        uint hintOrOrdinal,
        T detour) where T : Delegate;

    /// <summary>Hooks an exported function from a loaded module.</summary>
    Hook<T> HookFromSymbol<T>(
        string moduleName,
        string exportName,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate;

    /// <summary>Hooks code at a signed native address.</summary>
    Hook<T> HookFromAddress<T>(
        nint procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate;

    /// <summary>Hooks code at an unsigned native address.</summary>
    Hook<T> HookFromAddress<T>(
        nuint procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate;

    /// <summary>Hooks code at a native pointer.</summary>
    unsafe Hook<T> HookFromAddress<T>(
        void* procAddress,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate;

    /// <summary>Scans executable code and hooks the resolved address.</summary>
    Hook<T> HookFromSignature<T>(
        string signature,
        T detour,
        HookBackend backend = HookBackend.Automatic) where T : Delegate;
}
