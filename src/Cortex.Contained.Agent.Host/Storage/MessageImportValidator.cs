using System.Globalization;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Validates a staged tenant import before any row is touched (§4.1 atomic tenant import): the
/// whole batch is checked first, and only a clean batch reaches the single purge-and-replace
/// transaction.
/// </summary>
public static class MessageImportValidator
{
    /// <summary>Largest content accepted for one imported row.</summary>
    public const int MaxContentChars = 1_000_000;

    /// <summary>Returns one message per invalid row (empty when the batch is clean).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<MessageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var errors = new List<string>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (string.IsNullOrWhiteSpace(record.ChannelId) || string.IsNullOrWhiteSpace(record.Role) || string.IsNullOrWhiteSpace(record.UserId))
            {
                errors.Add(Row(i, "channelId, role and userId are required"));
            }

            if (record.Content is null || record.Content.Length > MaxContentChars)
            {
                errors.Add(Row(i, $"content is missing or longer than {MaxContentChars} characters"));
            }

            if (!Enum.IsDefined(record.Category))
            {
                errors.Add(Row(i, "category is not a known message category"));
            }

            if (record.Timestamp == default)
            {
                errors.Add(Row(i, "timestamp is missing"));
            }
        }

        return errors;
    }

    private static string Row(int index, string problem) =>
        string.Create(CultureInfo.InvariantCulture, $"row {index}: {problem}");
}
