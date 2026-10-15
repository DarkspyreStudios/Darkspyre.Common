using System.Text.Json;

namespace Darkspyre.LocalServices.Packaging;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "finalize")
        {
            Console.Error.WriteLine("Usage: Darkspyre.LocalServices.Packaging finalize <directory>");
            return 2;
        }
        try
        {
            string[] manifests = await ServicePackageBuild.RefreshManifestsAsync(args[1]).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { manifests }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
