using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>One configured remote as its configuration names it: its URL and push URL counts, never their values.</summary>
/// <param name="Name">The validated remote name.</param>
/// <param name="Urls">How many <c>remote.&lt;name&gt;.url</c> values are configured.</param>
/// <param name="PushUrls">How many <c>remote.&lt;name&gt;.pushurl</c> values are configured.</param>
/// <param name="LineBreak">True when a configured value holds a line break, so Git's line-per-URL output cannot be split.</param>
internal sealed record GitRemoteConfiguration(string Name, int Urls, int PushUrls, bool LineBreak);

/// <summary>
/// Turns a remote's configuration and Git's rewritten destinations into structured endpoints. A raw URL, a user name and
/// any query never leave this class; an endpoint the host cannot describe safely requires association instead.
/// </summary>
internal static class GitRemoteEndpoints
{
    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>An endpoint whose syntax the host does not describe.</summary>
    internal static GitRemoteEndpoint Unrecognized { get; } = new(GitRemoteTransport.Unrecognized, GitRemoteIdentityStatus.RequiresAssociation,
        null, null, null);

    /// <summary>
    /// The remotes in the output of <c>config --null --get-regexp ^remote\..*\.(url|pushurl)$</c>, in order of first
    /// appearance. Each record is a key, a newline and a value, ended by NUL. A name that is empty, starts with
    /// <c>-</c>, or holds NUL, CR or LF throws <see cref="InvalidDataException"/>, and the name is not echoed.
    /// </summary>
    internal static IReadOnlyList<GitRemoteConfiguration> ParseConfiguration(ReadOnlySpan<byte> output)
    {
        List<GitRemoteConfiguration> remotes = [];
        while (!output.IsEmpty)
        {
            int end = output.IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException("Git returned unterminated remote configuration.");
            ReadOnlySpan<byte> record = output[..end];
            output = output[(end + 1)..];
            int newline = record.IndexOf((byte)'\n');
            ReadOnlySpan<byte> key = newline < 0 ? record : record[..newline];
            ReadOnlySpan<byte> value = newline < 0 ? [] : record[(newline + 1)..];
            bool push = key.EndsWith(".pushurl"u8);
            if (!key.StartsWith("remote."u8) || !push && !key.EndsWith(".url"u8))
                throw new InvalidDataException("Git returned an unexpected remote configuration key.");
            ReadOnlySpan<byte> nameBytes = key["remote.".Length..^(push ? ".pushurl".Length : ".url".Length)];
            string name;
            try { name = Strict.GetString(nameBytes); }
            catch (DecoderFallbackException) { throw UnsafeName(); }
            if (name.Length == 0 || name[0] == '-' || name.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0) throw UnsafeName();
            bool lineBreak = value.IndexOfAny((byte)'\r', (byte)'\n') >= 0;
            int index = remotes.FindIndex(remote => remote.Name == name);
            GitRemoteConfiguration current = index < 0 ? new(name, 0, 0, false) : remotes[index];
            current = current with
            {
                Urls = current.Urls + (push ? 0 : 1),
                PushUrls = current.PushUrls + (push ? 1 : 0),
                LineBreak = current.LineBreak || lineBreak,
            };
            if (index < 0) remotes.Add(current);
            else remotes[index] = current;
        }
        return remotes;
    }

    /// <summary>
    /// The endpoints in the output of <c>remote get-url --all</c>: one LF-terminated destination per line, in Git's order.
    /// A line that is not exact UTF-8 requires association.
    /// </summary>
    internal static async Task<IReadOnlyList<GitRemoteEndpoint>> ParseDestinationsAsync(byte[] output, string worktree, IGitPathPolicy paths,
        CancellationToken cancellationToken)
    {
        if (output.Length == 0 || output[^1] != '\n')
            throw new InvalidDataException("Git returned incomplete remote destinations.");
        List<GitRemoteEndpoint> endpoints = [];
        int start = 0;
        while (start < output.Length)
        {
            int end = Array.IndexOf(output, (byte)'\n', start);
            string url;
            try { url = Strict.GetString(output, start, end - start); }
            catch (DecoderFallbackException)
            {
                endpoints.Add(Unrecognized);
                start = end + 1;
                continue;
            }
            endpoints.Add(await SanitizeAsync(url, worktree, paths, cancellationToken).ConfigureAwait(false));
            start = end + 1;
        }
        return endpoints;
    }

