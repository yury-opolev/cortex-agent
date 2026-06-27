namespace Cortex.Contained.Evals;

/// <summary>
/// Shares a single <see cref="EvalFixture"/> across all eval test classes.
/// This avoids creating multiple fixture instances, which would cause OAuth
/// token refresh failures (Anthropic rotates refresh tokens on each use).
/// </summary>
[CollectionDefinition("Evals")]
public sealed class EvalTestGroup : ICollectionFixture<EvalFixture>;
