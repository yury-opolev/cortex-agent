using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class MessageImportValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private static MessageRecord Valid() => new() { UserId = "u", ChannelId = "ch", Role = "user", Content = "x", Timestamp = T0 };

    [Fact]
    public void Validate_CleanBatch_NoErrors()
    {
        Assert.Empty(MessageImportValidator.Validate([Valid(), Valid() with { Role = "assistant" }]));
    }

    [Fact]
    public void Validate_BadRows_ReportsEachWithIndex()
    {
        var errors = MessageImportValidator.Validate(
        [
            Valid(),
            Valid() with { ChannelId = " " },
            Valid() with { Category = (MessageCategory)42 },
            Valid() with { Timestamp = default },
        ]);

        Assert.Equal(3, errors.Count);
        Assert.StartsWith("row 1:", errors[0], StringComparison.Ordinal);
        Assert.StartsWith("row 2:", errors[1], StringComparison.Ordinal);
        Assert.StartsWith("row 3:", errors[2], StringComparison.Ordinal);
    }
}
