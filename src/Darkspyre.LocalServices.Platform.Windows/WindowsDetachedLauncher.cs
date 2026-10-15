using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

internal static class WindowsDetachedLauncher
{
    internal static unsafe DetachedServiceProcess Start(ServiceLaunchSpec spec, ServicePackagePin pin)
    {
        if (!pin.IsHeld || spec.ExecutablePath != pin.ExecutablePath || !spec.ExecutableSha256.Equals(pin.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The registered launch must match its held package pin.");
        WindowsPrivateDirectory.ValidatePath(spec.UserHome);
        using WindowsPrivateDirectory scratch = WindowsPrivateDirectory.OpenOrCreate(spec.ScratchDirectory);
        using SafeFileHandle executable = WindowsPrivateDirectory.OpenPath(spec.ExecutablePath, directory: false);
        WindowsFileSecurity.Inspect(executable, directory: false, readOnly: true, executable: true);
        using (var reader = new FileStream(executable, FileAccess.Read))
            if (!Convert.ToHexString(SHA256.HashData(reader)).Equals(spec.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed service executable does not match its registered digest.");
        using SafeFileHandle inherited = ((WindowsFileLease)pin.Lease).DuplicateForChild();
        using SafeFileHandle nullFile = OpenNull();
        if (!WindowsNative.SetHandleInformation(nullFile, 1, 1)) throw WindowsNative.Error("prepare isolated standard streams");
        nint[] handles = [inherited.DangerousGetHandle(), nullFile.DangerousGetHandle()];
        nuint size = 0;
        WindowsSpawn.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0) throw WindowsNative.Error("size inherited handle list");
        nint attributes = Marshal.AllocHGlobal(checked((nint)size));
        bool initialized = false;
        nint command = 0, environment = 0;
        try
        {
            if (!WindowsSpawn.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw WindowsNative.Error("initialize inherited handle list");
            initialized = true;
            fixed (nint* handleList = handles)
            {
                if (!WindowsSpawn.UpdateProcThreadAttribute(attributes, 0, 0x20002, (nint)handleList, (nuint)(handles.Length * IntPtr.Size), 0, 0))
                    throw WindowsNative.Error("set inherited handle list");
                string commandLine = string.Join(' ', new[] { spec.ExecutablePath }.Concat(spec.Arguments).Select(QuoteArgument));
                if (commandLine.Length >= 32767) throw new ArgumentException("The registered launch arguments exceed the Windows command line limit.", nameof(spec));
                command = Marshal.StringToHGlobalUni(commandLine);
                string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] variables = [
                    $"{WindowsServiceFileSystem.InheritedPinVariable}={inherited.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)}",
                    $"{WindowsServiceFileSystem.InheritedDigestVariable}={pin.BuildDigest}",
                    $"{WindowsServiceFileSystem.InheritedCacheVariable}={pin.CachePath}",
                    $"USERPROFILE={spec.UserHome}", $"TEMP={scratch.Path}", $"TMP={scratch.Path}",
                    $"SystemRoot={systemRoot}", $"PATH={Path.Combine(systemRoot, "System32")}"];
                environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Order(StringComparer.OrdinalIgnoreCase)) + "\0\0");
                var startup = new WindowsSpawn.StartupInfoEx
                {
                    Startup = new WindowsSpawn.StartupInfo { Size = (uint)Marshal.SizeOf<WindowsSpawn.StartupInfoEx>(), Flags = 0x100,
                        Input = nullFile.DangerousGetHandle(), Output = nullFile.DangerousGetHandle(), Error = nullFile.DangerousGetHandle() },
                    Attributes = attributes,
                };
                if (!WindowsSpawn.IsProcessInJob(WindowsNative.GetCurrentProcess(), 0, out bool inJob)) throw WindowsNative.Error("inspect launch job");
                uint flags = 0x8 | 0x200 | 0x400 | 0x80000; // Detached, new process group, Unicode environment, extended startup.
                if (inJob) flags |= 0x01000000; // A job must permit breakaway; failure is an unavailable launch.
                if (!WindowsSpawn.CreateProcess(spec.ExecutablePath, command, 0, 0, true, flags, environment, systemRoot, ref startup, out WindowsSpawn.ProcessInformation process))
                    throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "Local service protocol failed: Unavailable.", WindowsNative.Error("launch independent service"));
                using var thread = new SafeWaitHandle(process.Thread, ownsHandle: true);
                var observation = new SafeProcessHandle(process.Process, ownsHandle: true);
                return new(checked((int)process.ProcessId), ObserveAsync(observation, checked((int)process.ProcessId)));
            }
        }
        finally
        {
            if (command != 0) Marshal.FreeHGlobal(command);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            if (initialized) WindowsSpawn.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }
    private static SafeFileHandle OpenNull()
    {
        var security = new WindowsNative.SecurityAttributes { Length = (uint)Marshal.SizeOf<WindowsNative.SecurityAttributes>() };
        SafeFileHandle file = WindowsNative.CreateFile("NUL", WindowsNative.Read | WindowsNative.Write, 3, in security, 3, 0, 0);
        if (!file.IsInvalid) return file;
        int error = Marshal.GetLastPInvokeError(); file.Dispose(); throw WindowsNative.Error("open isolated standard streams", error);
    }
    internal static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("A launch argument cannot contain a null character.", nameof(value));
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            text.Append(character); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static async Task<ServiceProcessExit> ObserveAsync(SafeProcessHandle process, int processId)
    {
        using (process)
        {
            while (true)
            {
                uint state = WindowsSpawn.WaitForSingleObject(process, 0);
                if (state == 0)
                {
                    if (!WindowsSpawn.GetExitCodeProcess(process, out uint exitCode)) throw WindowsNative.Error("read service exit");
                    return new(processId, unchecked((int)exitCode), null);
                }
                if (state != 258) throw WindowsNative.Error("observe service exit");
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
    }
}
