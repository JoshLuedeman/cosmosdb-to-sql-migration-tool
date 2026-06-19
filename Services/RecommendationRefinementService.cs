using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services;

/// <summary>
/// Computes a bounded similarity score between two anonymized <see cref="WorkloadProfile"/>
/// fingerprints. The score weights normalized workload complexity and size most heavily, with
/// smaller contributions from container count and provisioned throughput.
/// </summary>
public static class WorkloadSimilarity
{
    /// <summary>
    /// The minimum <see cref="Score"/> at which two workloads are considered comparable for the
    /// purpose of recommendation refinement.
    /// </summary>
    public const double DefaultThreshold = 0.6;

    private const double ComplexityWeight = 0.35;
    private const double SizeWeight = 0.35;
    private const double ContainerWeight = 0.15;
    private const double ThroughputWeight = 0.15;

    /// <summary>
    /// Computes a similarity score in the range [0, 1] between two workload profiles, where 1 is
    /// identical along every weighted dimension.
    /// </summary>
    /// <param name="a">The first workload profile.</param>
    /// <param name="b">The second workload profile.</param>
    /// <returns>A similarity score between 0 and 1.</returns>
    /// <exception cref="ArgumentNullException">Thrown when either profile is null.</exception>
    public static double Score(WorkloadProfile a, WorkloadProfile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var complexity = ComplexityCloseness(a.ComplexityRating, b.ComplexityRating);
        var size = OrdinalCloseness((int)a.SizeBucket, (int)b.SizeBucket);
        var containers = CountCloseness(a.ContainerCount, b.ContainerCount, treatZeroAsUnknown: false);
        var throughput = CountCloseness(a.MaxProvisionedRUs, b.MaxProvisionedRUs, treatZeroAsUnknown: true);

        return (ComplexityWeight * complexity)
            + (SizeWeight * size)
            + (ContainerWeight * containers)
            + (ThroughputWeight * throughput);
    }

    /// <summary>
    /// Determines whether two workload profiles are comparable at the given threshold.
    /// </summary>
    /// <param name="a">The first workload profile.</param>
    /// <param name="b">The second workload profile.</param>
    /// <param name="threshold">The minimum score for comparability (defaults to <see cref="DefaultThreshold"/>).</param>
    /// <returns><see langword="true"/> when the profiles are comparable; otherwise <see langword="false"/>.</returns>
    public static bool AreComparable(WorkloadProfile a, WorkloadProfile b, double threshold = DefaultThreshold) =>
        Score(a, b) >= threshold;

    private static double ComplexityCloseness(MigrationComplexityRating a, MigrationComplexityRating b)
    {
        // An unknown rating carries no signal, so treat it as neutral rather than dissimilar.
        if (a == MigrationComplexityRating.Unknown || b == MigrationComplexityRating.Unknown)
        {
            return 0.5;
        }

        return OrdinalCloseness((int)a, (int)b);
    }

    private static double OrdinalCloseness(int a, int b)
    {
        var diff = Math.Abs(a - b);
        return diff switch
        {
            0 => 1.0,
            1 => 0.5,
            _ => 0.0
        };
    }

    private static double CountCloseness(long a, long b, bool treatZeroAsUnknown)
    {
        if (a == b)
        {
            return 1.0;
        }

        if (a == 0 || b == 0)
        {
            // Zero may mean "unknown" (e.g., serverless throughput) — stay neutral instead of
            // penalizing — or, for a genuine count, a real mismatch.
            return treatZeroAsUnknown ? 0.5 : 0.0;
        }

        var min = Math.Min(a, b);
        var max = Math.Max(a, b);
        return (double)min / max;
    }
}

/// <summary>
/// A single labeled scenario used to evaluate refinement accuracy: an assessment, the prior
/// migration history available at the time, and the ground-truth configuration that is known to
/// perform satisfactorily for that workload.
/// </summary>
public sealed class RefinementScenario
{
    /// <summary>
    /// Creates a new <see cref="RefinementScenario"/>.
    /// </summary>
    /// <param name="assessment">The assessment whose baseline recommendation is being evaluated.</param>
    /// <param name="history">The prior migration outcomes available when refining.</param>
    /// <param name="expectedPlatform">The ground-truth satisfactory platform for this workload.</param>
    /// <param name="expectedTier">The ground-truth satisfactory tier for this workload.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assessment"/> or <paramref name="history"/> is null.</exception>
    public RefinementScenario(
        AssessmentResult assessment,
        IReadOnlyList<MigrationOutcome> history,
        string expectedPlatform,
        string expectedTier)
    {
        Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
        History = history ?? throw new ArgumentNullException(nameof(history));
        ExpectedPlatform = expectedPlatform ?? string.Empty;
        ExpectedTier = expectedTier ?? string.Empty;
    }

