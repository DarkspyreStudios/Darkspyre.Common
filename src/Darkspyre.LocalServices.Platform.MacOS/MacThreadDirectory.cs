using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

// A socket path longer than Darwin's 104-byte limit cannot be bound or connected. The calling thread's own working folder is
// moved to the runtime directory for one call, so the call names the socket by its short file name. Other threads keep the
// process's working folder.
[SupportedOSPlatform("macos")]
internal static class MacThreadDirectory
{
    internal static T Run<T>(MacPrivateDirectory directory, Func<T> action)
    {
        SafeFileHandle handle = directory.Handle;
        bool added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            if (MacNative.ThreadChangeDirectory((int)handle.DangerousGetHandle()) != 0)
                throw MacNative.Error("pthread_fchdir_np", Marshal.GetLastPInvokeError());
            T result;
            try { result = action(); }
            catch
            {
                Reset();
                throw;
            }
            Reset();
            return result;
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    // A thread left in the runtime directory would resolve every later relative path there, so a failed reset throws.
    private static void Reset()
    {
        if (MacNative.ThreadChangeDirectory(-1) != 0)
            throw MacNative.Error("pthread_fchdir_np reset", Marshal.GetLastPInvokeError());
    }
}
