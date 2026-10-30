using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>Launches a verified executable in a new session with closed inherited descriptors and a clean environment.</summary>
[SupportedOSPlatform("macos")]
public static class MacDetachedLauncher
{
    /// <summary>Starts a detached service without transferring a package pin.</summary>
    public static DetachedServiceProcess Start(ServiceLaunchSpec spec) => Start(spec, packagePin: null);

    /// <summary>Transfers a duplicate package pin into descriptor 3 before exec, closing the parent-death startup gap.</summary>
    public static DetachedServiceProcess Start(ServiceLaunchSpec spec, ServicePackagePin? packagePin)
    {
        MacNative.RequirePlatform();
        ArgumentNullException.ThrowIfNull(spec);
        ValidateAbsolute(spec.ExecutablePath);
        ValidateAbsolute(spec.UserHome);
        ValidateAbsolute(spec.ScratchDirectory);
        ArgumentNullException.ThrowIfNull(spec.Arguments);
        if (spec.ExecutableSha256 is not { Length: 64 } || !spec.ExecutableSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The installed executable needs a SHA256 identity.", nameof(spec));
        using var retainedPin = packagePin is null ? null : new PinReference(packagePin, spec);
        using MacPrivateDirectory scratch = MacPrivateDirectory.OpenOrCreate(spec.ScratchDirectory);
        int fd = MacNative.Open(spec.ExecutablePath, MacNative.CloseOnExec | MacNative.NoFollowAny);
        if (fd < 0) throw MacNative.Error("open installed executable", Marshal.GetLastPInvokeError());
        using var executable = new SafeFileHandle((nint)fd, ownsHandle: true);
        if (MacNative.Fstat(executable, out MacNative.FileStatus status) != 0)
            throw MacNative.Error("inspect installed executable", Marshal.GetLastPInvokeError());
        if ((status.Mode & 0xf000) != 0x8000 || (status.Mode & 0x12) != 0 || (status.Mode & 0x49) == 0
            || (status.UserId != 0 && status.UserId != MacNative.EffectiveUserId()))
            throw new UnauthorizedAccessException("The installed executable must be a trusted regular executable without group/other write permission.");
        using (var reader = new FileStream(executable, FileAccess.Read))
        {
            string actual = Convert.ToHexString(SHA256.HashData(reader));
            if (!string.Equals(actual, spec.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed service executable does not match its registered digest.");
        }

        using var arguments = new NativeStrings([spec.ExecutablePath, .. spec.Arguments]);
        List<string> variables = [
            $"HOME={spec.UserHome}", $"TMPDIR={spec.ScratchDirectory}/", "PATH=/usr/bin:/bin:/usr/sbin:/sbin", "LANG=en_US.UTF-8"];
        if (packagePin is not null)
        {
            variables.Add($"{MacServiceFileSystem.InheritedPinVariable}={MacServiceFileSystem.InheritedPinDescriptor}");
            variables.Add($"{MacServiceFileSystem.InheritedDigestVariable}={packagePin.BuildDigest}");
            variables.Add($"{MacServiceFileSystem.InheritedCacheVariable}={packagePin.CachePath}");
        }
        using var environment = new NativeStrings([.. variables]);
        using var attributes = new SpawnAttributes();
        using var actions = new SpawnActions();
        MacSpawn.Check(MacSpawn.SetFlags(ref attributes.Value, 0x0400 | 0x4000 | 0x0004 | 0x0008), "set detached spawn flags");
        uint emptyMask = 0;
        uint defaults = (1u << 0) | (1u << 1) | (1u << 12) | (1u << 14); // HUP, INT, PIPE, TERM
        MacSpawn.Check(MacSpawn.SetSignalMask(ref attributes.Value, ref emptyMask), "clear inherited signal mask");
        MacSpawn.Check(MacSpawn.SetSignalDefaults(ref attributes.Value, ref defaults), "reset termination signals");
        MacSpawn.Check(MacSpawn.ChangeDirectory(ref actions.Value, "/"), "set service working directory");
        for (int i = 0; i < 3; i++)
            MacSpawn.Check(MacSpawn.OpenDescriptor(ref actions.Value, i, "/dev/null", i == 0 ? 0 : 1, 0), "isolate service stdio");
        if (packagePin is not null)
            MacSpawn.Check(MacSpawn.DuplicateDescriptor(ref actions.Value, ((MacFileLease)packagePin.Lease).Descriptor, MacServiceFileSystem.InheritedPinDescriptor), "inherit package pin");
        // dup2(3, 3) leaves an adopted descriptor close-on-exec. Explicit inheritance keeps the pin through exec.
        if (packagePin is not null)
            MacSpawn.Check(MacSpawn.InheritDescriptor(ref actions.Value, MacServiceFileSystem.InheritedPinDescriptor), "retain inherited package pin");
        MacSpawn.Check(MacSpawn.Spawn(out int pid, spec.ExecutablePath, ref actions.Value, ref attributes.Value,
            arguments.Pointer, environment.Pointer), "spawn service");
        return new DetachedServiceProcess(pid, ReapAsync(pid));
    }

    private static async Task<ServiceProcessExit> ReapAsync(int pid)
    {
        while (true)
        {
            int result = MacSpawn.WaitPid(pid, out int status, 1); // WNOHANG
            if (result == pid)
            {
                int signal = status & 0x7f;
                return new ServiceProcessExit(pid, signal == 0 ? (status >> 8) & 0xff : null, signal != 0 ? signal : null);
            }
            if (result < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 4) throw MacNative.Error("reap service", error); // EINTR retries the observation, not the launch.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static void ValidateAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || path.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Service launch paths must be absolute and contain no traversal components.", nameof(path));
    }

    private sealed class PinReference : IDisposable
    {
        private readonly SafeFileHandle descriptor;
        private bool retained;
        public PinReference(ServicePackagePin pin, ServiceLaunchSpec spec)
        {
            if (!pin.IsHeld || spec.ExecutablePath != pin.ExecutablePath || !spec.ExecutableSha256.Equals(pin.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The registered launch must match its held package pin.");
            this.descriptor = ((MacFileLease)pin.Lease).Descriptor;
            this.descriptor.DangerousAddRef(ref this.retained);
        }
        public void Dispose() { if (this.retained) { this.retained = false; this.descriptor.DangerousRelease(); } }
    }

    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] strings;
        public NativeStrings(string[] values)
        {
            if (values.Length > 64 || values.Any(value => value is null || value.Contains('\0'))
                || values.Sum(value => (long)Encoding.UTF8.GetByteCount(value) + 1) > 32768)
                throw new ArgumentException("The registered launch arguments exceed their bounds.", nameof(values));
            this.strings = new nint[values.Length];
            this.Pointer = Marshal.AllocHGlobal((values.Length + 1) * IntPtr.Size);
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    this.strings[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
                    Marshal.WriteIntPtr(this.Pointer, i * IntPtr.Size, this.strings[i]);
                }
                Marshal.WriteIntPtr(this.Pointer, values.Length * IntPtr.Size, 0);
            }
            catch { Dispose(); throw; }
        }
        public nint Pointer { get; }
        public void Dispose()
        {
            foreach (nint value in this.strings) if (value != 0) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(this.Pointer);
        }
    }

    private sealed class SpawnAttributes : IDisposable
    {
        public nint Value;
        public SpawnAttributes() => MacSpawn.Check(MacSpawn.InitializeAttributes(out this.Value), "initialize spawn attributes");
        public void Dispose() => MacSpawn.Check(MacSpawn.DestroyAttributes(ref this.Value), "destroy spawn attributes");
    }

    private sealed class SpawnActions : IDisposable
    {
        public nint Value;
        public SpawnActions() => MacSpawn.Check(MacSpawn.InitializeActions(out this.Value), "initialize spawn actions");
        public void Dispose() => MacSpawn.Check(MacSpawn.DestroyActions(ref this.Value), "destroy spawn actions");
    }
}