    /// <summary>The assessment whose recommendation is being evaluated.</summary>
    public AssessmentResult Assessment { get; }

    /// <summary>The prior migration outcomes available when refining.</summary>
    public IReadOnlyList<MigrationOutcome> History { get; }

    /// <summary>The ground-truth satisfactory platform for this workload.</summary>
    public string ExpectedPlatform { get; }

    /// <summary>The ground-truth satisfactory tier for this workload.</summary>
    public string ExpectedTier { get; }
}

/// <summary>
/// The aggregate result of an A/B comparison between the static baseline recommendation and the
/// learning-refined recommendation across a labeled set of <see cref="RefinementScenario"/>s.
/// Correctness is measured on the full <c>(platform, tier)</c> pair.
/// </summary>
public sealed class RefinementAbComparison
{
    /// <summary>Creates a new <see cref="RefinementAbComparison"/>.</summary>
    /// <param name="scenarioCount">The total number of scenarios evaluated.</param>
    /// <param name="baselineCorrect">The number of scenarios the baseline recommendation got right.</param>
    /// <param name="refinedCorrect">The number of scenarios the refined recommendation got right.</param>
    /// <param name="baselineTierOnlyCorrect">Secondary diagnostic: baseline correct on tier alone.</param>
    /// <param name="refinedTierOnlyCorrect">Secondary diagnostic: refined correct on tier alone.</param>
    public RefinementAbComparison(
        int scenarioCount,
        int baselineCorrect,
        int refinedCorrect,
        int baselineTierOnlyCorrect,
        int refinedTierOnlyCorrect)
    {
        ScenarioCount = scenarioCount;
        BaselineCorrect = baselineCorrect;
        RefinedCorrect = refinedCorrect;
        BaselineTierOnlyCorrect = baselineTierOnlyCorrect;
        RefinedTierOnlyCorrect = refinedTierOnlyCorrect;
    }

    /// <summary>The total number of scenarios evaluated.</summary>
    public int ScenarioCount { get; }

    /// <summary>The number of scenarios the baseline got right on the full (platform, tier) pair.</summary>
    public int BaselineCorrect { get; }

    /// <summary>The number of scenarios the refined recommendation got right on the full (platform, tier) pair.</summary>
    public int RefinedCorrect { get; }

    /// <summary>Secondary diagnostic: baseline scenarios correct on tier alone.</summary>
    public int BaselineTierOnlyCorrect { get; }

    /// <summary>Secondary diagnostic: refined scenarios correct on tier alone.</summary>
    public int RefinedTierOnlyCorrect { get; }

    /// <summary>The fraction (0–1) of scenarios the baseline got right on the full pair.</summary>
    public double BaselineAccuracy => ScenarioCount == 0 ? 0.0 : (double)BaselineCorrect / ScenarioCount;

    /// <summary>The fraction (0–1) of scenarios the refined recommendation got right on the full pair.</summary>
    public double RefinedAccuracy => ScenarioCount == 0 ? 0.0 : (double)RefinedCorrect / ScenarioCount;

    /// <summary>Whether the refined recommendation is strictly more accurate than the baseline.</summary>
    public bool RefinedImprovesOverBaseline => RefinedAccuracy > BaselineAccuracy;
}

/// <summary>
/// Correlates the current assessment with prior, anonymized <see cref="MigrationOutcome"/>s to
/// refine (or confirm) the recommended Azure SQL platform and tier, producing an attributable
/// <see cref="RecommendationRefinement"/>.
/// </summary>
/// <remarks>
/// The core <see cref="Refine(AssessmentResult, IReadOnlyList{MigrationOutcome})"/> is pure and
/// deterministic. Candidate configurations are ranked by the Wilson lower bound of their observed
/// satisfaction rate so that small samples cannot outrank larger, consistently-satisfactory ones,
/// and ties deterministically favor the baseline to avoid unnecessary churn.
/// </remarks>
public sealed class RecommendationRefinementService
{
    /// <summary>The minimum number of comparable prior migrations required to attempt refinement.</summary>
    public const int MinimumSimilarSamples = 3;

    /// <summary>The minimum number of samples a single configuration needs to be eligible as the refined choice.</summary>
    public const int MinimumCandidateSamples = 3;

    // z for a 95% confidence interval, used by the Wilson lower-bound ranking.
    private const double WilsonZ = 1.96;

    private readonly FeedbackCollectionService _feedback;

    /// <summary>
    /// Creates a new <see cref="RecommendationRefinementService"/>.
    /// </summary>
    /// <param name="feedback">The feedback service used to read prior local outcomes.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="feedback"/> is null.</exception>
    public RecommendationRefinementService(FeedbackCollectionService feedback)
    {
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
    }

