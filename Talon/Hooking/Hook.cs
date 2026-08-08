using System.Runtime.InteropServices;

namespace Talon.Hooking;

/// <summary>Controls a hook and exposes its callable original function.</summary>
public abstract class Hook<T> : IDisposable where T : Delegate
{
    protected Hook(nint address) => Address = address;

    /// <summary>Gets the address patched by this hook.</summary>
    public nint Address { get; }

    /// <summary>Gets the trampoline that calls the original function.</summary>
    public abstract T Original { get; }

    /// <summary>Gets an original-function delegate that remains safe after disposal.</summary>
    public T OriginalDisposeSafe =>
        IsDisposed ? Marshal.GetDelegateForFunctionPointer<T>(Address) : Original;

    /// <summary>Gets whether the hook is enabled.</summary>
    public abstract bool IsEnabled { get; }

    /// <summary>Gets whether the hook has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets the name of the selected hook backend.</summary>
    public abstract string BackendName { get; }

    /// <summary>Enables the detour.</summary>
    public abstract void Enable();

    /// <summary>Disables the detour.</summary>
    public abstract void Disable();

    /// <summary>Disables and releases the hook.</summary>
    public virtual void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
    }

    protected void CheckDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
