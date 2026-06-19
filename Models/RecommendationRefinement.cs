namespace CosmosToSqlAssessment.Models;

/// <summary>
/// A qualitative confidence level for a refined recommendation, derived from the volume and
/// consistency of the prior similar migrations that support it.
/// </summary>
public enum RefinementConfidence
{
    /// <summary>No refinement was produced (insufficient comparable history).</summary>
    None = 0,

    /// <summary>A small amount of comparable history supports the recommendation.</summary>
    Low = 1,

    /// <summary>A moderate amount of consistent comparable history supports the recommendation.</summary>
    Medium = 2,

    /// <summary>A substantial amount of highly consistent comparable history supports the recommendation.</summary>
    High = 3
}

/// <summary>
/// The result of correlating the current assessment with prior, anonymized migration outcomes to
/// refine (or confirm) the recommended Azure SQL platform and tier. Carries an attributable
/// rationale ("based on N prior similar migrations") for transparent inclusion in reports.
/// </summary>
/// <remarks>
/// When <see cref="HasRefinement"/> is <see langword="false"/> the refined values equal the
/// baseline and <see cref="ChangedFromBaseline"/> is <see langword="false"/>; the
/// <see cref="Rationale"/> explains why no learning was applied (e.g., too little comparable
/// history). When <see cref="HasRefinement"/> is <see langword="true"/> the recommendation may
/// still equal the baseline — in which case the prior outcomes <em>confirm</em> rather than change
/// it.
/// </remarks>
public sealed class RecommendationRefinement
{
    /// <summary>
    /// Whether enough comparable prior history existed to apply learning. When false, the baseline
    /// recommendation is used unchanged.
    /// </summary>
    public bool HasRefinement { get; set; }

    /// <summary>The number of prior similar, successful migrations that informed this result.</summary>
    public int PriorSimilarMigrationCount { get; set; }

    /// <summary>The assessment's original (baseline) recommended Azure SQL platform.</summary>
    public string BaselinePlatform { get; set; } = string.Empty;

    /// <summary>The assessment's original (baseline) recommended Azure SQL tier.</summary>
    public string BaselineTier { get; set; } = string.Empty;

    /// <summary>The refined recommended Azure SQL platform (equals the baseline when not changed).</summary>
    public string RefinedPlatform { get; set; } = string.Empty;

    /// <summary>The refined recommended Azure SQL tier (equals the baseline when not changed).</summary>
    public string RefinedTier { get; set; } = string.Empty;

    /// <summary>Whether the refined recommendation differs from the baseline recommendation.</summary>
    public bool ChangedFromBaseline { get; set; }

    /// <summary>The confidence level for the refined recommendation.</summary>
    public RefinementConfidence Confidence { get; set; } = RefinementConfidence.None;

    /// <summary>
    /// A human-readable, attributable rationale suitable for inclusion in a generated report.
    /// </summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// The observed fraction (0–1) of prior similar migrations on the refined configuration that
    /// performed satisfactorily, or <see langword="null"/> when no refinement was produced.
    /// </summary>
    public double? ObservedSatisfactionRate { get; set; }

    /// <summary>
    /// The average monthly cost variance percentage (actual vs estimate) observed across the
    /// supporting prior migrations, or <see langword="null"/> when unavailable.
    /// </summary>
    public double? AverageMonthlyCostVariancePercent { get; set; }

    /// <summary>
    /// Creates a "no refinement" result that defers to the baseline recommendation.
    /// </summary>
    /// <param name="baselinePlatform">The baseline recommended platform.</param>
    /// <param name="baselineTier">The baseline recommended tier.</param>
    /// <param name="priorSimilarCount">The number of comparable prior migrations found (may be 0).</param>
    /// <param name="rationale">An explanation of why no refinement was applied.</param>
    /// <returns>A <see cref="RecommendationRefinement"/> that mirrors the baseline.</returns>
    public static RecommendationRefinement None(
        string baselinePlatform,
        string baselineTier,
        int priorSimilarCount,
        string rationale) => new()
        {
            HasRefinement = false,
            PriorSimilarMigrationCount = priorSimilarCount,
            BaselinePlatform = baselinePlatform,
            BaselineTier = baselineTier,
            RefinedPlatform = baselinePlatform,
            RefinedTier = baselineTier,
            ChangedFromBaseline = false,
            Confidence = RefinementConfidence.None,
            Rationale = rationale,
            ObservedSatisfactionRate = null,
            AverageMonthlyCostVariancePercent = null
        };
}
