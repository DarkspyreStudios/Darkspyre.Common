using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Darkspyre.LocalServices.Packaging;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["finalize", string directory]:
                    string[] manifests = await ServicePackageBuild.RefreshManifestsAsync(directory).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(new { manifests }));
                    return 0;
                case ["capture", string payload, string entryPoint, string output, string metadataPrefix, string layoutId]:
                    await CaptureAsync(payload, entryPoint, output, metadataPrefix, layoutId).ConfigureAwait(false);
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: Darkspyre.LocalServices.Packaging finalize <directory>");
                    Console.Error.WriteLine("       Darkspyre.LocalServices.Packaging capture <payload> <entry point> <output> <metadata prefix> <layout id>");
                    return 2;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    /// <summary>
    /// Writes a subscriber's service manifest and its assembly metadata (prefix.LayoutId and prefix.ClientBuildDigest) into
    /// <paramref name="output"/>. The client digest covers the manifest and the hashes of the inputs listed in inputs.txt.
    /// </summary>
    private static async Task CaptureAsync(string payload, string entryPoint, string output, string prefix, string layoutId)
    {
        ServicePackageManifest captured = await ServicePackageBuild.CaptureAsync(Path.GetFullPath(payload), entryPoint).ConfigureAwait(false);
        // MSBuild copies files and creates their parent directories; empty build directories are not payload resources.
        ServicePackageManifest manifest = captured with
        {
            Directories = captured.Directories.Where(d => captured.Files.Any(f => f.Path.StartsWith(d + "/", StringComparison.Ordinal))).ToArray()
        };
        string inputs = manifest.BuildDigest() + "\n" + string.Join("\n", File.ReadAllLines(Path.Combine(output, "inputs.txt")).Where(File.Exists)
            .Distinct(StringComparer.Ordinal).Select(Hash).Order(StringComparer.Ordinal));
        string clientDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)));
        Write("manifest.json", manifest.CanonicalBytes());
        Write("ServicePackageMetadata.cs", Encoding.UTF8.GetBytes(
            $"[assembly: System.Reflection.AssemblyMetadata(\"{prefix}.LayoutId\", \"{layoutId}\")]\n" +
            $"[assembly: System.Reflection.AssemblyMetadata(\"{prefix}.ClientBuildDigest\", \"{clientDigest}\")]\n"));

        void Write(string name, byte[] bytes)
        {
            string path = Path.Combine(output, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        }
    }

    private static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
