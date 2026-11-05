using System.Globalization;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>Groups recorded message times and formats the transcript's local-day headers.</summary>
public static class TranscriptTimeHeader
{
    /// <summary>Formats a recorded time relative to <paramref name="now"/> using the owner's time-of-day formatter.</summary>
    /// <param name="at">The recorded instant.</param>
    /// <param name="now">The instant whose local day is today.</param>
    /// <param name="time">Formats the time of day through the owner's local-time service.</param>
    public static string Format(DateTimeOffset at, DateTimeOffset now, Func<DateTimeOffset, string> time)
    {
        DateTime local = at.LocalDateTime;
        int days = (now.LocalDateTime.Date - local.Date).Days;
        string day = days switch
        {
            0 => "Today",
            1 => "Yesterday",
            >= 2 and <= 6 => local.ToString("dddd", CultureInfo.CurrentCulture),
            _ => local.ToString("MMM d", CultureInfo.CurrentCulture),
        };
        return $"{day} {time(at)}";
    }

    internal static bool BeginsGroup(DateTimeOffset? previous, DateTimeOffset at) => previous is not { } before
        || before.LocalDateTime.Date != at.LocalDateTime.Date
        || at - before >= TimeSpan.FromMinutes(15);
}
