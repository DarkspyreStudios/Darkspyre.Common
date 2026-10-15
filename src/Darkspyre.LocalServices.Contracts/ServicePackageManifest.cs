using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.LocalServices;

/// <summary>One verified payload file and its executable metadata.</summary>
/// <param name="Path">The package-relative file path.</param>
/// <param name="Length">The file length in bytes.</param>
/// <param name="Sha256">The expected SHA-256 file digest.</param>
/// <param name="Executable">Whether the file requires executable permissions.</param>
public sealed record ServicePackageFile(string Path, long Length, string Sha256, bool Executable);

/// <summary>A complete immutable payload inventory. The entry point, directory list and file metadata bind the build digest.</summary>
/// <param name="Format">The manifest format version.</param>
/// <param name="EntryPoint">The package-relative service executable.</param>
/// <param name="Directories">The complete package-relative directory inventory.</param>
/// <param name="Files">The complete file inventory.</param>
public sealed record ServicePackageManifest(int Format, string EntryPoint, string[] Directories, ServicePackageFile[] Files)
{
    /// <summary>The supported manifest format.</summary>
    public const int CurrentFormat = 1;
    /// <summary>The maximum accepted manifest size in bytes.</summary>
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    /// <summary>The manifest filename within an immutable package.</summary>
    public const string FileName = ".service-package.json";
    private const int MaximumEntries = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Validates and serializes the inventory into its digest input.</summary>
    public byte[] CanonicalBytes()
    {
        Validate();
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", this.Format);
            writer.WriteString("entryPoint", this.EntryPoint);
            writer.WriteStartArray("directories");
            foreach (string directory in this.Directories.Order(StringComparer.Ordinal)) writer.WriteStringValue(directory);
            writer.WriteEndArray();
            writer.WriteStartArray("files");
            foreach (ServicePackageFile file in this.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256.ToLowerInvariant());
                writer.WriteBoolean("executable", file.Executable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (output.Length > MaximumManifestBytes) throw new InvalidDataException("The service package manifest exceeds its size limit.");
        return output.ToArray();
    }

    /// <summary>Computes the SHA-256 digest of the canonical inventory.</summary>
    public string BuildDigest() => Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes()));

    /// <summary>Parses and validates an encoded inventory.</summary>
    public static ServicePackageManifest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumManifestBytes) throw new InvalidDataException("The service package manifest exceeds its size limit.");
        ServicePackageManifest manifest = JsonSerializer.Deserialize<ServicePackageManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The service package manifest is missing.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>Rejects invalid paths, digests, lengths and inventory structure.</summary>
    public void Validate()
    {
        if (this.Format != CurrentFormat || this.Directories is null || this.Files is null
            || (long)this.Directories.Length + this.Files.Length > MaximumEntries || this.Files.Length == 0)
            throw new InvalidDataException("The service package manifest format or entry count is invalid.");
        ValidatePath(this.EntryPoint);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directories = new(StringComparer.Ordinal);
        foreach (string directory in this.Directories)
        {
            ValidatePath(directory);
            if (!paths.Add(directory)) throw new InvalidDataException("Service package paths collide.");
            directories.Add(directory);
        }
        foreach (ServicePackageFile file in this.Files)
        {
            if (file is null) throw new InvalidDataException("The manifest has an empty file entry.");
            ValidatePath(file.Path);
            if (!paths.Add(file.Path)) throw new InvalidDataException("Service package paths collide.");
            ValidateDigest(file.Sha256);
            if (file.Length is < 0 or > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("A service payload file exceeds its size limit.");
        }
        foreach (string path in paths)
        {
            int slash = path.LastIndexOf('/');
            if (slash >= 0 && !directories.Contains(path[..slash]))
                throw new InvalidDataException("The manifest omits a parent directory.");
        }
        if (!this.Files.Any(file => file.Path == this.EntryPoint && file.Executable))
            throw new InvalidDataException("The manifest entry point must name an executable payload file.");
        if (this.Files.Sum(file => file.Length) > 16L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The service payload exceeds its total size limit.");
    }

    internal static void ValidateDigest(string digest)
    {
        if (digest is not { Length: 64 } || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("A SHA256 build/file identity is required.");
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.IsNormalized(NormalizationForm.FormC) || Encoding.UTF8.GetByteCount(path) > 512
            || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Equals(FileName, StringComparison.OrdinalIgnoreCase)
            || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("Service payload paths must be normalized, unambiguous root-relative names.");
    }
}
