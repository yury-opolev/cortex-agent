namespace Cortex.Contained.Bridge.Coding;

/// <summary>Runtime-selectable coda provider + model (null = coda's default).</summary>
public sealed record CodaModelSettings(string? Provider, string? Model);
