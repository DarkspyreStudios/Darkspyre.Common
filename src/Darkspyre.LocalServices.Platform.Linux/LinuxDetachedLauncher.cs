using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices.Platform.Linux;

/// <summary>Launches a verified executable in a new session with closed inherited descriptors and a clean environment.</summary>
internal static class LinuxDetachedLauncher
{
    public static DetachedServiceProcess Start(ServiceLaunchSpec spec) => Start(spec, packagePin: null);

    /// <summary>Transfers a duplicate package pin into descriptor 3 before exec, closing the parent-death startup gap.</summary>
    public static DetachedServiceProcess Start(ServiceLaunchSpec spec, ServicePackagePin? packagePin)
    {
        LinuxNative.RequirePlatform();
        ArgumentNullException.ThrowIfNull(spec);
        ValidateAbsolute(spec.ExecutablePath);
        ValidateAbsolute(spec.UserHome);
        ValidateAbsolute(spec.ScratchDirectory);
        ArgumentNullException.ThrowIfNull(spec.Arguments);
        if (spec.ExecutableSha256 is not { Length: 64 } || !spec.ExecutableSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The installed executable needs a SHA256 identity.", nameof(spec));
        using var retainedPin = packagePin is null ? null : new PinReference(packagePin, spec);
        using LinuxPrivateDirectory scratch = LinuxPrivateDirectory.OpenOrCreate(spec.ScratchDirectory);
        using var executable = LinuxPrivateDirectory.OpenPath(spec.ExecutablePath, directory: false);
        if (LinuxNative.Fstat(executable, out LinuxNative.FileStatus status) != 0)
            throw LinuxNative.Error("inspect installed executable", Marshal.GetLastPInvokeError());
        if ((status.Mode & 0xf000) != 0x8000 || (status.Mode & 0x12) != 0 || (status.Mode & 0x49) == 0
            || (status.UserId != 0 && status.UserId != LinuxNative.EffectiveUserId()))
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
            variables.Add($"{LinuxServiceFileSystem.InheritedPinVariable}={LinuxServiceFileSystem.InheritedPinDescriptor}");
            variables.Add($"{LinuxServiceFileSystem.InheritedDigestVariable}={packagePin.BuildDigest}");
            variables.Add($"{LinuxServiceFileSystem.InheritedCacheVariable}={packagePin.CachePath}");
        }
        using var environment = new NativeStrings([.. variables]);
        using var attributes = new SpawnAttributes();
        using var actions = new SpawnActions();
        LinuxSpawn.Check(LinuxSpawn.SetFlags(attributes.Value, 0x0080 | 0x0004 | 0x0008), "set detached spawn flags");
        using var signals = new NativeBuffer(128);
        LinuxSpawn.Check(LinuxSpawn.SetSignalMask(attributes.Value, signals.Value), "clear inherited signal mask");
        Marshal.WriteInt64(signals.Value, (1L << 0) | (1L << 1) | (1L << 12) | (1L << 14));
        LinuxSpawn.Check(LinuxSpawn.SetSignalDefaults(attributes.Value, signals.Value), "reset termination signals");
        LinuxSpawn.Check(LinuxSpawn.ChangeDirectory(actions.Value, "/"), "set service working directory");
        for (int i = 0; i < 3; i++)
            LinuxSpawn.Check(LinuxSpawn.OpenDescriptor(actions.Value, i, "/dev/null", i == 0 ? 0 : 1, 0), "isolate service stdio");
        if (packagePin is not null)
            LinuxSpawn.Check(LinuxSpawn.DuplicateDescriptor(actions.Value, ((LinuxFileLease)packagePin.Lease).Descriptor, LinuxServiceFileSystem.InheritedPinDescriptor), "inherit package pin");
        LinuxSpawn.Check(LinuxSpawn.CloseFrom(actions.Value, packagePin is null ? 3 : 4), "close inherited descriptors");
        LinuxSpawn.Check(LinuxSpawn.Spawn(out int pid, spec.ExecutablePath, actions.Value, attributes.Value,
            arguments.Pointer, environment.Pointer), "spawn service");
        return new DetachedServiceProcess(pid, ReapAsync(pid));
    }

    private static async Task<ServiceProcessExit> ReapAsync(int pid)
    {
        while (true)
        {
            int result = LinuxSpawn.WaitPid(pid, out int status, 1); // WNOHANG
            if (result == pid)
            {
                int signal = status & 0x7f;
                return new ServiceProcessExit(pid, signal == 0 ? (status >> 8) & 0xff : null, signal != 0 ? signal : null);
            }
            if (result < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 4) throw LinuxNative.Error("reap service", error); // EINTR retries the observation, not the launch.
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
            this.descriptor = ((LinuxFileLease)pin.Lease).Descriptor;
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
        private readonly NativeBuffer buffer = new(336);
        public nint Value => this.buffer.Value;
        public SpawnAttributes() => LinuxSpawn.Check(LinuxSpawn.InitializeAttributes(this.Value), "initialize spawn attributes");
        public void Dispose() { try { LinuxSpawn.Check(LinuxSpawn.DestroyAttributes(this.Value), "destroy spawn attributes"); } finally { this.buffer.Dispose(); } }
    }
    private sealed class SpawnActions : IDisposable
    {
        private readonly NativeBuffer buffer = new(80);
        public nint Value => this.buffer.Value;
        public SpawnActions() => LinuxSpawn.Check(LinuxSpawn.InitializeActions(this.Value), "initialize spawn actions");
        public void Dispose() { try { LinuxSpawn.Check(LinuxSpawn.DestroyActions(this.Value), "destroy spawn actions"); } finally { this.buffer.Dispose(); } }
    }
    private sealed unsafe class NativeBuffer : IDisposable
    {
        public NativeBuffer(nuint size) => this.Value = (nint)NativeMemory.AllocZeroed(size);
        public nint Value { get; }
        public void Dispose() => NativeMemory.Free((void*)this.Value);
    }
}
