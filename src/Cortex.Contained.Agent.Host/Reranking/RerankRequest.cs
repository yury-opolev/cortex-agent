namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Per-call reranking choice of a search: the configured budget (production), off, or an explicit
/// budget (evaluation runs compare budgets on the same corpus).
/// </summary>
/// <param name="UseConfigured">Use <see cref="IRerankStage.BudgetFor"/>.</param>
/// <param name="Budget">The explicit budget when <paramref name="UseConfigured"/> is false; null = off.</param>
public sealed record RerankRequest(bool UseConfigured, RerankBudget? Budget)
{
    /// <summary>The configured budget (null when reranking is off for the surface).</summary>
    public static RerankRequest Configured { get; } = new(true, null);

    /// <summary>No reranking.</summary>
    public static RerankRequest Off { get; } = new(false, null);

    /// <summary>Rerank with <paramref name="budget"/> regardless of the configuration.</summary>
    public static RerankRequest With(RerankBudget budget) => new(false, budget ?? throw new ArgumentNullException(nameof(budget)));

    /// <summary>The effective budget for <paramref name="surface"/>.</summary>
    public RerankBudget? Resolve(IRerankStage? stage, RerankSurface surface) =>
        this.UseConfigured ? stage?.BudgetFor(surface) : this.Budget;
}
