using System.Collections.Concurrent;
using Cortex.Contained.Agent.Host.Reminders;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SpeakAfterDelayToolTests : IDisposable
{
    private static readonly ToolExecutionContext VoiceContext = new()
    {
        ConversationId = "discord-voice-tenant-1",
        ChannelId = "discord-voice",
    };

    private static readonly ToolExecutionContext WebchatContext = new()
    {
        ConversationId = "webchat-default",
        ChannelId = "webchat-default",
    };

    private readonly FakeVoiceCueDeliverer deliverer = new();
    private readonly SessionReminderService service;
    private readonly SpeakAfterDelayTool setTool;
    private readonly CancelDelayedSpeechTool cancelTool;

    public SpeakAfterDelayToolTests()
    {
        this.service = new SessionReminderService(this.deliverer, NullLogger<SessionReminderService>.Instance);
        this.setTool = new SpeakAfterDelayTool(this.service);
        this.cancelTool = new CancelDelayedSpeechTool(this.service);
    }

    public void Dispose()
    {
        this.service.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── speak_after_delay ───────────────────────────────────────────────

    [Fact]
    public void SpeakAfterDelayTool_Name_IsCorrect()
    {
        Assert.Equal("speak_after_delay", this.setTool.Name);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_FromVoiceConversation_Success()
    {
        var args = """
        {
            "delay_seconds": 60,
            "text": "sixty seconds rest"
        }
        """;

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));
        Assert.Contains("cue_id", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_FromNonVoiceConversation_ReturnsError()
    {
        var args = """
        {
            "delay_seconds": 60,
            "text": "hello"
        }
        """;

        var result = await this.setTool.ExecuteAsync(args, WebchatContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("voice", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_MissingDelaySeconds_ReturnsError()
    {
        var args = """{ "text": "hello" }""";

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("delay_seconds", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_MissingText_ReturnsError()
    {
        var args = """{ "delay_seconds": 60 }""";

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("text", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_EmptyText_ReturnsError()
    {
        var args = """{ "delay_seconds": 60, "text": "   " }""";

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("text", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_DelayBelowMinimum_ReturnsError()
    {
        var args = $$"""
        { "delay_seconds": {{SessionReminderService.MinDelaySeconds - 1}}, "text": "hello" }
        """;

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("between", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_DelayAboveMaximum_ReturnsError()
    {
        var args = $$"""
        { "delay_seconds": {{SessionReminderService.MaxDelaySeconds + 1}}, "text": "hello" }
        """;

        var result = await this.setTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("between", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeakAfterDelayTool_MalformedJson_ReturnsError()
    {
        var result = await this.setTool.ExecuteAsync("not-json{", VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── cancel_delayed_speech ───────────────────────────────────────────

    [Fact]
    public void CancelDelayedSpeechTool_Name_IsCorrect()
    {
        Assert.Equal("cancel_delayed_speech", this.cancelTool.Name);
    }

    [Fact]
    public async Task CancelDelayedSpeechTool_KnownCue_Success()
    {
        var setResult = await this.setTool.ExecuteAsync(
            """{ "delay_seconds": 60, "text": "rest complete" }""",
            VoiceContext,
            CancellationToken.None);
        Assert.True(setResult.Success);

        // Extract id from "{\"cue_id\":\"<hex>\"}".
        using var doc = System.Text.Json.JsonDocument.Parse(setResult.Content);
        var id = doc.RootElement.GetProperty("cue_id").GetString()!;

        var cancelArgs = $$"""
        { "cue_id": "{{id}}" }
        """;
        var cancelResult = await this.cancelTool.ExecuteAsync(cancelArgs, VoiceContext, CancellationToken.None);

        Assert.True(cancelResult.Success);
        Assert.Contains("\"cancelled\":true", cancelResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDelayedSpeechTool_UnknownCue_SuccessWithCancelledFalse()
    {
        var args = """
        { "cue_id": "deadbeef" }
        """;

        var result = await this.cancelTool.ExecuteAsync(args, VoiceContext, CancellationToken.None);

        // Cancelling an unknown id is not a tool error — it's "false" data.
        Assert.True(result.Success);
        Assert.Contains("\"cancelled\":false", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDelayedSpeechTool_MissingCueId_ReturnsError()
    {
        var result = await this.cancelTool.ExecuteAsync("{}", VoiceContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cue_id", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDelayedSpeechTool_FromNonVoiceConversation_ReturnsError()
    {
        var args = """
        { "cue_id": "anything" }
        """;

        var result = await this.cancelTool.ExecuteAsync(args, WebchatContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("voice", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fake deliverer ────────────────────────────────────────────────────

    private sealed class FakeVoiceCueDeliverer : IVoiceCueDeliverer
    {
        public ConcurrentBag<(string ConversationId, string ChannelId, string Text)> Calls { get; } = new();

        public Task SpeakAsync(string conversationId, string channelId, string text, CancellationToken cancellationToken)
        {
            this.Calls.Add((conversationId, channelId, text));
            return Task.CompletedTask;
        }
    }
}
