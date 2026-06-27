using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class SqliteDateTimeTextTests
{
    [Fact]
    public void Format_ThenParse_ReturnsOriginalInstant()
    {
        var original = new DateTimeOffset(2024, 3, 15, 10, 30, 45, 123, TimeSpan.Zero);

        var text = SqliteDateTimeText.Format(original);
        var parsed = SqliteDateTimeText.Parse(text);

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Format_WithNonUtcOffset_StoredAsUtc()
    {
        // A value with +05:00 offset — Format must store the UTC instant.
        var original = new DateTimeOffset(2024, 3, 15, 15, 30, 0, TimeSpan.FromHours(5));

        var text = SqliteDateTimeText.Format(original);
        var parsed = SqliteDateTimeText.Parse(text);

        Assert.Equal(original.ToUniversalTime(), parsed.ToUniversalTime());
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void ParseNullable_Null_ReturnsNull()
    {
        var result = SqliteDateTimeText.ParseNullable(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseNullable_NonNull_ParsesValue()
    {
        var original = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var text = SqliteDateTimeText.Format(original);

        var result = SqliteDateTimeText.ParseNullable(text);

        Assert.Equal(original, result);
    }
}
