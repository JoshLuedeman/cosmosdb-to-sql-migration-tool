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

    public double? ToleranceFactor { get; set; }
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
    double ToleranceFactor,
    long AllocationFloorBytes,
    bool MeanRegression,
    bool AllocationRegression,
    bool MeanImproved,
    bool AllocationImproved)
{
    public bool AnyRegression => MeanRegression || AllocationRegression;
    public bool AnyImprovement => MeanImproved || AllocationImproved;

    public double MeanThresholdNs => BaselineMeanNs * ToleranceFactor;

    public long AllocationThresholdBytes => Math.Max(
        (long)Math.Ceiling(BaselineAllocatedBytes * ToleranceFactor),
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
