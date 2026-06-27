namespace Cortex.Contained.Channels.Discord;

/// <summary>One enrollment guidance line: the text to deliver and how.</summary>
public readonly record struct EnrollmentLine(string Text, EnrollmentLineKind Kind);
