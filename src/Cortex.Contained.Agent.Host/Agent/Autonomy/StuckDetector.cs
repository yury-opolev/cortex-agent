namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// A tool action observed during an autonomous run, including the model thought that led to it.
/// </summary>
internal sealed record StuckDetectorAction(
    string ToolName,
    string Arguments,
    string Thought,
    string Observation,
    bool IsError);

/// <summary>
/// One completed model turn as seen by the autonomy stuck detector.
/// </summary>
internal sealed record StuckDetectorTurn(string Thought, IReadOnlyList<StuckDetectorAction> Actions)
{
    public static StuckDetectorTurn Monologue(string thought)
        => new(thought, []);

    public static StuckDetectorTurn WithActions(IReadOnlyList<StuckDetectorAction> actions, string? thought = null)
        => new(thought ?? string.Empty, actions);
}

internal enum StuckDetectionKind
{
    None,
    Nudge,
    Stuck,
}

internal sealed record StuckDetectionResult(StuckDetectionKind Kind, string Description)
{
    public static StuckDetectionResult None { get; } = new(StuckDetectionKind.None, string.Empty);
}

/// <summary>
/// Detects looping autonomy runs before their large backstop budgets become the real stop condition.
/// </summary>
internal sealed class StuckDetector
{
    private const int SameActionObservationThreshold = 4;
    private const int SameActionErrorThresholdExclusive = 3;
    private const int MonologueThreshold = 3;
    private const int CycleRepetitions = 3;
    private const int MaxCyclePeriod = 4;
    private const int MaxEvents = MaxCyclePeriod * CycleRepetitions;

    private readonly List<StuckEvent> events = [];
    private readonly HashSet<string> nudgedKeys = new(StringComparer.Ordinal);
    private int consecutiveNoToolTurns;

    public StuckDetectionResult ObserveTurn(StuckDetectorTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Actions.Count == 0)
        {
            this.consecutiveNoToolTurns++;
            this.AddEvent(StuckEvent.Thought(turn.Thought));
        }
        else
        {
            this.consecutiveNoToolTurns = 0;
            foreach (var action in turn.Actions)
            {
                this.AddEvent(StuckEvent.Action(action));
            }
        }

        var detection = this.FindDetection();
        if (detection is null)
        {
            this.nudgedKeys.Clear();
            return StuckDetectionResult.None;
        }

        if (this.nudgedKeys.Contains(detection.Key))
        {
            return new StuckDetectionResult(StuckDetectionKind.Stuck, detection.Description);
        }