    /// <summary>
    /// Refines the assessment's recommendation using prior outcomes read from the local feedback
    /// store. Reading is not consent-gated (it is the user's own data); see
    /// <see cref="FeedbackCollectionService"/>.
    /// </summary>
    /// <param name="assessment">The assessment to refine.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The refinement result (which may defer to the baseline).</returns>
    public async Task<RecommendationRefinement> RefineAsync(
        AssessmentResult assessment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var prior = new List<MigrationOutcome>();
        await foreach (var outcome in _feedback.GetOutcomesAsync(cancellationToken).ConfigureAwait(false))
        {
            prior.Add(outcome);
        }

        return Refine(assessment, prior);
    }

    /// <summary>
    /// Refines the assessment's recommendation against an explicit set of prior outcomes. Pure and
    /// deterministic — identical inputs always yield an identical result.
    /// </summary>
    /// <param name="assessment">The assessment to refine.</param>
    /// <param name="prior">The prior migration outcomes to learn from.</param>
    /// <returns>The refinement result (which may defer to or confirm the baseline).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assessment"/> or <paramref name="prior"/> is null.</exception>
    public RecommendationRefinement Refine(AssessmentResult assessment, IReadOnlyList<MigrationOutcome> prior)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(prior);

        var sql = assessment.SqlAssessment ?? new SqlMigrationAssessment();
        var baselinePlatform = sql.RecommendedPlatform ?? string.Empty;
        var baselineTier = sql.RecommendedTier ?? string.Empty;

        var current = WorkloadProfile.FromAssessment(assessment);

        // Comparable history: similar, successful, and with a known performance signal to learn from.
        var similar = prior
            .Where(o => o is { Succeeded: true, Profile: not null }
                        && o.PerformanceSatisfactory.HasValue
                        && WorkloadSimilarity.AreComparable(current, o.Profile))
            .ToList();

        if (similar.Count < MinimumSimilarSamples)
        {
            var reason = prior.Count == 0
                ? "No prior migration outcomes are available yet; using the baseline recommendation."
                : $"Only {similar.Count} comparable prior migration(s) found (need at least {MinimumSimilarSamples}); using the baseline recommendation.";
            return RecommendationRefinement.None(baselinePlatform, baselineTier, similar.Count, reason);
        }

        var candidates = similar
            .GroupBy(o => new ConfigKey(o.DeployedPlatform ?? string.Empty, o.DeployedTier ?? string.Empty))
            .Select(g => BuildCandidate(g.Key, g.ToList(), baselinePlatform, baselineTier))
            .Where(c => c.Count >= MinimumCandidateSamples)
            .ToList();

        if (candidates.Count == 0)
        {
            var reason = $"Found {similar.Count} comparable migration(s), but no single deployed configuration had at least {MinimumCandidateSamples} samples; using the baseline recommendation.";
            return RecommendationRefinement.None(baselinePlatform, baselineTier, similar.Count, reason);
        }

