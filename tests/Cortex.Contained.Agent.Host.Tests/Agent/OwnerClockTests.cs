using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class OwnerClockTests
{
    private static OwnerClock Berlin(DateTimeOffset utc) =>
        new(new FakeTimeProvider(utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

    [Fact]
    public void FormatStamp_WinterTime_UsesStandardOffset()
    {
        var clock = Berlin(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));

        Assert.Equal("[Current time: Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]", clock.FormatStamp());
    }

    [Fact]
    public void FormatStamp_SummerTime_UsesDaylightOffset()
    {
        var clock = Berlin(new DateTimeOffset(2026, 7, 15, 12, 5, 0, TimeSpan.Zero));

        Assert.Equal("[Current time: Wednesday 15 July 2026, 14:05 (Europe/Berlin, UTC+02:00)]", clock.FormatStamp());
    }

    [Fact]
    public void FormatStamp_AcrossSpringForward_OffsetFollowsTheInstant()
    {
        // 2026-03-29 01:00 UTC is the instant Berlin jumps from 02:00 CET to 03:00 CEST.
        var before = Berlin(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero)).FormatStamp();
        var after = Berlin(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero)).FormatStamp();

        Assert.Equal("[Current time: Sunday 29 March 2026, 01:59 (Europe/Berlin, UTC+01:00)]", before);
        Assert.Equal("[Current time: Sunday 29 March 2026, 03:00 (Europe/Berlin, UTC+02:00)]", after);
    }

    [Fact]
    public void FormatStamp_SecondsAreDropped_SoTheStampChangesOnlyOncePerMinute()
    {
        var a = Berlin(new DateTimeOffset(2026, 11, 22, 15, 45, 1, TimeSpan.Zero)).FormatStamp();
        var b = Berlin(new DateTimeOffset(2026, 11, 22, 15, 45, 59, TimeSpan.Zero)).FormatStamp();

        Assert.Equal(a, b);
    }

    [Fact]
    public void FormatStamp_FractionalOffsetZone_ShowsMinutes()
    {
        var clock = new OwnerClock(
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero)),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        Assert.Equal("[Current time: Sunday 22 November 2026, 21:15 (Asia/Kolkata, UTC+05:30)]", clock.FormatStamp());
    }

    [Fact]
    public void FormatStamp_WindowsZoneId_IsShownAsIana()
    {
        // TimeZoneInfo.Local on a Windows host carries a Windows id; the model should still see IANA.
        var windows = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var clock = new OwnerClock(
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero)), windows);

        Assert.Contains("(Europe/", clock.FormatStamp(), StringComparison.Ordinal);
        Assert.DoesNotContain("Standard Time", clock.FormatStamp(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStamp_Utc_IsLabelledUtc()
    {
        var clock = new OwnerClock(
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

        Assert.Equal("[Current time: Sunday 22 November 2026, 15:45 (UTC, UTC+00:00)]", clock.FormatStamp());
    }

    [Fact]
    public void FormatStamp_IsInvariantEnglishRegardlessOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var clock = Berlin(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));

            Assert.StartsWith("[Current time: Sunday 22 November 2026", clock.FormatStamp(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FromHistoryOptions_UsesTheConfiguredHistoryZone()
    {
        var options = new HistoryOptions { TimeZone = "Europe/Berlin" };
        var clock = OwnerClock.FromHistoryOptions(
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)), options);

        Assert.Equal("[Current time: Wednesday 15 July 2026, 14:00 (Europe/Berlin, UTC+02:00)]", clock.FormatStamp());
    }

    [Fact]
    public void FromHistoryOptions_EmptyZone_UsesTheMachineLocalZone()
    {
        var clock = OwnerClock.FromHistoryOptions(TimeProvider.System, new HistoryOptions());

        Assert.Equal(TimeZoneInfo.Local, clock.Zone);
    }

    [Fact]
    public void FromHistoryOptions_UnknownZone_FallsBackToUtcInsteadOfFailingStartup()
    {
        var clock = OwnerClock.FromHistoryOptions(TimeProvider.System, new HistoryOptions { TimeZone = "Mars/Olympus" });

        Assert.Equal(TimeZoneInfo.Utc, clock.Zone);
    }

    [Fact]
    public void AppendSystemPromptNote_Enabled_AddsTheStaticExplanation()
    {
        var clock = Berlin(DateTimeOffset.UnixEpoch);

        var result = clock.AppendSystemPromptNote("base prompt");

        Assert.StartsWith("base prompt", result, StringComparison.Ordinal);
        Assert.Contains("[Current time: ...]", result, StringComparison.Ordinal);
        Assert.Contains("authoritative", result, StringComparison.Ordinal);
        Assert.Contains("earlier messages", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendSystemPromptNote_IsIdenticalForEveryTime()
    {
        var first = Berlin(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).AppendSystemPromptNote("p");
        var second = Berlin(new DateTimeOffset(2026, 8, 9, 10, 11, 0, TimeSpan.Zero)).AppendSystemPromptNote("p");

        Assert.Equal(first, second);
    }

    [Fact]
    public void AppendSystemPromptNote_Disabled_LeavesThePromptUntouched()
    {
        var clock = new OwnerClock(TimeProvider.System, TimeZoneInfo.Utc, stampEnabled: false);

        Assert.Equal("base prompt", clock.AppendSystemPromptNote("base prompt"));
    }
}
