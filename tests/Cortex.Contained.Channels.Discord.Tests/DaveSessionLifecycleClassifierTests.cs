using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the recognition of the two Discord.Net log lines that bracket the DAVE
/// handshake. These strings are the contract with an external library, so the
/// exact wire text from Discord.Net 3.20.1 is asserted verbatim — a silent upstream
/// rename would otherwise disable both the self-heal disarm and the stall watchdog.
/// </summary>
public class DaveSessionLifecycleClassifierTests
{
    [Fact]
    public void InitLine_IsHandshakeStarted()
    {
        Assert.Equal(
            DaveLifecycleEvent.HandshakeStarted,
            DaveSessionLifecycleClassifier.Classify("Dave #5", "Init dave protocol session, version 1"));
    }

    [Fact]
    public void TransitionLine_IsSessionReady()
    {
        // Verbatim from the 2026-08-19 log, the healthy Audio #5 session.
        Assert.Equal(
            DaveLifecycleEvent.SessionReady,
            DaveSessionLifecycleClassifier.Classify("Dave #5", "Preparing to transition to protocol version 1 (transition #0)"));
    }

    [Fact]
    public void MultiDigitProtocolVersion_IsRecognised()
    {
        Assert.Equal(
            DaveLifecycleEvent.SessionReady,
            DaveSessionLifecycleClassifier.Classify("Dave #12", "Preparing to transition to protocol version 10 (transition #3)"));
    }

    [Fact]
    public void DisabledProtocolVersion_IsNotALifecycleEvent()
    {
        // Version 0 means the session is running unencrypted. Treating it as a started
        // handshake would arm the stall watchdog on a DAVE-disabled deployment, which
        // can never produce a transition.
        Assert.Equal(
            DaveLifecycleEvent.None,
            DaveSessionLifecycleClassifier.Classify("Dave #5", "Init dave protocol session, version 0"));
        Assert.Equal(
            DaveLifecycleEvent.None,
            DaveSessionLifecycleClassifier.Classify("Dave #5", "Preparing to transition to protocol version 0 (transition #0)"));
    }

    [Theory]
    [InlineData("Audio #5", "Init dave protocol session, version 1")]
    [InlineData("Gateway", "Preparing to transition to protocol version 1 (transition #0)")]
    public void NonDaveSource_IsIgnored(string source, string message)
    {
        Assert.Equal(DaveLifecycleEvent.None, DaveSessionLifecycleClassifier.Classify(source, message));
    }

    [Theory]
    [InlineData("Dave #5", "MLS Failure: discord::dave::mls::Session::ValidateProposalMessage -> Unexpected user ID in add proposal")]
    [InlineData("Dave #5", "Handling external sender package")]
    [InlineData("Dave #5", "Received Binary DaveMLSWelcome | 959 bytes, sequence #7")]
    [InlineData("Dave #5", "Executing tranisition to protocol version 1 (transition #1)")]
    [InlineData("Dave #5", "Init dave protocol session, version ")]
    [InlineData("Dave #5", null)]
    [InlineData(null, "Init dave protocol session, version 1")]
    public void UnrelatedOrMalformed_IsNone(string? source, string? message)
    {
        Assert.Equal(DaveLifecycleEvent.None, DaveSessionLifecycleClassifier.Classify(source, message));
    }

    [Fact]
    public void DecryptStreamSource_DoesNotFalselyMatch()
    {
        // "Dave decrypt stream {id}" shares the source prefix but never emits these
        // messages; the message check must carry the disambiguation.
        Assert.Equal(
            DaveLifecycleEvent.None,
            DaveSessionLifecycleClassifier.Classify("Dave decrypt stream 806798098839765047", "Failed to decrypt audio packet for 806798098839765047: MissingKeyRatchet"));
    }
}