        // Rank by Wilson lower bound (penalizes small samples), then prefer the baseline on a tie,
        // then larger samples, then a stable ordinal ordering for full determinism.
        var best = candidates
            .OrderByDescending(c => c.WilsonScore)
            .ThenByDescending(c => c.IsBaseline)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Tier, StringComparer.OrdinalIgnoreCase)
            .First();

        var changed = !ConfigEquals(best.Platform, best.Tier, baselinePlatform, baselineTier);
        var confidence = DetermineConfidence(best.Count, best.SatisfactionRate);

        return new RecommendationRefinement
        {
            HasRefinement = true,
            PriorSimilarMigrationCount = similar.Count,
            BaselinePlatform = baselinePlatform,
            BaselineTier = baselineTier,
            RefinedPlatform = best.Platform,
            RefinedTier = best.Tier,
            ChangedFromBaseline = changed,
            Confidence = confidence,
            ObservedSatisfactionRate = best.SatisfactionRate,
            AverageMonthlyCostVariancePercent = best.AverageCostVariancePercent,
            Rationale = BuildRationale(best, similar.Count, changed, baselinePlatform, baselineTier)
        };
    }

    /// <summary>
    /// Evaluates an A/B comparison of baseline vs refined recommendation accuracy across a labeled
    /// set of scenarios. Correctness is measured on the full <c>(platform, tier)</c> pair.
    /// </summary>
    /// <param name="scenarios">The labeled scenarios to evaluate (must be non-empty).</param>
    /// <returns>The aggregate comparison.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="scenarios"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scenarios"/> is empty.</exception>
    public RefinementAbComparison EvaluateAccuracy(IEnumerable<RefinementScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var list = scenarios.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one scenario is required to evaluate accuracy.", nameof(scenarios));
        }

        int baselineCorrect = 0, refinedCorrect = 0, baselineTierOnly = 0, refinedTierOnly = 0;

        foreach (var scenario in list)
        {
            var sql = scenario.Assessment.SqlAssessment ?? new SqlMigrationAssessment();
            var refinement = Refine(scenario.Assessment, scenario.History);

            if (ConfigEquals(sql.RecommendedPlatform, sql.RecommendedTier, scenario.ExpectedPlatform, scenario.ExpectedTier))
            {
                baselineCorrect++;
            }

            if (ConfigEquals(refinement.RefinedPlatform, refinement.RefinedTier, scenario.ExpectedPlatform, scenario.ExpectedTier))
            {
                refinedCorrect++;
            }

            if (string.Equals(sql.RecommendedTier ?? string.Empty, scenario.ExpectedTier, StringComparison.OrdinalIgnoreCase))
            {
                baselineTierOnly++;
            }

            if (string.Equals(refinement.RefinedTier, scenario.ExpectedTier, StringComparison.OrdinalIgnoreCase))
            {
                refinedTierOnly++;
            }
        }

        return new RefinementAbComparison(list.Count, baselineCorrect, refinedCorrect, baselineTierOnly, refinedTierOnly);
    }

    private Candidate BuildCandidate(ConfigKey key, IReadOnlyList<MigrationOutcome> outcomes, string baselinePlatform, string baselineTier)
    {
        var satisfied = outcomes.Count(o => o.PerformanceSatisfactory == true);
        var satisfactionRate = (double)satisfied / outcomes.Count;

        var variances = outcomes
            .Select(o => o.MonthlyCostVariancePercent)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        double? avgVariance = variances.Count > 0 ? variances.Average() : null;

        return new Candidate(
            key.Platform,
            key.Tier,
            outcomes.Count,
            satisfied,
            satisfactionRate,
            WilsonLowerBound(satisfied, outcomes.Count),
            avgVariance,
            ConfigEquals(key.Platform, key.Tier, baselinePlatform, baselineTier));
    }

    private static RefinementConfidence DetermineConfidence(int count, double satisfactionRate)
    {
        if (count >= 8 && satisfactionRate >= 0.8)
        {
            return RefinementConfidence.High;
        }

        if (count >= 5 && satisfactionRate >= 0.6)
        {
            return RefinementConfidence.Medium;
        }

        return RefinementConfidence.Low;
    }

    private static string BuildRationale(Candidate best, int similarCount, bool changed, string baselinePlatform, string baselineTier)
    {
        var config = DescribeConfig(best.Platform, best.Tier);
        var satisfactionText = $"{best.SatisfactionRate:P0}";
        var lead = changed
            ? $"Based on {similarCount} prior similar migration(s), {config} met performance expectations {satisfactionText} of the time, refining the baseline recommendation of {DescribeConfig(baselinePlatform, baselineTier)}."
            : $"Based on {similarCount} prior similar migration(s), prior outcomes support the baseline recommendation of {config}, which met performance expectations {satisfactionText} of the time.";

        if (best.AverageCostVariancePercent is { } variance)
        {
            var direction = variance > 0 ? "above" : "below";
            lead += $" On average, actual monthly cost ran {Math.Abs(variance):0.#}% {direction} estimate for these migrations.";
        }

        return lead;
    }

    private static string DescribeConfig(string platform, string tier)
    {
        var hasPlatform = !string.IsNullOrWhiteSpace(platform);
        var hasTier = !string.IsNullOrWhiteSpace(tier);
        if (hasPlatform && hasTier)
        {
            return $"{platform} ({tier})";
        }

        if (hasPlatform)
        {
            return platform;
        }

        return hasTier ? tier : "the deployed configuration";
    }

    private static bool ConfigEquals(string? platformA, string? tierA, string? platformB, string? tierB) =>
        string.Equals(platformA ?? string.Empty, platformB ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(tierA ?? string.Empty, tierB ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static double WilsonLowerBound(int positives, int total)
    {
        if (total == 0)
        {
            return 0.0;
        }

        var n = (double)total;
        var phat = positives / n;
        var z = WilsonZ;
        var denominator = 1 + (z * z / n);
        var centre = phat + (z * z / (2 * n));
        var margin = z * Math.Sqrt((phat * (1 - phat) + (z * z / (4 * n))) / n);
        return (centre - margin) / denominator;
    }

    private readonly record struct ConfigKey(string Platform, string Tier);

    private sealed record Candidate(
        string Platform,
        string Tier,
        int Count,
        int Satisfied,
        double SatisfactionRate,
        double WilsonScore,
        double? AverageCostVariancePercent,
        bool IsBaseline);
}
