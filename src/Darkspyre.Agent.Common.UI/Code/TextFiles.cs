using System.Text;

namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>Decodes a text file's bytes for the read-only viewers: UTF-8 with or without a byte order mark, and UTF-16 or UTF-32 with one.</summary>
public static class TextFiles
{
    /// <summary>The largest text file the viewers decode, 8 MiB.</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>The text of <paramref name="bytes"/>.</summary>
    /// <exception cref="IOException">The bytes exceed <see cref="MaximumBytes"/> ("File exceeds the 8 MiB text-view
    /// limit."), do not decode ("Unsupported text encoding.") or hold NUL ("Binary content is not supported by the text
    /// viewers.").</exception>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new IOException("File exceeds the 8 MiB text-view limit.");
        Encoding encoding = new UTF8Encoding(false, true);
        int bom = 0;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); bom = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); bom = 4; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); bom = 2; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); bom = 2; }
        string text;
        try { text = encoding.GetString(bytes[bom..]); }
        catch (DecoderFallbackException error) { throw new IOException("Unsupported text encoding.", error); }
        if (text.Contains('\0')) throw new IOException("Binary content is not supported by the text viewers.");
        return text;
    }
}
