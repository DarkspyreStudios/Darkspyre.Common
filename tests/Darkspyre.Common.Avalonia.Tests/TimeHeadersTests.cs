using System.Globalization;
using Darkspyre.Agent.Common.UI.Transcript;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

/// <summary>Recorded message times produce the shared transcript's day and gap headers.</summary>
public sealed class TimeHeadersTests
{
    /// <summary>Untimed messages do not interrupt the previous recorded time.</summary>
    [Fact]
    public void HeadersFollowTimedGapsAndLocalDays()
    {
        DateTimeOffset nine = new(DateTime.Today.AddHours(9));
        IReadOnlyList<TranscriptLayout.Row> rows = TranscriptLayout.Build([
            Message("assistant", 0, null),
            Message("user", 1, nine),
            Message("assistant", 2, nine.AddMinutes(5)),
            Message("notice", 3, null),
            Message("assistant", 4, nine.AddHours(2).AddMinutes(40)),
            Message("user", 5, nine.AddDays(1)),
        ]);

        Assert.Equal([nine, nine.AddHours(2).AddMinutes(40), nine.AddDays(1)],
            rows.Where(row => row.TimeHeader).Select(row => row.Message!.Created!.Value));
        Assert.Equal([("time", 1), ("time", 4), ("time", 5)], rows.Where(row => row.TimeHeader).Select(row => row.Key));
    }

    /// <summary>A time boundary splits a work stretch without losing its absolute row keys.</summary>
    [Fact]
    public void ActivitySplitsAtTheSameBoundaryAndKeepsAbsoluteKeys()
    {
        DateTimeOffset nine = new(DateTime.Today.AddHours(9));
        TranscriptMessage[] messages = [Message("reasoning", 0, nine), Message("tool", 1, nine.AddMinutes(5)),
            Message("reasoning", 2, nine.AddMinutes(20))];
        IReadOnlyList<TranscriptLayout.Row> rows = TranscriptLayout.Build(messages, "Still working", busy: true);
        Assert.Equal([("time", 0), ("activity", 0), ("time", 2), ("activity", 2)], rows.Select(row => row.Key));
        Assert.Equal(2, rows[1].Activity!.Count);
        Assert.Equal(2, rows[3].Activity!.Count);
        Assert.True(rows[3].Live);
        Assert.Null(rows[3].Activity![1].Created);

        IReadOnlyList<TranscriptLayout.Row> extended = TranscriptLayout.Build(
            [Message("user", -1, nine.AddHours(-1)), .. messages], "Still working", busy: true);
        Assert.Equal(rows.Select(row => row.Key), extended.Skip(2).Select(row => row.Key));
    }

    /// <summary>The date label uses the owner's time-of-day text without replacing its format.</summary>
    [Fact]
    public void LabelsUseTheOwnersTimeFormatter()
    {
        DateTimeOffset now = new(DateTime.Today.AddHours(12));
        static string Clock(DateTimeOffset at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal("Today 12:00", TranscriptTimeHeader.Format(now, now, Clock));
        Assert.Equal("Yesterday 12:00", TranscriptTimeHeader.Format(now.AddDays(-1), now, Clock));
        DateTimeOffset weekday = now.AddDays(-6);
        Assert.Equal($"{weekday.LocalDateTime.ToString("dddd", CultureInfo.CurrentCulture)} 12:00",
            TranscriptTimeHeader.Format(weekday, now, Clock));
        DateTimeOffset older = now.AddDays(-7);
        Assert.Equal($"{older.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture)} 12:00",
            TranscriptTimeHeader.Format(older, now, Clock));
    }

    private static TranscriptMessage Message(string role, int position, DateTimeOffset? at) =>
        new(role, "text", null, position, null, false, null, [], [], null, null) { Created = at };
}