    /// <summary>
    /// One destination as structured fields. HTTP and HTTPS drop user information, normalize the host to lowercase ASCII,
    /// keep an explicit port and the escaped path as written, and require association for a query or fragment. SSH and SCP
    /// accept no user or the user <c>git</c>, drop it, and keep the path's spelling and its leading slash or its absence.
    /// A local path resolves against the worktree and is described only when the path policy lets the caller read its target.
    /// Anything else, including a remote helper, requires association.
    /// </summary>
    internal static async Task<GitRemoteEndpoint> SanitizeAsync(string url, string worktree, IGitPathPolicy paths,
        CancellationToken cancellationToken)
    {
        if (url.Length == 0 || url.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0) return Unrecognized;
        int scheme = 0;
        while (scheme < url.Length && (char.IsAsciiLetter(url[scheme]) || scheme > 0 && (char.IsAsciiDigit(url[scheme]) || url[scheme] is '+' or '-' or '.')))
            scheme++;
        // Git hands "<transport>::<address>" to a remote helper.
        if (scheme > 0 && url.AsSpan(scheme).StartsWith("::", StringComparison.Ordinal)) return Unrecognized;
        if (scheme > 0 && url.AsSpan(scheme).StartsWith("://", StringComparison.Ordinal))
            return url[..scheme] switch
            {
                "https" => Web(url, GitRemoteTransport.Https, scheme + 3),
                "http" => Web(url, GitRemoteTransport.Http, scheme + 3),
                "ssh" or "git+ssh" or "ssh+git" => SshUrl(url, scheme + 3),
                "file" => await FileUrlAsync(url, scheme + 3, worktree, paths, cancellationToken).ConfigureAwait(false),
                _ => Unrecognized,
            };
        // Git's rule: a colon with no slash before it is the SCP-like form; anything else is a local path.
        int colon = url.IndexOf(':', StringComparison.Ordinal);
        int slash = url.IndexOf('/', StringComparison.Ordinal);
        if (colon >= 0 && (slash < 0 || colon < slash)) return Scp(url);
        return await LocalAsync(url, worktree, paths, cancellationToken).ConfigureAwait(false);
    }

