using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class OwnerClockTests
{
    private static OwnerClock Berlin() =>
        new(new FakeTimeProvider(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    [Fact]
    public void FormatStamp_WinterTime_UsesStandardOffset()
    {
        Assert.Equal(
            "[Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]",
            Berlin().FormatStamp(Utc(2026, 11, 22, 15, 45)));
    }

    [Fact]
    public void FormatStamp_SummerTime_UsesDaylightOffset()
    {
        Assert.Equal(
            "[Wednesday 15 July 2026, 14:05 (Europe/Berlin, UTC+02:00)]",
            Berlin().FormatStamp(Utc(2026, 7, 15, 12, 5)));
    }

    [Fact]
    public void FormatStamp_AcrossSpringForward_OffsetFollowsTheInstant()
    {
        // 2026-03-29 01:00 UTC is the instant Berlin jumps from 02:00 CET to 03:00 CEST.
        Assert.Equal(
            "[Sunday 29 March 2026, 01:59 (Europe/Berlin, UTC+01:00)]",
            Berlin().FormatStamp(Utc(2026, 3, 29, 0, 59)));
        Assert.Equal(
            "[Sunday 29 March 2026, 03:00 (Europe/Berlin, UTC+02:00)]",
            Berlin().FormatStamp(Utc(2026, 3, 29, 1, 0)));
    }

    [Fact]
    public void FormatStamp_AcrossFallBack_RepeatedLocalHourIsToldApartByTheOffset()
    {
        // 2026-10-25: 02:30 happens twice in Berlin, once at UTC+02:00 and once at UTC+01:00.
        Assert.Equal(
            "[Sunday 25 October 2026, 02:30 (Europe/Berlin, UTC+02:00)]",
            Berlin().FormatStamp(Utc(2026, 10, 25, 0, 30)));
        Assert.Equal(
            "[Sunday 25 October 2026, 02:30 (Europe/Berlin, UTC+01:00)]",
            Berlin().FormatStamp(Utc(2026, 10, 25, 1, 30)));
    }

    [Fact]
    public void FormatStamp_SecondsAreDropped()
    {
        Assert.Equal(
            Berlin().FormatStamp(Utc(2026, 11, 22, 15, 45, 1)),
            Berlin().FormatStamp(Utc(2026, 11, 22, 15, 45, 59)));
    }

    [Fact]
    public void FormatStamp_FractionalOffsetZone_ShowsMinutes()
    {
        var clock = new OwnerClock(new FakeTimeProvider(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        Assert.Equal(
            "[Sunday 22 November 2026, 21:15 (Asia/Kolkata, UTC+05:30)]",
            clock.FormatStamp(Utc(2026, 11, 22, 15, 45)));
    }

    [Fact]
    public void FormatStamp_WindowsZoneId_IsShownAsIana()
    {
        // TimeZoneInfo.Local on a Windows host carries a Windows id; the model should still see IANA.
        var clock = new OwnerClock(new FakeTimeProvider(), TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));

        var stamp = clock.FormatStamp(Utc(2026, 11, 22, 15, 45));

        Assert.Contains("(Europe/", stamp, StringComparison.Ordinal);
        Assert.DoesNotContain("Standard Time", stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStamp_Utc_IsLabelledUtc()
    {
        var clock = new OwnerClock(new FakeTimeProvider(), TimeZoneInfo.Utc);

        Assert.Equal("[Sunday 22 November 2026, 15:45 (UTC, UTC+00:00)]", clock.FormatStamp(Utc(2026, 11, 22, 15, 45)));
    }

    [Fact]
    public void FormatStamp_IsInvariantEnglishRegardlessOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.StartsWith("[Sunday 22 November 2026", Berlin().FormatStamp(Utc(2026, 11, 22, 15, 45)), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FromHistoryOptions_UsesTheConfiguredHistoryZone()
    {
        var clock = OwnerClock.FromHistoryOptions(new FakeTimeProvider(), new HistoryOptions { TimeZone = "Europe/Berlin" });

        Assert.Equal("[Wednesday 15 July 2026, 14:00 (Europe/Berlin, UTC+02:00)]", clock.FormatStamp(Utc(2026, 7, 15, 12, 0)));
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
    public void UtcNow_ReadsTheTimeProvider()
    {
        var time = new FakeTimeProvider(Utc(2026, 11, 22, 15, 45));
        var clock = new OwnerClock(time, TimeZoneInfo.Utc);

        time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(Utc(2026, 11, 22, 15, 48), clock.UtcNow);
    }

    [Fact]
    public void AppendSystemPromptNote_Enabled_ExplainsThePerMessageTimestamps()
    {
        var result = Berlin().AppendSystemPromptNote("base prompt");

        Assert.StartsWith("base prompt", result, StringComparison.Ordinal);
        Assert.Contains("Each user message starts with", result, StringComparison.Ordinal);
        Assert.Contains("supplied by the system", result, StringComparison.Ordinal);
        Assert.Contains("latest", result, StringComparison.Ordinal);
        Assert.Contains("UTC", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendSystemPromptNote_IsStaticText()
    {
        Assert.Equal(
            new OwnerClock(new FakeTimeProvider(Utc(2026, 1, 1, 0, 0)), TimeZoneInfo.Utc).AppendSystemPromptNote("p"),
            new OwnerClock(new FakeTimeProvider(Utc(2026, 8, 9, 10, 11)), TimeZoneInfo.Utc).AppendSystemPromptNote("p"));
    }

    [Fact]
    public void AppendSystemPromptNote_Disabled_LeavesThePromptUntouched()
    {
        var clock = new OwnerClock(TimeProvider.System, TimeZoneInfo.Utc, stampEnabled: false);

        Assert.Equal("base prompt", clock.AppendSystemPromptNote("base prompt"));
    }
}
