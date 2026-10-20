using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Darkspyre.Agent.Common.UI.Markdown;

/// <summary>
/// Turns Mermaider's themed SVG into plain SVG that a renderer without CSS custom properties can draw. Mermaider declares colours and
/// sizes as custom properties (<c>--fg</c>, <c>--_node-fill</c>), uses <c>var()</c> with fallbacks, and mixes colours with
/// <c>color-mix(in srgb, ...)</c>. This resolves every <c>var()</c> and <c>color-mix()</c> to a literal and converts <c>rem</c> to pixels.
/// </summary>
internal static partial class SvgCssFlattener
{
    private const double RemPixels = 16;

    [GeneratedRegex("(--[\\w-]+)\\s*:\\s*([^;}\"]+?)\\s*(?=;|}|\"|$)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"filter\s*:\s*drop-shadow\((?:[^()]|\([^()]*\))*\)\s*;")]
    private static partial Regex CssFilter();

    [GeneratedRegex(@"(-?\d*\.?\d+)rem\b")]
    private static partial Regex Rem();

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex Hex();

    [GeneratedRegex(@"\s+(\d*\.?\d+)%$")]
    private static partial Regex Percent();

    [GeneratedRegex("<svg[^>]*?\\sstyle=\"([^\"]*)\"")]
    private static partial Regex RootStyle();

    /// <summary>Returns the SVG with every custom property reference resolved.</summary>
    internal static string Flatten(string svg)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        // Declarations in the svg rule come first; the root element's style attribute is more specific and wins.
        foreach (Match match in Declaration().Matches(svg)) properties[match.Groups[1].Value] = match.Groups[2].Value;
        Match rootStyle = RootStyle().Match(svg);
        if (rootStyle.Success)
            foreach (Match match in Declaration().Matches(rootStyle.Groups[1].Value)) properties[match.Groups[1].Value] = match.Groups[2].Value;

        string resolved = Evaluate(svg, properties, 0);
        // A CSS drop-shadow filter makes Svg.Skia skip the filtered element, so the boxes would not draw. The shadow is decoration.
        resolved = CssFilter().Replace(resolved, string.Empty);
        resolved = Rem().Replace(resolved, match => (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * RemPixels).ToString("0.##", CultureInfo.InvariantCulture) + "px");
        return resolved;
    }

    /// <summary>Replaces each <c>var(...)</c> and <c>color-mix(...)</c> call in the text, innermost first through recursion.</summary>
    private static string Evaluate(string text, Dictionary<string, string> properties, int depth)
    {
        if (depth > 24) return text;
        StringBuilder result = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int var = text.IndexOf("var(", i, StringComparison.Ordinal);
            int mix = text.IndexOf("color-mix(", i, StringComparison.Ordinal);
            int next = var < 0 ? mix : mix < 0 ? var : Math.Min(var, mix);
            if (next < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, next - i);
            bool isVar = next == var;
            int open = next + (isVar ? 3 : 9);
            int close = Match(text, open);
            if (close < 0)
            {
                result.Append(text, next, text.Length - next);
                break;
            }
            string inner = text.Substring(open + 1, close - open - 1);
            result.Append(isVar ? ResolveVar(inner, properties, depth) : ResolveMix(inner, properties, depth));
            i = close + 1;
        }
        return result.ToString();
    }

    private static string ResolveVar(string inner, Dictionary<string, string> properties, int depth)
    {
        List<string> parts = SplitTopLevel(inner, 2);
        string name = parts[0].Trim();
        if (properties.TryGetValue(name, out string? value)) return Evaluate(value, properties, depth + 1);
        return parts.Count > 1 ? Evaluate(parts[1].Trim(), properties, depth + 1) : "currentColor";
    }

    private static string ResolveMix(string inner, Dictionary<string, string> properties, int depth)
    {
        // color-mix(in srgb, <colour> <percent>?, <colour> <percent>?)
        List<string> parts = SplitTopLevel(Evaluate(inner, properties, depth + 1), 3);
        if (parts.Count != 3 || !parts[0].Trim().StartsWith("in srgb", StringComparison.Ordinal)) return "#808080";
        (double[] first, double? firstShare) = ParseStop(parts[1]);
        (double[] second, double? secondShare) = ParseStop(parts[2]);
        double weight = firstShare ?? (secondShare is { } s ? 1 - s : 0.5);
        double[] mixed = [.. first.Zip(second, (a, b) => a * weight + b * (1 - weight))];
        return "#" + string.Concat(mixed.Select(channel => Math.Clamp((int)Math.Round(channel), 0, 255).ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static (double[] Rgb, double? Share) ParseStop(string stop)
    {
        string text = stop.Trim();
        double? share = null;
        Match percent = Percent().Match(text);
        if (percent.Success)
        {
            share = double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
            text = text[..percent.Index].Trim();
        }
        return (ParseColour(text), share);
    }

    private static double[] ParseColour(string text)
    {
        Match hex = Hex().Match(text);
        if (!hex.Success) return [128, 128, 128];
        string digits = hex.Groups[1].Value;
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => new string(c, 2)));
        return [Convert.ToInt32(digits[..2], 16), Convert.ToInt32(digits[2..4], 16), Convert.ToInt32(digits[4..6], 16)];
    }

    /// <summary>Index of the parenthesis that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int Match(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Splits on commas outside parentheses; the last part keeps any further commas when <paramref name="max"/> is reached.</summary>
    private static List<string> SplitTopLevel(string text, int max)
    {
        List<string> parts = [];
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length && parts.Count < max - 1; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
