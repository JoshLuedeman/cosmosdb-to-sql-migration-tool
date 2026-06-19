using System.Globalization;
using System.Text.Json;

namespace CosmosToSqlAssessment.Benchmarks.Tracking;

/// <summary>
/// Compares a BenchmarkDotNet full-report JSON file against a tracked baseline and reports
/// regressions. Invoked via <c>compare-baseline</c> subcommand on the benchmark CLI.
/// Owned by sub-issue #177 (parent #79). CI integration arrives in #178.
/// </summary>
public static class BaselineComparer
{
    public const int ExitSuccess = 0;
    public const int ExitRegression = 1;
    public const int ExitBadInvocation = 2;

    public static int Run(string[] args)
    {
        string? reportPath = null;
        string? baselinePath = null;
        bool update = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--report":
                    if (++i >= args.Length) return Fail("--report requires a path argument.");
                    reportPath = args[i];
                    break;
                case "--baseline":
                    if (++i >= args.Length) return Fail("--baseline requires a path argument.");
                    baselinePath = args[i];
                    break;
                case "--update":
                    update = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return ExitSuccess;
                default:
                    return Fail($"Unknown argument: {a}");
            }
        }

        if (string.IsNullOrEmpty(reportPath))
        {
            return Fail("--report <path> is required.");
        }

        baselinePath ??= ResolveDefaultBaselinePath();

        if (!File.Exists(reportPath))
        {
            return Fail($"Report file not found: {reportPath}");
        }

        BaselineFile baseline;
        try
        {
            baseline = LoadBaseline(baselinePath);
        }
        catch (FileNotFoundException)
        {
            if (!update)
            {
                return Fail(
                    $"Baseline file not found: {baselinePath}. Re-run with --update to seed it from the report.");
            }
            baseline = new BaselineFile();
        }
        catch (JsonException ex)
        {
            return Fail($"Baseline file is not valid JSON ({baselinePath}): {ex.Message}");
        }

        Dictionary<string, ReportEntry> reportEntries;
        try
        {
            reportEntries = LoadReport(reportPath);
        }
        catch (JsonException ex)
        {
            return Fail($"Report file is not valid JSON ({reportPath}): {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return Fail($"Report file is malformed ({reportPath}): {ex.Message}");
        }

        if (reportEntries.Count == 0)
        {
            return Fail($"Report file contained no benchmarks: {reportPath}");
        }

        if (update)
        {
            ApplyUpdate(baseline, reportEntries, reportPath);
            WriteBaseline(baselinePath, baseline);
            Console.WriteLine($"Wrote {reportEntries.Count} benchmark entries to {baselinePath}.");
            return ExitSuccess;
        }

        var outcome = Compare(baseline, reportEntries);

        PrintOutcome(outcome, baselinePath, reportPath);

        if (outcome.Errors.Count > 0)
        {
            return ExitBadInvocation;
        }

        return outcome.Rows.Any(r => r.AnyRegression) ? ExitRegression : ExitSuccess;
    }

    internal static string ResolveDefaultBaselinePath()
    {
        // The benchmark assembly typically lives in bin/Release/net8.0/. Walking up three levels
        // anchors us at the project root, where the source-tree `baselines/` folder lives. This
        // means --update edits the committed file (rather than a stray copy in bin/).
        string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string defaultPath = Path.Combine(projectRoot, "baselines", "baseline.json");

        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        // Fallback: walk upward from BaseDirectory looking for any baselines/baseline.json.
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            string candidate = Path.Combine(cursor.FullName, "baselines", "baseline.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            cursor = cursor.Parent;
        }

        return defaultPath;
    }

    internal static BaselineFile LoadBaseline(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Baseline file not found.", path);
        }
        string json = File.ReadAllText(path);
        var baseline = JsonSerializer.Deserialize<BaselineFile>(json, BaselineSerialization.Options)
            ?? throw new JsonException("Baseline file deserialised to null.");
        baseline.Benchmarks ??= new Dictionary<string, BenchmarkBaseline>(StringComparer.Ordinal);
        return baseline;
    }

    internal static void WriteBaseline(string path, BaselineFile baseline)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string json = JsonSerializer.Serialize(baseline, BaselineSerialization.Options);
        File.WriteAllText(path, json);
    }

    internal static Dictionary<string, ReportEntry> LoadReport(string path)
    {
        var entries = new Dictionary<string, ReportEntry>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("Benchmarks", out JsonElement benchmarksEl) ||
            benchmarksEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Expected top-level 'Benchmarks' array.");
        }

        foreach (JsonElement entry in benchmarksEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("FullName", out JsonElement nameEl) ||
                nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string fullName = nameEl.GetString()!;

            double? meanNs = null;
            if (entry.TryGetProperty("Statistics", out JsonElement statsEl) &&
                statsEl.ValueKind == JsonValueKind.Object &&
                statsEl.TryGetProperty("Mean", out JsonElement meanEl) &&
                meanEl.ValueKind == JsonValueKind.Number)
            {
                meanNs = meanEl.GetDouble();
            }

            long? allocBytes = null;
            if (entry.TryGetProperty("Memory", out JsonElement memEl) &&
                memEl.ValueKind == JsonValueKind.Object &&
                memEl.TryGetProperty("BytesAllocatedPerOperation", out JsonElement allocEl) &&
                allocEl.ValueKind == JsonValueKind.Number)
            {
                allocBytes = allocEl.GetInt64();
            }

            entries[fullName] = new ReportEntry(fullName, meanNs, allocBytes);
        }

        return entries;
    }

    /// <summary>
    /// Resolves the mean-axis "noise floor" tolerance for a benchmark whose baseline mean falls below
    /// <see cref="BaselineFile.MeanNoiseFloorNs"/>. Returns <c>null</c> when the floor is disabled
    /// (<see cref="BaselineFile.MeanNoiseFloorNs"/> is <c>0</c>) or the benchmark is at or above it, so
    /// callers fall through to <see cref="BaselineFile.DefaultToleranceFactor"/>.
    /// </summary>
    internal static double? MeanNoiseFloorFactor(BaselineFile baseline, double baselineMeanNs) =>
        baseline.MeanNoiseFloorNs > 0 && baselineMeanNs < baseline.MeanNoiseFloorNs
            ? baseline.MeanNoiseFloorToleranceFactor
            : null;

    internal static ComparisonOutcome Compare(BaselineFile baseline, Dictionary<string, ReportEntry> report)
    {
        var outcome = new ComparisonOutcome();
        var baselineKeys = new HashSet<string>(baseline.Benchmarks.Keys, StringComparer.Ordinal);

        foreach (KeyValuePair<string, ReportEntry> kv in report)
        {
            string name = kv.Key;
            ReportEntry actual = kv.Value;

            if (!baseline.Benchmarks.TryGetValue(name, out BenchmarkBaseline? baselineRow))
            {
                outcome.ReportOnlyBenchmarks.Add(name);
                continue;
            }
            baselineKeys.Remove(name);

            if (actual.MeanNs is null)
            {
                outcome.Errors.Add($"{name}: report is missing Statistics.Mean (was [MemoryDiagnoser]/measurement disabled?).");
                continue;
            }
            if (actual.AllocatedBytes is null)
            {
                outcome.Errors.Add($"{name}: report is missing Memory.BytesAllocatedPerOperation (is [MemoryDiagnoser] applied?).");
                continue;
            }

            double meanTolerance = baselineRow.MeanToleranceFactor
                ?? baselineRow.ToleranceFactor
                ?? MeanNoiseFloorFactor(baseline, baselineRow.MeanNs)
                ?? baseline.DefaultToleranceFactor;
            double allocTolerance = baselineRow.AllocationToleranceFactor
                ?? baselineRow.ToleranceFactor
                ?? baseline.DefaultToleranceFactor;
            long floor = baseline.DefaultAllocationFloorBytes;

            double actualMean = actual.MeanNs.Value;
            long actualAlloc = actual.AllocatedBytes.Value;

            double meanThreshold = baselineRow.MeanNs * meanTolerance;
            long allocThreshold = Math.Max(
                (long)Math.Ceiling(baselineRow.AllocatedBytes * allocTolerance),
                baselineRow.AllocatedBytes + floor);

            bool meanRegression = actualMean > meanThreshold;
            bool allocRegression = actualAlloc > allocThreshold;

            double meanImproveDivisor = meanTolerance > 1.0 ? meanTolerance : 1.0;
            double allocImproveDivisor = allocTolerance > 1.0 ? allocTolerance : 1.0;
            bool meanImproved = baselineRow.MeanNs > 0 && actualMean < baselineRow.MeanNs / meanImproveDivisor;
            bool allocImproved = baselineRow.AllocatedBytes > 0 && actualAlloc < baselineRow.AllocatedBytes / allocImproveDivisor;

            outcome.Rows.Add(new ComparisonRow(
                FullName: name,
                BaselineMeanNs: baselineRow.MeanNs,
                ActualMeanNs: actualMean,
                BaselineAllocatedBytes: baselineRow.AllocatedBytes,
                ActualAllocatedBytes: actualAlloc,
                MeanToleranceFactor: meanTolerance,
                AllocationToleranceFactor: allocTolerance,
                AllocationFloorBytes: floor,
                MeanRegression: meanRegression,
                AllocationRegression: allocRegression,
                MeanImproved: meanImproved,
                AllocationImproved: allocImproved));
        }

        foreach (string remaining in baselineKeys)
        {
            outcome.BaselineOnlyBenchmarks.Add(remaining);
        }

        if (baseline.Benchmarks.Count > 0 && outcome.Rows.Count == 0)
        {
            outcome.Errors.Add(
                "Baseline contains entries but zero benchmarks matched the report (possible FullName/schema drift).");
        }

        return outcome;
    }

    internal static void ApplyUpdate(BaselineFile baseline, Dictionary<string, ReportEntry> report, string reportPath)
    {
        baseline.CapturedAt = DateTimeOffset.UtcNow;
        baseline.CapturedOn = $"{Environment.MachineName} / {Environment.OSVersion} / .NET {Environment.Version}";
        baseline.CaptureCommand = $"compare-baseline --update --report \"{reportPath}\"";

        foreach (KeyValuePair<string, ReportEntry> kv in report)
        {
            ReportEntry actual = kv.Value;
            if (actual.MeanNs is null || actual.AllocatedBytes is null)
            {
                // Skip entries missing measurements; --update should only seed complete rows.
                continue;
            }

            if (baseline.Benchmarks.TryGetValue(kv.Key, out BenchmarkBaseline? existing))
            {
                existing.MeanNs = actual.MeanNs.Value;
                existing.AllocatedBytes = actual.AllocatedBytes.Value;
                // Per-row ToleranceFactor preserved across updates.
            }
            else
            {
                baseline.Benchmarks[kv.Key] = new BenchmarkBaseline
                {
                    MeanNs = actual.MeanNs.Value,
                    AllocatedBytes = actual.AllocatedBytes.Value
                };
            }
        }
    }

    private static void PrintOutcome(ComparisonOutcome outcome, string baselinePath, string reportPath)
    {
        Console.WriteLine($"compare-baseline");
        Console.WriteLine($"  baseline: {baselinePath}");
        Console.WriteLine($"  report:   {reportPath}");
        Console.WriteLine();

        if (outcome.Rows.Count > 0)
        {
            Console.WriteLine("Benchmark                                                  Mean(ns)         Δmean   Alloc(B)        Δalloc   Status");
            Console.WriteLine(new string('-', 130));
            foreach (ComparisonRow row in outcome.Rows.OrderBy(r => r.FullName, StringComparer.Ordinal))
            {
                string name = Truncate(row.FullName, 55);
                string meanStr = row.ActualMeanNs.ToString("N1", CultureInfo.InvariantCulture);
                string allocStr = row.ActualAllocatedBytes.ToString("N0", CultureInfo.InvariantCulture);
                string meanDelta = row.BaselineMeanNs > 0
                    ? ((row.ActualMeanNs / row.BaselineMeanNs) - 1.0).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture)
                    : "n/a";
                string allocDelta = row.BaselineAllocatedBytes > 0
                    ? ((row.ActualAllocatedBytes / (double)row.BaselineAllocatedBytes) - 1.0).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture)
                    : "n/a";
                string status = row.AnyRegression
                    ? "REGRESSION"
                    : (row.AnyImprovement ? "improved" : "ok");
                Console.WriteLine($"{name,-55}  {meanStr,16}  {meanDelta,8}  {allocStr,12}  {allocDelta,8}   {status}");
                if (row.MeanRegression)
                {
                    Console.WriteLine(
                        $"  ⚠ Mean above threshold: actual={row.ActualMeanNs:N1}ns > " +
                        $"{row.BaselineMeanNs:N1}ns × {row.MeanToleranceFactor:N2} = {row.MeanThresholdNs:N1}ns");
                }
                if (row.AllocationRegression)
                {
                    Console.WriteLine(
                        $"  ⚠ Allocations above threshold: actual={row.ActualAllocatedBytes:N0}B > " +
                        $"max(baseline×{row.AllocationToleranceFactor:N2}, baseline+{row.AllocationFloorBytes}) = {row.AllocationThresholdBytes:N0}B");
                }
                if (!row.AnyRegression && row.AnyImprovement)
                {
                    Console.WriteLine($"  ℹ Improved beyond tolerance; consider refreshing baseline.");
                }
            }
            Console.WriteLine();
        }

        if (outcome.ReportOnlyBenchmarks.Count > 0)
        {
            Console.WriteLine("Benchmarks in report but not in baseline (consider --update):");
            foreach (string name in outcome.ReportOnlyBenchmarks)
            {
                Console.WriteLine($"  + {name}");
            }
            Console.WriteLine();
        }

        if (outcome.BaselineOnlyBenchmarks.Count > 0)
        {
            Console.WriteLine("Benchmarks in baseline but missing from this report:");
            foreach (string name in outcome.BaselineOnlyBenchmarks)
            {
                Console.WriteLine($"  - {name}");
            }
            Console.WriteLine();
        }

        if (outcome.Errors.Count > 0)
        {
            Console.WriteLine("Errors:");
            foreach (string err in outcome.Errors)
            {
                Console.WriteLine($"  ✗ {err}");
            }
            Console.WriteLine();
        }

        int regressionCount = outcome.Rows.Count(r => r.AnyRegression);
        int improvedCount = outcome.Rows.Count(r => !r.AnyRegression && r.AnyImprovement);
        Console.WriteLine($"Summary: {outcome.Rows.Count} compared, {regressionCount} regression(s), {improvedCount} improvement(s).");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s.Substring(s.Length - max + 1);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"compare-baseline: {message}");
        Console.Error.WriteLine("Run with --help for usage.");
        return ExitBadInvocation;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: <benchmark-cli> compare-baseline --report <full.json> [--baseline <path>] [--update]");
        Console.WriteLine();
        Console.WriteLine("Compares a BenchmarkDotNet *-report-full.json file against the tracked baseline");
        Console.WriteLine("and exits non-zero if any benchmark regresses past its tolerance factor.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --report <path>     Path to BenchmarkDotNet full-report JSON (required).");
        Console.WriteLine("  --baseline <path>   Path to baseline.json (default: <project>/baselines/baseline.json).");
        Console.WriteLine("  --update            Write the report's actuals back as the new baseline.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 = pass, 1 = regression detected, 2 = bad invocation / bad data.");
    }

    /// <summary>
    /// Minimal projection of one benchmark row from a BenchmarkDotNet full-report JSON file.
    /// </summary>
    internal sealed record ReportEntry(string FullName, double? MeanNs, long? AllocatedBytes);
}
