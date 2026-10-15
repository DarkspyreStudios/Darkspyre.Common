using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Windows;

internal static partial class WindowsPipeIdentity
{
    internal static void VerifyClient(NamedPipeServerStream pipe)
    {
        SecurityIdentifier expected = WindowsFileSecurity.User;
        pipe.RunAsClient(() =>
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(ifImpersonating: true)
                ?? throw new AuthenticationException("The service pipe client has no impersonation token.");
            if (identity.User != expected) throw new AuthenticationException("The local service peer belongs to another OS user.");
        });
    }
    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId)) throw WindowsNative.Error("identify pipe server");
        using SafeProcessHandle process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw WindowsNative.Error("open pipe server identity");
        if (!OpenProcessToken(process, 8, out SafeAccessTokenHandle token)) throw WindowsNative.Error("read pipe server token");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User != WindowsFileSecurity.User) throw new AuthenticationException("The local service peer belongs to another OS user.");
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
