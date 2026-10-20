using System.Globalization;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Byte counts as text for the person reading them.</summary>
public static class Sizes
{
    private const double Megabyte = 1024 * 1024;
    private const double Gigabyte = 1024 * Megabyte;

    /// <summary>A byte count in MB below 1 GB and in GB from 1 GB up, with one decimal, such as "12.5 MB" or "3.2 GB".</summary>
    public static string Format(long bytes) => bytes >= Gigabyte
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / Gigabyte:0.0} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / Megabyte:0.0} MB");
}
