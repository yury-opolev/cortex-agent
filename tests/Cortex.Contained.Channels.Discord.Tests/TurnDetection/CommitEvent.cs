using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

/// <summary>
/// One commit event emitted by <see cref="TurnDetectionPipelineHarness"/>.
/// Tests assert against a list of these to validate end-of-turn behaviour
/// for each fixture WAV.
/// </summary>
/// <param name="VirtualTimeMs">Time on the harness's virtual clock when the commit fired.</param>
/// <param name="Reason">The <see cref="CommitReason"/> that drove the decision.</param>
/// <param name="PartialTranscript">Whisper output up to the commit point.</param>
/// <param name="PEou">Most-recent P(end-of-turn) at commit time.</param>
/// <param name="SilenceMsAtCommit">Wall-time silence span at commit.</param>
internal readonly record struct CommitEvent(
    int VirtualTimeMs,
    CommitReason Reason,
    string PartialTranscript,
    float PEou,
    int SilenceMsAtCommit);
