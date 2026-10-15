using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darkspyre.LocalServices;

internal sealed class ServicePackageFiles(ServiceFileSystem fileSystem)
{
    /// <summary>Captures a trusted build output inventory. A distribution must register/verify the returned digest.</summary>
    internal async Task<ServicePackageManifest> CaptureAsync(string sourceDirectory, string entryPoint, CancellationToken cancellationToken = default)
    {
        string source = Root(sourceDirectory);
        var inventory = Inventory(source, cached: false);
        List<ServicePackageFile> files = [];
        foreach (string path in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = fileSystem.Open(Path.Combine(source, path), directory: false);
            using var input = new FileStream(handle, FileAccess.Read);
            long length = input.Length;
            string hash = await ServicePackageFiles.HashAsync(input, length, null, cancellationToken).ConfigureAwait(false);
            files.Add(new(path, length, hash, fileSystem.IsExecutable(Path.Combine(source, path))));
        }
        var manifest = new ServicePackageManifest(ServicePackageManifest.CurrentFormat, entryPoint, inventory.Directories, [.. files]);
        manifest.Validate();
        return manifest;
    }

    internal string Root(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Replace(Path.DirectorySeparatorChar, '/').Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("A service payload requires an absolute path without traversal.", nameof(path));
        using SafeFileHandle directory = fileSystem.Open(path, directory: true);
        return Path.GetFullPath(path);
    }

    internal (string[] Directories, string[] Files) Inventory(string root, bool cached)
    {
        List<string> directories = [], files = [];
        Scan(root);
        return ([.. directories.Order(StringComparer.Ordinal)], [.. files.Order(StringComparer.Ordinal)]);
        void Scan(string directory)
        {
            using SafeFileHandle handle = fileSystem.Open(directory, directory: true);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Normalize(NormalizationForm.FormC);
                if (cached && relative == ServicePackageManifest.FileName) continue;
                if (directories.Count + files.Count >= 16384) throw new InvalidDataException("The package has too many entries.");
                ServicePackageManifest.ValidatePath(relative);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Service payloads cannot contain symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (relative.Count(character => character == '/') >= 32) throw new InvalidDataException("The package exceeds its directory-depth limit.");
                    directories.Add(relative);
                    Scan(path);
                }
                else
                {
                    using SafeFileHandle file = fileSystem.Open(path, directory: false);
                    files.Add(relative);
                }
            }
        }
    }

    internal static async Task<string> HashAsync(Stream input, long length, Stream? output, CancellationToken token)
    {
        if (length is < 0 or > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("The payload file exceeds its size limit.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long copied = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            copied += read;
            if (copied > length) throw new InvalidDataException("A payload file changed length while being read.");
            hash.AppendData(buffer.AsSpan(0, read));
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (copied != length) throw new InvalidDataException("A payload file is truncated.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal async Task VerifyAsync(string root, ServicePackageManifest manifest, bool cached, CancellationToken token)
    {
        var inventory = Inventory(root, cached);
        if (!inventory.Directories.SequenceEqual(manifest.Directories.Order(StringComparer.Ordinal))
            || !inventory.Files.SequenceEqual(manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("The service payload does not match its complete manifest inventory.");
        if (cached)
        {
            foreach (string directory in manifest.Directories.Prepend(""))
            {
                using SafeFileHandle handle = fileSystem.Open(Path.Combine(root, directory), directory: true);
                fileSystem.InspectPrivate(handle, directory: true, readOnly: true);
            }
        }
        foreach (ServicePackageFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using SafeFileHandle handle = fileSystem.Open(Path.Combine(root, file.Path), directory: false);
            if (cached) fileSystem.InspectPrivate(handle, directory: false, readOnly: true, executable: file.Executable);
            else if (fileSystem.IsExecutable(Path.Combine(root, file.Path)) != file.Executable)
                throw new InvalidDataException("The payload executable flags do not match the manifest.");
            using var input = new FileStream(handle, FileAccess.Read);
            string digest = await HashAsync(input, file.Length, null, token).ConfigureAwait(false);
            if (!digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A service payload file failed digest verification.");
        }
    }

    internal void Seal(string root, ServicePackageManifest manifest)
    {
        foreach (ServicePackageFile file in manifest.Files)
            fileSystem.Seal(Path.Combine(root, file.Path), file.Executable);
        fileSystem.Seal(Path.Combine(root, ServicePackageManifest.FileName), false);
        foreach (string directory in manifest.Directories.OrderByDescending(path => path.Length))
            fileSystem.Seal(Path.Combine(root, directory), true);
        fileSystem.Seal(root, true);
    }
}
