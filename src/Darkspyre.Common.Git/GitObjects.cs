using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>Object existence in the local object store, without fetching.</summary>
internal static class GitObjects
{
    /// <summary>
    /// Whether <paramref name="objectId"/> is in the local object store. <c>--missing=print</c> stops Git from fetching a
    /// missing object; an absent ID prints nothing or a <c>?</c> line.
    /// </summary>
    internal static async Task<bool> IsPresentAsync(GitCommands git, string worktree, string objectId, CancellationToken cancellationToken)
    {
        GitRunResult result = await git.RunReadAsync(worktree,
            ["rev-list", "--objects", "--no-object-names", "--no-walk", "--ignore-missing", "--missing=print", objectId],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        GitOperationChecks.Check(result, "check the object");
        string output = Encoding.ASCII.GetString(result.Output);
        if (output == objectId + "\n") return true;
        if (output.Length == 0 || output == "?" + objectId + "\n") return false;
        throw new IOException("Could not check the object: Git returned an unexpected answer.");
    }
}
