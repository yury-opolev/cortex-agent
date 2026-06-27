namespace Cortex.Contained.Channels.Voice.Tests;

public class VoiceChannelOptionsTests
{
    #region Default Values

    [Fact]
    public void InputDeviceIndex_DefaultsToNegativeOne()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal(-1, options.InputDeviceIndex);
    }

    [Fact]
    public void OutputDeviceIndex_DefaultsToNegativeOne()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal(-1, options.OutputDeviceIndex);
    }

    [Fact]
    public void PushToTalk_DefaultsToTrue()
    {
        var options = new VoiceChannelOptions();

        Assert.True(options.PushToTalk);
    }

    [Fact]
    public void PushToTalkHotkey_DefaultsToCtrlSpace()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal("Ctrl+Space", options.PushToTalkHotkey);
    }

    [Fact]
    public void SilenceTimeoutMs_DefaultsTo1500()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal(1500, options.SilenceTimeoutMs);
    }

    [Fact]
    public void VoiceActivityThreshold_DefaultsTo001()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal(0.01f, options.VoiceActivityThreshold);
    }

    [Fact]
    public void ChannelId_DefaultsToVoiceDefault()
    {
        var options = new VoiceChannelOptions();

        Assert.Equal("voice-default", options.ChannelId);
    }

    #endregion

    #region Custom Values

    [Fact]
    public void AllProperties_CanBeCustomized()
    {
        var options = new VoiceChannelOptions
        {
            InputDeviceIndex = 2,
            OutputDeviceIndex = 3,
            PushToTalk = true,
            PushToTalkHotkey = "Alt+V",
            SilenceTimeoutMs = 2000,
            VoiceActivityThreshold = 0.05f,
            ChannelId = "my-voice",
        };

        Assert.Equal(2, options.InputDeviceIndex);
        Assert.Equal(3, options.OutputDeviceIndex);
        Assert.True(options.PushToTalk);
        Assert.Equal("Alt+V", options.PushToTalkHotkey);
        Assert.Equal(2000, options.SilenceTimeoutMs);
        Assert.Equal(0.05f, options.VoiceActivityThreshold);
        Assert.Equal("my-voice", options.ChannelId);
    }

    #endregion

    #region Record With-expression

    [Fact]
    public void WithExpression_CreatesNewInstanceWithModifiedValues()
    {
        var original = new VoiceChannelOptions { PushToTalk = false };

        var modified = original with { PushToTalk = true, SilenceTimeoutMs = 3000 };

        Assert.False(original.PushToTalk);
        Assert.Equal(1500, original.SilenceTimeoutMs);
        Assert.True(modified.PushToTalk);
        Assert.Equal(3000, modified.SilenceTimeoutMs);
    }

    #endregion
}
