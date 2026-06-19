namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// Options for the rolling-window z-score anomaly detector (#226) applied to migration
/// Request-Units/sec and throughput. Bound from the <c>AzureMonitor:Anomaly</c> section.
/// </summary>
/// <remarks>
/// The detector is a deliberately simple statistical baseline: it assumes a roughly
/// stationary, symmetric local distribution. Bursty / heavy-tailed metrics may produce false
/// positives; the z-score threshold and relative-change guard are the primary tuning knobs.
/// </remarks>
public sealed class AnomalyDetectionOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "AzureMonitor:Anomaly";

    /// <summary>Whether anomaly detection is active. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of recent observations retained per key/metric baseline. Defaults to 20.</summary>
    public int WindowSize { get; set; } = 20;

    /// <summary>
    /// Minimum number of prior observations required before the baseline is evaluated (warmup).
    /// Must be at least 2 (sample standard deviation is undefined for one point). Defaults to 5.
    /// </summary>
    public int MinSamplesForBaseline { get; set; } = 5;

    /// <summary>Absolute z-score at or above which an observation is flagged. Defaults to 3.0.</summary>
    public double ZScoreThreshold { get; set; } = 3.0;

    /// <summary>
    /// Minimum fractional change from the baseline mean required to flag an anomaly, guarding
    /// against flapping on near-constant baselines. Defaults to 0.25 (25%).
    /// </summary>
    public double MinRelativeChange { get; set; } = 0.25;

    /// <summary>
    /// Floor applied to the baseline standard deviation in the z-score denominator to avoid
    /// divide-by-zero on constant series. Defaults to a tiny epsilon.
    /// </summary>
    public double MinBaselineStdDev { get; set; } = 1e-9;

    /// <summary>Whether to watch <c>RequestUnitsPerSecond</c>. Defaults to <c>true</c>.</summary>
    public bool WatchRequestUnits { get; set; } = true;

    /// <summary>Whether to watch <c>ThroughputRowsPerSecond</c>. Defaults to <c>true</c>.</summary>
    public bool WatchThroughput { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (default), low-direction anomalies are suppressed for snapshots whose
    /// status is terminal (e.g. <c>Succeeded</c>), so a natural ramp-down at completion does
    /// not spam warnings.
    /// </summary>
    public bool SuppressLowOnTerminalStatus { get; set; } = true;
}
