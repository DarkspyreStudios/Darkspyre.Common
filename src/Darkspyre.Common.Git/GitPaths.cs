using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>Repository-relative paths and object IDs as the Git reads accept them.</summary>
internal static class GitPaths
{
    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>Decodes Git's path bytes exactly; bytes that are not UTF-8 throw <see cref="InvalidDataException"/>.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return Strict.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Git returned a path that is not exact UTF-8.");
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a repository-relative path a Git read accepts: nonempty, not absolute, without
    /// NUL and without an empty, <c>.</c> or <c>..</c> component. Tabs and newlines are ordinary characters.
    /// </summary>
    internal static bool IsRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path[0] == '/') return false;
        foreach (string component in path.Split('/'))
        {
            if (component is "" or "." or "..") return false;
        }
        return true;
    }

    /// <summary>The absolute path of a repository-relative path in the worktree.</summary>
    internal static string Combine(string worktree, string path) =>
        Path.Join(worktree, Path.DirectorySeparatorChar == '/' ? path : path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The number of hexadecimal digits in a full object ID of <paramref name="format"/>.</summary>
    internal static int Width(GitObjectFormat format) => format == GitObjectFormat.Sha256 ? 64 : 40;

    /// <summary>Whether <paramref name="id"/> is a full, lowercase object ID of <paramref name="format"/>.</summary>
    internal static bool IsObjectId(ReadOnlySpan<char> id, GitObjectFormat format)
    {
        if (id.Length != Width(format)) return false;
        foreach (char character in id) if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character)) return false;
        return true;
    }

    /// <summary>The object format Git names, or null for one the host does not support.</summary>
    internal static GitObjectFormat? Format(string name) => name switch
    {
        "sha1" => GitObjectFormat.Sha1,
        "sha256" => GitObjectFormat.Sha256,
        _ => null,
    };
}