    private static GitRemoteEndpoint Web(string url, GitRemoteTransport transport, int authorityStart)
    {
        GitRemoteEndpoint refused = new(transport, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
        // Dropping a query or fragment could merge distinct destinations, so neither is described.
        if (url.IndexOfAny(['?', '#']) >= 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return refused;
        int pathStart = url.IndexOf('/', authorityStart);
        string authority = pathStart < 0 ? url[authorityStart..] : url[authorityStart..pathStart];
        string path = pathStart < 0 ? "" : url[pathStart..];
        string hostAndPort = authority[(authority.LastIndexOf('@') + 1)..];
        if (!TrySplitPort(hostAndPort, out _, out int? port) || !IsEscapedPath(path) || parsed.IdnHost.Length == 0) return refused;
        return new(transport, GitRemoteIdentityStatus.Parsed, parsed.IdnHost.ToLowerInvariant(), port, path);
    }

    private static GitRemoteEndpoint SshUrl(string url, int authorityStart)
    {
        GitRemoteEndpoint refused = new(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
        int pathStart = url.IndexOf('/', authorityStart);
        string authority = pathStart < 0 ? url[authorityStart..] : url[authorityStart..pathStart];
        int at = authority.LastIndexOf('@');
        if (at >= 0 && authority[..at] != "git") return refused;
        if (!TrySplitPort(authority[(at + 1)..], out string host, out int? port) || !TryHost(host, out string? normalized)) return refused;
        return new(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.Parsed, normalized, port, pathStart < 0 ? "" : url[pathStart..]);
    }

    private static GitRemoteEndpoint Scp(string url)
    {
        GitRemoteEndpoint refused = new(GitRemoteTransport.Scp, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
        // [user@]host:path, where only a bracketed host may hold a colon: the separator is the first colon outside brackets.
        int colon = -1;
        bool bracketed = false;
        for (int index = 0; index < url.Length && colon < 0; index++)
        {
            if (url[index] == '[') bracketed = true;
            else if (url[index] == ']') bracketed = false;
            else if (url[index] == ':' && !bracketed) colon = index;
        }
        if (colon < 0) return refused;
        string userAndHost = url[..colon];
        int at = userAndHost.LastIndexOf('@');
        if (at >= 0 && userAndHost[..at] != "git") return refused;
        if (!TryHost(userAndHost[(at + 1)..], out string? normalized)) return refused;
        return new(GitRemoteTransport.Scp, GitRemoteIdentityStatus.Parsed, normalized, null, url[(colon + 1)..]);
    }

    private static async Task<GitRemoteEndpoint> FileUrlAsync(string url, int pathStart, string worktree, IGitPathPolicy paths,
        CancellationToken cancellationToken)
    {
        // Only file:///absolute/path is described; a host, or escapes the host would have to decode, require association.
        string path = url[pathStart..];
        if (!path.StartsWith('/') || path.Contains('%', StringComparison.Ordinal))
            return new(GitRemoteTransport.File, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
        return await LocalAsync(path, worktree, paths, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitRemoteEndpoint> LocalAsync(string path, string worktree, IGitPathPolicy paths, CancellationToken cancellationToken)
    {
        string target = paths.Resolve(Path.GetFullPath(path, worktree));
        // The canonical target is exposed only to a caller the path policy lets read it.
        return await GitRepositoryReader.PermitsAsync(paths, target, cancellationToken).ConfigureAwait(false)
            ? new(GitRemoteTransport.File, GitRemoteIdentityStatus.Parsed, null, null, target)
            : new(GitRemoteTransport.File, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
    }

    // host, host:port, [address] or [address]:port; a port must be decimal and in range.
    private static bool TrySplitPort(string text, out string host, out int? port)
    {
        port = null;
        host = text;
        int portStart;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return false;
            host = text[..(close + 1)];
            if (close == text.Length - 1) return true;
            if (text[close + 1] != ':') return false;
            portStart = close + 2;
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon < 0) return true;
            host = text[..colon];
            portStart = colon + 1;
        }
        string digits = text[portStart..];
        if (digits.Length == 0) return true;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 65535) return false;
        port = value;
        return true;
    }

    // An SSH or SCP host: a bracketed IPv6 address, or an ASCII host name, which is lowercased. Anything else requires
    // association.
    private static bool TryHost(string host, out string? normalized)
    {
        normalized = null;
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            if (!IPAddress.TryParse(host[1..^1], out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            normalized = address.ToString().ToLowerInvariant();
            return true;
        }
        if (host.Length == 0 || host.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')))
            return false;
        normalized = host.ToLowerInvariant();
        return true;
    }

    // An already-escaped URI path: unreserved and sub-delimiter characters, ':', '@', '/', and well-formed %XX escapes.
    private static bool IsEscapedPath(string path)
    {
        for (int index = 0; index < path.Length; index++)
        {
            char character = path[index];
            if (char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=:@/".Contains(character, StringComparison.Ordinal)) continue;
            if (character == '%' && index + 2 < path.Length && char.IsAsciiHexDigit(path[index + 1]) && char.IsAsciiHexDigit(path[index + 2]))
            {
                index += 2;
                continue;
            }
            return false;
        }
        return true;
    }

    private static InvalidDataException UnsafeName() =>
        new("The repository configures a remote whose name the host refuses.");
}
