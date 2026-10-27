using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal sealed class WindowsFileLease(SafeFileHandle handle) : IServicePackageLease
{
    internal SafeFileHandle Handle { get; } = handle;
    public bool IsHeld => !this.Handle.IsClosed && !this.Handle.IsInvalid;
    internal static WindowsFileLease? TryAcquire(WindowsPrivateDirectory directory, string name, bool shared = false)
    {
        SafeFileHandle file;
        try
        {
            // A shared pin denies write/delete opens for the lifetime of every inherited handle.
            file = WindowsPrivateDirectory.OpenNative(directory.Child(name), shared ? WindowsNative.Read : WindowsNative.Read | WindowsNative.Write,
                shared ? 1u : 3u, 4, directory: false);
        }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: WindowsNative.SharingViolation }) { return null; }
        try
        {
            WindowsFileSecurity.Inspect(file, directory: false);
            if (!shared)
            {
                WindowsNative.Overlapped overlap = default;
                if (!WindowsNative.LockFileEx(file, 1 | 2, 0, 1, 0, ref overlap))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == WindowsNative.LockViolation) return null;
                    throw WindowsNative.Error("acquire ownership", error);
                }
            }
            var lease = new WindowsFileLease(file); file = null!; return lease;
        }
        finally { file?.Dispose(); }
    }
    // Whether another handle holds the exclusive lease. A missing file is not held, and nothing is created.
    internal static bool IsHeldElsewhere(WindowsPrivateDirectory directory, string name)
    {
        SafeFileHandle file;
        try { file = WindowsPrivateDirectory.OpenNative(directory.Child(name), WindowsNative.Read | WindowsNative.Write, 3u, 3, directory: false); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: WindowsNative.Missing }) { return false; }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: WindowsNative.SharingViolation }) { return true; }
        using (file)
        {
            WindowsFileSecurity.Inspect(file, directory: false);
            WindowsNative.Overlapped overlap = default;
            // Closing the handle releases a lock this probe took.
            if (WindowsNative.LockFileEx(file, 1 | 2, 0, 1, 0, ref overlap)) return false;
            int error = Marshal.GetLastPInvokeError();
            if (error == WindowsNative.LockViolation) return true;
            throw WindowsNative.Error("probe ownership", error);
        }
    }
    internal static WindowsFileLease AdoptShared(SafeFileHandle inherited, WindowsPrivateDirectory directory, string name)
    {
        WindowsFileSecurity.Inspect(inherited, directory: false);
        using SafeFileHandle expected = WindowsPrivateDirectory.OpenNative(directory.Child(name), WindowsNative.Read, 1, 3, directory: false);
        if (!WindowsNative.Inspect(inherited, false).SameFile(WindowsNative.Inspect(expected, false)))
            throw new InvalidDataException("The inherited package handle does not identify its permanent lease file.");
        if (!WindowsNative.SetHandleInformation(inherited, 1, 0)) throw WindowsNative.Error("clear inherited package handle flag");
        return new(inherited);
    }
    internal SafeFileHandle DuplicateForChild()
    {
        if (!WindowsNative.DuplicateHandle(WindowsNative.GetCurrentProcess(), this.Handle, WindowsNative.GetCurrentProcess(), out SafeFileHandle duplicate, 0, true, 2))
            throw WindowsNative.Error("duplicate package pin");
        return duplicate;
    }
    public void Dispose() => this.Handle.Dispose();
}
