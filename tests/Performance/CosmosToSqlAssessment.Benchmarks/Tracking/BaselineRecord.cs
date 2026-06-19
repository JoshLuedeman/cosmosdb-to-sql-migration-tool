using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Benchmarks.Tracking;

/// <summary>
/// Top-level shape of <c>baselines/baseline.json</c>. Owned by sub-issue #177 (parent #79).
/// </summary>
public sealed class BaselineFile
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset? CapturedAt { get; set; }

    public string? CapturedOn { get; set; }

    public string? CaptureCommand { get; set; }

    public double DefaultToleranceFactor { get; set; } = 2.00;

    public long DefaultAllocationFloorBytes { get; set; } = 1024;

    /// <summary>
    /// Mean-axis "noise floor" for nanosecond-scale micro-benchmarks. When a benchmark's recorded
    /// baseline mean is below <see cref="MeanNoiseFloorNs"/> and it carries no explicit mean override,
    /// the mean axis is compared at <see cref="MeanNoiseFloorToleranceFactor"/> instead of
    /// <see cref="DefaultToleranceFactor"/>. Sub-microsecond wall-clock timings have a jitter floor on
    /// shared CI runners that swamps any sub-10% signal, so the strict default produces false positives
    /// on byte-identical code. The allocation axis is unaffected (allocations are deterministic), so
    /// real algorithmic regressions are still caught. Set to <c>0</c> (the default) to disable.
    /// </summary>
    public double MeanNoiseFloorNs { get; set; }

    /// <summary>
    /// Mean tolerance applied to benchmarks below <see cref="MeanNoiseFloorNs"/> that have no explicit
    /// mean override. Ignored when <see cref="MeanNoiseFloorNs"/> is <c>0</c>.
    /// </summary>
    public double MeanNoiseFloorToleranceFactor { get; set; } = 1.30;

    public Dictionary<string, BenchmarkBaseline> Benchmarks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Recorded values for one benchmark (one row per parameterised invocation, keyed by
/// BenchmarkDotNet's <c>FullName</c> including parameter suffix e.g. <c>Method(Size: Small)</c>).
/// </summary>
public sealed class BenchmarkBaseline
{
    public double MeanNs { get; set; }

    public long AllocatedBytes { get; set; }

    /// <summary>
    /// Shared per-benchmark override applied to <em>both</em> the mean and allocation axes when the
    /// axis-specific overrides below are absent. Kept for backwards compatibility with baselines
    /// authored before the mean/allocation split.
    /// </summary>
    public double? ToleranceFactor { get; set; }

    /// <summary>
    /// Per-benchmark override for the <em>mean (wall-clock)</em> axis only. Use this to widen the
    /// tolerance for benchmarks whose timing is inherently noisy on shared runners (e.g. disk-I/O
    /// macro-benchmarks) without loosening their allocation budget. Falls back to
    /// <see cref="ToleranceFactor"/> then the file-level default.
    /// </summary>
    public double? MeanToleranceFactor { get; set; }

    /// <summary>
    /// Per-benchmark override for the <em>allocation</em> axis only. Allocations are deterministic,
    /// so this is rarely needed; it exists for symmetry with <see cref="MeanToleranceFactor"/>.
    /// Falls back to <see cref="ToleranceFactor"/> then the file-level default.
    /// </summary>
    public double? AllocationToleranceFactor { get; set; }
}

/// <summary>
/// Result of comparing one benchmark's actual run vs its baseline.
/// </summary>
public sealed record ComparisonRow(
    string FullName,
    double BaselineMeanNs,
    double ActualMeanNs,
    long BaselineAllocatedBytes,
    long ActualAllocatedBytes,
    double MeanToleranceFactor,
    double AllocationToleranceFactor,
    long AllocationFloorBytes,
    bool MeanRegression,
    bool AllocationRegression,
    bool MeanImproved,
    bool AllocationImproved)
{
    public bool AnyRegression => MeanRegression || AllocationRegression;
    public bool AnyImprovement => MeanImproved || AllocationImproved;

    public double MeanThresholdNs => BaselineMeanNs * MeanToleranceFactor;

    public long AllocationThresholdBytes => Math.Max(
        (long)Math.Ceiling(BaselineAllocatedBytes * AllocationToleranceFactor),
        BaselineAllocatedBytes + AllocationFloorBytes);
}

/// <summary>
/// Aggregate outcome of a <c>compare-baseline</c> run.
/// </summary>
public sealed class ComparisonOutcome
{
    public List<ComparisonRow> Rows { get; } = new();
    public List<string> ReportOnlyBenchmarks { get; } = new();
    public List<string> BaselineOnlyBenchmarks { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Shared JSON options for reading/writing the baseline file (camelCase, indented).
/// </summary>
internal static class BaselineSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
