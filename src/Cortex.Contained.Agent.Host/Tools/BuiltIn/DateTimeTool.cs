using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Provides current date/time information and timezone conversion.
/// Supports listing available timezones and converting between them.
/// </summary>
internal sealed class DateTimeTool : IAgentTool
{
    public string Name => "date_time";

    public string Description =>
        "Get the current date and time, or convert between timezones. " +
        "Actions: 'now' (current UTC + optional timezone), 'convert' (convert a datetime between timezones), " +
        "'list_timezones' (search available timezone IDs).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["now", "convert", "list_timezones"],
              "description": "The action to perform"
            },
            "timezone": {
              "type": "string",
              "description": "IANA or Windows timezone ID (e.g. 'America/New_York', 'UTC'). Used with 'now' and as target for 'convert'."
            },
            "datetime": {
              "type": "string",
              "description": "ISO 8601 datetime string to convert (e.g. '2025-01-15T14:30:00Z'). Used with 'convert'."
            },
            "from_timezone": {
              "type": "string",
              "description": "Source timezone for 'convert'. Defaults to UTC if not specified."
            },
            "search": {
              "type": "string",
              "description": "Filter string for 'list_timezones' (e.g. 'America', 'Europe'). Returns all if omitted."
            }
          },
          "required": ["action"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionElement))
            {
                return Task.FromResult(AgentToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionElement.GetString() ?? string.Empty;

            return action switch
            {
                "now" => HandleNow(root),
                "convert" => HandleConvert(root),
                "list_timezones" => HandleListTimezones(root),
                _ => Task.FromResult(AgentToolResult.Fail($"Unknown action: '{action}'. Valid actions: now, convert, list_timezones")),
            };
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}"));
        }
    }

    private static Task<AgentToolResult> HandleNow(JsonElement root)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"UTC: {utcNow:yyyy-MM-dd HH:mm:ss zzz} ({utcNow:dddd})");

        if (root.TryGetProperty("timezone", out var tzElement))
        {
            var tzId = tzElement.GetString() ?? string.Empty;
            if (!TryFindTimeZone(tzId, out var tz))
            {
                return Task.FromResult(AgentToolResult.Fail($"Unknown timezone: '{tzId}'. Use action 'list_timezones' to find valid IDs."));
            }

            var converted = TimeZoneInfo.ConvertTime(utcNow, tz);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{tz.Id}: {converted:yyyy-MM-dd HH:mm:ss zzz} ({converted:dddd})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Timezone display name: {tz.DisplayName}");
        }

        return Task.FromResult(AgentToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private static Task<AgentToolResult> HandleConvert(JsonElement root)
    {
        if (!root.TryGetProperty("datetime", out var dtElement))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter for 'convert': datetime (ISO 8601 format)"));
        }

        var dtString = dtElement.GetString() ?? string.Empty;

        if (!DateTimeOffset.TryParse(dtString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var inputDto))
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid datetime format: '{dtString}'. Use ISO 8601 (e.g. '2025-01-15T14:30:00Z')."));
        }

        // Determine source timezone
        TimeZoneInfo fromTz = TimeZoneInfo.Utc;
        if (root.TryGetProperty("from_timezone", out var fromTzElement))
        {
            var fromTzId = fromTzElement.GetString() ?? string.Empty;
            if (!TryFindTimeZone(fromTzId, out fromTz))
            {
                return Task.FromResult(AgentToolResult.Fail($"Unknown source timezone: '{fromTzId}'. Use action 'list_timezones' to find valid IDs."));
            }
        }

        // Determine target timezone
        if (!root.TryGetProperty("timezone", out var toTzElement))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter for 'convert': timezone (target timezone)"));
        }

        var toTzId = toTzElement.GetString() ?? string.Empty;
        if (!TryFindTimeZone(toTzId, out var toTz))
        {
            return Task.FromResult(AgentToolResult.Fail($"Unknown target timezone: '{toTzId}'. Use action 'list_timezones' to find valid IDs."));
        }

        // Convert: first interpret in source timezone, then convert to target
        var sourceTime = TimeZoneInfo.ConvertTime(inputDto, fromTz);
        var targetTime = TimeZoneInfo.ConvertTime(sourceTime, toTz);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source ({fromTz.Id}): {sourceTime:yyyy-MM-dd HH:mm:ss zzz} ({sourceTime:dddd})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Target ({toTz.Id}): {targetTime:yyyy-MM-dd HH:mm:ss zzz} ({targetTime:dddd})");

        return Task.FromResult(AgentToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private static Task<AgentToolResult> HandleListTimezones(JsonElement root)
    {
        var search = string.Empty;
        if (root.TryGetProperty("search", out var searchElement))
        {
            search = searchElement.GetString() ?? string.Empty;
        }

        var timezones = TimeZoneInfo.GetSystemTimeZones();
        var sb = new StringBuilder();
        var count = 0;

        foreach (var tz in timezones)
        {
            if (search.Length > 0 &&
                !tz.Id.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !tz.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{tz.Id} — {tz.DisplayName}");
            count++;
        }

        if (count == 0)
        {
            return Task.FromResult(AgentToolResult.Ok($"No timezones found matching '{search}'."));
        }

        var header = string.Create(CultureInfo.InvariantCulture, $"Found {count} timezone(s)");
        if (search.Length > 0)
        {
            header = string.Create(CultureInfo.InvariantCulture, $"Found {count} timezone(s) matching '{search}'");
        }

        return Task.FromResult(AgentToolResult.Ok(header + ":\n" + sb.ToString().TrimEnd()));
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
