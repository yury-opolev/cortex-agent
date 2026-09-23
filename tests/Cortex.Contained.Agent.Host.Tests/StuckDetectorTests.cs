using Cortex.Contained.Agent.Host.Agent.Autonomy;

namespace Cortex.Contained.Agent.Host.Tests;

public class StuckDetectorTests
{
    [Fact]
    public void ObserveTurn_SameActionObservationThreeTimes_DoesNotDetect()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("file_read", "{\"path\":\"a\"}", "checking", "same output", false);

        Assert.Equal(StuckDetectionKind.None, detector.ObserveTurn(StuckDetectorTurn.WithActions([action])).Kind);
        Assert.Equal(StuckDetectionKind.None, detector.ObserveTurn(StuckDetectorTurn.WithActions([action])).Kind);
        Assert.Equal(StuckDetectionKind.None, detector.ObserveTurn(StuckDetectorTurn.WithActions([action])).Kind);
    }

    [Fact]
    public void ObserveTurn_SameToolAndArgumentsWithDifferentThought_DoesNotDetect()
    {
        var detector = new StuckDetector();

        detector.ObserveTurn(StuckDetectorTurn.WithActions([new StuckDetectorAction("file_read", "a", "first reason", "same", false)]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([new StuckDetectorAction("file_read", "a", "second reason", "same", false)]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([new StuckDetectorAction("file_read", "a", "third reason", "same", false)]));
        var result = detector.ObserveTurn(StuckDetectorTurn.WithActions([new StuckDetectorAction("file_read", "a", "fourth reason", "same", false)]));

        Assert.Equal(StuckDetectionKind.None, result.Kind);
    }

    [Fact]
    public void ObserveTurn_SameActionObservationFourTimes_NudgesThenStucksIfPersistent()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("file_read", "{\"path\":\"a\"}", "checking", "same output", false);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var nudge = detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var stuck = detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));

        Assert.Equal(StuckDetectionKind.Nudge, nudge.Kind);
        Assert.Contains("same action and observation", nudge.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StuckDetectionKind.Stuck, stuck.Kind);
    }

    [Fact]
    public void ObserveTurn_SameActionErroringThreeTimes_DoesNotDetect()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("run", "build", "retry build", "exit 1", true);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var result = detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));

        Assert.Equal(StuckDetectionKind.None, result.Kind);
    }

    [Fact]
    public void ObserveTurn_SameActionErroringFourTimes_NudgesThenStucksIfPersistent()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("run", "build", "retry build", "exit 1", true);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var nudge = detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var stuck = detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));

        Assert.Equal(StuckDetectionKind.Nudge, nudge.Kind);
        Assert.Contains("erroring", nudge.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StuckDetectionKind.Stuck, stuck.Kind);
    }

    [Fact]
    public void ObserveTurn_MonologueTwoTurns_DoesNotDetect()
    {
        var detector = new StuckDetector();

        detector.ObserveTurn(StuckDetectorTurn.Monologue("thinking"));
        var result = detector.ObserveTurn(StuckDetectorTurn.Monologue("still thinking"));

        Assert.Equal(StuckDetectionKind.None, result.Kind);
    }

    [Fact]
    public void ObserveTurn_MonologueThreeTurns_NudgesThenStucksIfPersistent()
    {
        var detector = new StuckDetector();

        detector.ObserveTurn(StuckDetectorTurn.Monologue("thinking"));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("still thinking"));
        var nudge = detector.ObserveTurn(StuckDetectorTurn.Monologue("more thinking"));
        var stuck = detector.ObserveTurn(StuckDetectorTurn.Monologue("even more thinking"));

        Assert.Equal(StuckDetectionKind.Nudge, nudge.Kind);
        Assert.Contains("no tool calls", nudge.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StuckDetectionKind.Stuck, stuck.Kind);
    }

    [Fact]
    public void ObserveTurn_CyclePeriodTwoRepeatedTwice_DoesNotDetect()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("search", "x", "find", "none", false);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var result = detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));

        Assert.Equal(StuckDetectionKind.None, result.Kind);
    }

    [Fact]
    public void ObserveTurn_CyclePeriodTwoRepeatedThreeTimesAcrossEvents_NudgesThenStucksIfPersistent()
    {
        var detector = new StuckDetector();
        var action = new StuckDetectorAction("search", "x", "find", "none", false);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var nudge = detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([action]));
        var stuck = detector.ObserveTurn(StuckDetectorTurn.Monologue("think"));

        Assert.Equal(StuckDetectionKind.Nudge, nudge.Kind);
        Assert.Contains("cycle", nudge.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StuckDetectionKind.Stuck, stuck.Kind);
    }

    [Fact]
    public void ObserveTurn_CyclePeriodFourRepeatedThreeTimes_Nudges()
    {
        var detector = new StuckDetector();
        var first = new StuckDetectorAction("search", "a", "find", "none", false);
        var second = new StuckDetectorAction("read", "b", "inspect", "same", false);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([first]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think one"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([second]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think two"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([first]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think one"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([second]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think two"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([first]));
        detector.ObserveTurn(StuckDetectorTurn.Monologue("think one"));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([second]));
        var result = detector.ObserveTurn(StuckDetectorTurn.Monologue("think two"));

        Assert.Equal(StuckDetectionKind.Nudge, result.Kind);
    }

    [Fact]
    public void ObserveTurn_CyclePeriodFiveRepeatedThreeTimes_DoesNotDetect()
    {
        var detector = new StuckDetector();
        var first = new StuckDetectorAction("search", "a", "find", "none", false);
        var second = new StuckDetectorAction("read", "b", "inspect", "same", false);
        var third = new StuckDetectorAction("grep", "c", "scan", "miss", false);
        StuckDetectionResult result = StuckDetectionResult.None;

        for (var i = 0; i < 3; i++)
        {
            result = detector.ObserveTurn(StuckDetectorTurn.WithActions([first]));
            Assert.Equal(StuckDetectionKind.None, result.Kind);
            result = detector.ObserveTurn(StuckDetectorTurn.Monologue("think one"));
            Assert.Equal(StuckDetectionKind.None, result.Kind);
            result = detector.ObserveTurn(StuckDetectorTurn.WithActions([second]));
            Assert.Equal(StuckDetectionKind.None, result.Kind);
            result = detector.ObserveTurn(StuckDetectorTurn.Monologue("think two"));
            Assert.Equal(StuckDetectionKind.None, result.Kind);
            result = detector.ObserveTurn(StuckDetectorTurn.WithActions([third]));
            Assert.Equal(StuckDetectionKind.None, result.Kind);
        }
    }

    [Fact]
    public void ObserveTurn_LoopBreaks_ClearsNudgeKeySoSameLoopCanBeNudgedAgain()
    {
        var detector = new StuckDetector();
        var repeated = new StuckDetectorAction("file_read", "a", "checking", "same", false);
        var different = new StuckDetectorAction("file_read", "b", "checking", "different", false);

        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));
        Assert.Equal(StuckDetectionKind.Nudge, detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated])).Kind);
        Assert.Equal(StuckDetectionKind.None, detector.ObserveTurn(StuckDetectorTurn.WithActions([different])).Kind);
        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));
        detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated]));

        Assert.Equal(StuckDetectionKind.Nudge, detector.ObserveTurn(StuckDetectorTurn.WithActions([repeated])).Kind);
    }
}