        this.nudgedKeys.Clear();
        this.nudgedKeys.Add(detection.Key);
        return new StuckDetectionResult(StuckDetectionKind.Nudge, detection.Description);
    }

    public void Reset()
    {
        this.events.Clear();
        this.nudgedKeys.Clear();
        this.consecutiveNoToolTurns = 0;
    }

    private void AddEvent(StuckEvent stuckEvent)
    {
        this.events.Add(stuckEvent);
        if (this.events.Count > MaxEvents)
        {
            this.events.RemoveAt(0);
        }
    }

    private Detection? FindDetection()
    {
        var sameActionError = this.FindSameActionErrorDetection();
        if (sameActionError is not null)
        {
            return sameActionError;
        }

        var sameActionObservation = this.FindSameActionObservationDetection();
        if (sameActionObservation is not null)
        {
            return sameActionObservation;
        }

        if (this.consecutiveNoToolTurns >= MonologueThreshold)
        {
            return new Detection(
                "monologue:none",
                $"The run has produced {this.consecutiveNoToolTurns} consecutive turns with no tool calls. Use a tool, update todos, park a blocker with what was tried, or finish only if the goal is met.");
        }

        return this.FindCycleDetection();
    }

    private Detection? FindSameActionObservationDetection()
    {
        var count = 0;
        string? last = null;

        for (var i = this.events.Count - 1; i >= 0; i--)
        {
            var current = this.events[i];
            if (!current.IsAction)
            {
                break;
            }

            var fingerprint = current.ActionObservationFingerprint;
            if (last is null)
            {
                last = fingerprint;
            }
            else if (!string.Equals(last, fingerprint, StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        if (count < SameActionObservationThreshold || last is null)
        {
            return null;
        }

        return new Detection(
            $"same-action-observation:{last}",
            $"The run is repeating the same action and observation {count} times: {this.events[^1].Display}. Change strategy or park the blocker with what was tried.");
    }

    private Detection? FindSameActionErrorDetection()
    {
        var count = 0;
        string? last = null;

        for (var i = this.events.Count - 1; i >= 0; i--)
        {
            var current = this.events[i];
            if (!current.IsAction || !current.IsError)
            {
                break;
            }

            var fingerprint = current.ActionFingerprint;
            if (last is null)
            {
                last = fingerprint;
            }
            else if (!string.Equals(last, fingerprint, StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        if (count <= SameActionErrorThresholdExclusive || last is null)
        {
            return null;
        }

        return new Detection(
            $"same-action-error:{last}",
            $"The same action is erroring repeatedly ({count} times): {this.events[^1].Display}. Stop retrying unchanged inputs; try a different approach or park the blocker with what was tried.");
    }

    private Detection? FindCycleDetection()
    {
        for (var period = 2; period <= MaxCyclePeriod; period++)
        {
            var needed = period * CycleRepetitions;
            if (this.events.Count < needed)
            {
                continue;
            }

            var start = this.events.Count - needed;
            var matches = true;
            for (var offset = 0; offset < period && matches; offset++)
            {
                var expected = this.events[start + offset].EventFingerprint;
                for (var repetition = 1; repetition < CycleRepetitions; repetition++)
                {
                    var actual = this.events[start + offset + (repetition * period)].EventFingerprint;
                    if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (!matches)
            {
                continue;
            }

            var cycleEvents = this.events.Skip(start).Take(period).ToArray();
            var cycle = string.Join(" -> ", cycleEvents.Select(static e => e.Display));
            return new Detection(
                $"cycle:{period}:{CanonicalCycleFingerprint(cycleEvents)}",
                $"The run is repeating a cycle of period {period} three times: {cycle}. Break the cycle with a new observable action or park the blocker with what was tried.");
        }

        return null;
    }

    private static string CanonicalCycleFingerprint(IReadOnlyList<StuckEvent> cycleEvents)
    {
        var rotations = new List<string>(cycleEvents.Count);
        for (var start = 0; start < cycleEvents.Count; start++)
        {
            var parts = new string[cycleEvents.Count];
            for (var offset = 0; offset < cycleEvents.Count; offset++)
            {
                parts[offset] = cycleEvents[(start + offset) % cycleEvents.Count].EventFingerprint;
            }

            rotations.Add(string.Join("|", parts));
        }

        rotations.Sort(StringComparer.Ordinal);
        return rotations[0];
    }

    private sealed record Detection(string Key, string Description);

    private sealed record StuckEvent(
        bool IsAction,
        bool IsError,
        string ActionFingerprint,
        string ActionObservationFingerprint,
        string EventFingerprint,
        string Display)
    {
        public static StuckEvent Thought(string thought)
        {
            var normalizedThought = Normalize(thought);
            return new StuckEvent(
                false,
                false,
                string.Empty,
                string.Empty,
                $"thought:{normalizedThought}",
                $"thought '{normalizedThought}'");
        }

        public static StuckEvent Action(StuckDetectorAction action)
        {
            ArgumentNullException.ThrowIfNull(action);

            var toolName = Normalize(action.ToolName);
            var arguments = Normalize(action.Arguments);
            var thought = Normalize(action.Thought);
            var observation = Normalize(action.Observation);
            var actionFingerprint = $"action:{toolName}:{arguments}:{thought}";
            var actionObservationFingerprint = $"{actionFingerprint}:{observation}";
            return new StuckEvent(
                true,
                action.IsError,
                actionFingerprint,
                actionObservationFingerprint,
                $"{actionObservationFingerprint}:error={action.IsError}",
                $"{toolName}({arguments}) after '{thought}' observed '{observation}'");
        }

        private static string Normalize(string? value)
            => value?.Trim() ?? string.Empty;
    }
}