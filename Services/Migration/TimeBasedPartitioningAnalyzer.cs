using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Recommends Azure SQL time-based table partitioning strategies and Cosmos <c>_ts</c>-based initial-load
    /// slicing for each migrated container (#138 of parent #69). Pure analysis over the already-collected
    /// <see cref="CosmosDbAnalysis"/>; performs no live Cosmos DB calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Partitioning in Azure SQL is primarily a manageability feature (sliding-window aging, partition-level
    /// maintenance) rather than a general query-performance guarantee, so the analyzer is deliberately
    /// conservative: it shortlists candidate temporal columns with confidence and mutability risk instead of
    /// confidently picking one, and it defaults to <see cref="PartitioningStrength.NotRecommended"/> unless
    /// the data volume justifies the added complexity.
    /// </para>
    /// <para>
    /// The Cosmos system <c>_ts</c> field is the last-<em>modified</em> timestamp (mutable), so it must never
    /// be used as the SQL partition column — updating a partitioning column forces expensive cross-partition
    /// row movement. <c>_ts</c> is only recommended for read-side initial-load slicing. The SQL partition
    /// column should be an immutable creation timestamp; when none exists, the analyzer recommends capturing
    /// the initial <c>_ts</c> as a stable (but not creation-accurate) <c>InitialLoadTimestamp</c> column.
    /// </para>
    /// </remarks>
    public sealed class TimeBasedPartitioningAnalyzer
    {
        private const long BytesPerGigabyte = 1024L * 1024L * 1024L;
        private const double NearUniversalPrevalence = 0.95;

        private readonly IConfiguration _configuration;
        private readonly ILogger<TimeBasedPartitioningAnalyzer> _logger;

        /// <summary>Creates a new <see cref="TimeBasedPartitioningAnalyzer"/>.</summary>
        /// <param name="configuration">Configuration supplying the <c>IncrementalMigration:Partitioning*</c> thresholds.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public TimeBasedPartitioningAnalyzer(IConfiguration configuration, ILogger<TimeBasedPartitioningAnalyzer> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Produces a database-level time-based partitioning analysis from the supplied Cosmos analysis.
        /// </summary>
        /// <param name="cosmosAnalysis">The collected Cosmos DB analysis. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="TimeBasedPartitioningAnalysis"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="cosmosAnalysis"/> is <c>null</c>.</exception>
        public TimeBasedPartitioningAnalysis Analyze(CosmosDbAnalysis cosmosAnalysis)
        {
            ArgumentNullException.ThrowIfNull(cosmosAnalysis);

            var monthlyDocs = Math.Max(1L, _configuration.GetValue("IncrementalMigration:PartitioningMonthlyDocumentThreshold", 1_000_000L));
            var dailyDocs = Math.Max(monthlyDocs, _configuration.GetValue("IncrementalMigration:PartitioningDailyDocumentThreshold", 50_000_000L));
            var monthlySizeGb = Math.Max(0.0, _configuration.GetValue("IncrementalMigration:PartitioningMonthlySizeGB", 5.0));
            var dailySizeGb = Math.Max(monthlySizeGb, _configuration.GetValue("IncrementalMigration:PartitioningDailySizeGB", 100.0));

            var analysis = new TimeBasedPartitioningAnalysis();

            foreach (var container in cosmosAnalysis.Containers)
            {
                analysis.Containers.Add(AnalyzeContainer(container, monthlyDocs, dailyDocs, monthlySizeGb, dailySizeGb));
            }

            analysis.ContainersRecommendedForPartitioning =
                analysis.Containers.Count(c => c.Strength != PartitioningStrength.NotRecommended);

            analysis.Assumptions.Add(
                $"Granularity thresholds: Day when document count \u2265 {dailyDocs:N0} or size \u2265 {dailySizeGb:N0} GB; " +
                $"Month when \u2265 {monthlyDocs:N0} documents or \u2265 {monthlySizeGb:N0} GB; Year for smaller-but-substantial " +
                "volumes; otherwise no partitioning.");
            analysis.Assumptions.Add(
                "Temporal column suitability is inferred from field names and detected types only; every candidate " +
                "requires user validation for immutability, non-null coverage, and query/retention alignment.");
            analysis.Assumptions.Add(
                "The Cosmos `_ts` system field is the last-modified timestamp (Unix epoch seconds). It is used only " +
                "for read-side initial-load slicing, never as the SQL partition column.");

            analysis.Notes.Add(
                "Azure SQL table partitioning is primarily a manageability feature (sliding-window aging, " +
                "partition-level maintenance), not a general query-performance optimization. Partition elimination " +
                "only benefits queries that filter on the partition column; validate against the real workload.");

            _logger.LogInformation(
                "Time-based partitioning analyzed for {ContainerCount} container(s); {Recommended} recommended/conditional",
                analysis.Containers.Count,
                analysis.ContainersRecommendedForPartitioning);

            return analysis;
        }

        private static ContainerPartitioningRecommendation AnalyzeContainer(
            ContainerAnalysis container,
            long monthlyDocs,
            long dailyDocs,
            double monthlySizeGb,
            double dailySizeGb)
        {
            var recommendation = new ContainerPartitioningRecommendation
            {
                ContainerName = container.ContainerName,
                DocumentCount = container.DocumentCount,
                SizeBytes = container.SizeBytes,
                InitialLoadParallelismUpperBound = container.FeedRangeCount,
            };

            recommendation.InitialLoadSlicingApproach = BuildSlicingApproach(container.FeedRangeCount);

            // Empty / unknown-volume containers: no partitioning decision.
            if (container.DocumentCount <= 0)
            {
                recommendation.Strength = PartitioningStrength.NotRecommended;
                recommendation.RecommendedGranularity = PartitionGranularity.None;
                recommendation.SlidingWindow = SlidingWindowConsideration.NotApplicable;
                recommendation.Rationale.Add(
                    "Container is empty or its document count is unknown; partitioning cannot be assessed. " +
                    "Default to a single non-partitioned table and revisit after the initial load.");
                return recommendation;
            }

            var candidates = DetectTemporalCandidates(container);
            recommendation.TemporalColumnCandidates = candidates;

            var hasImmutableCandidate = candidates.Any(c => c.MutabilityRisk == TemporalColumnMutabilityRisk.ImmutableLikely);
            if (!hasImmutableCandidate)
            {
                recommendation.RequiresSyntheticCreationColumn = true;
                var synthetic = BuildSyntheticCandidate();
                recommendation.TemporalColumnCandidates.Add(synthetic);
            }

            // Single recommended column only when exactly one near-universal, high-confidence immutable candidate exists.
            var strongImmutable = candidates
                .Where(c => c.Confidence == TemporalColumnConfidence.High
                    && c.MutabilityRisk == TemporalColumnMutabilityRisk.ImmutableLikely
                    && c.SchemaPrevalence >= NearUniversalPrevalence)
                .ToList();
            recommendation.RecommendedPartitionColumn = strongImmutable.Count == 1 ? strongImmutable[0].FieldName : null;

            var sizeGb = container.SizeBytes / (double)BytesPerGigabyte;
            recommendation.RecommendedGranularity = DetermineGranularity(
                container.DocumentCount, sizeGb, monthlyDocs, dailyDocs, monthlySizeGb, dailySizeGb);

            if (recommendation.RecommendedGranularity == PartitionGranularity.None)
            {
                recommendation.Strength = PartitioningStrength.NotRecommended;
                recommendation.SlidingWindow = SlidingWindowConsideration.NotApplicable;
                recommendation.Rationale.Add(
                    $"Container holds {container.DocumentCount:N0} document(s) (~{sizeGb:N2} GB), below the partitioning " +
                    "thresholds. A single non-partitioned table with appropriate indexing is simpler and sufficient.");
                return recommendation;
            }

            // Large enough to consider partitioning.
            recommendation.Strength =
                recommendation.RecommendedGranularity == PartitionGranularity.Day && recommendation.RecommendedPartitionColumn is not null
                    ? PartitioningStrength.Recommended
                    : PartitioningStrength.ConditionalManageability;

            recommendation.Rationale.Add(
                $"Container holds {container.DocumentCount:N0} document(s) (~{sizeGb:N2} GB); {recommendation.RecommendedGranularity} " +
                $"granularity keeps partitions a manageable size. Recommended partition function: {recommendation.PartitionFunctionType} " +
                "over ascending temporal boundaries.");

            if (recommendation.RecommendedPartitionColumn is not null)
            {
                recommendation.Rationale.Add(
                    $"'{recommendation.RecommendedPartitionColumn}' is a high-confidence immutable temporal column present in " +
                    "effectively all documents, making it a sound partitioning key.");
            }
            else if (recommendation.RequiresSyntheticCreationColumn)
            {
                recommendation.Rationale.Add(
                    "No suitable immutable temporal column was detected. Capture the initial `_ts` as a stable " +
                    "'InitialLoadTimestamp' column at load time and partition on that. It is NOT the document's true " +
                    "creation time, and change-feed updates must never mutate it.");
            }
            else
            {
                recommendation.Rationale.Add(
                    "Multiple or low-confidence temporal candidates were found; no single partition column is auto-selected. " +
                    "Validate a candidate from the shortlist for immutability and non-null coverage before adopting it.");
            }

            AddIndexAlignmentCaveats(recommendation);
            recommendation.SlidingWindow = DetermineSlidingWindow(container, recommendation);
            AddPartitioningCaveats(container, recommendation);

            return recommendation;
        }

        private static string BuildSlicingApproach(int feedRangeCount)
        {
            var parallelism = feedRangeCount > 0
                ? $"Parallelize the initial bulk load across feed ranges (upper bound ~{feedRangeCount} workers, " +
                  "subject to RU limits, throttling, hot ranges, and feed-range split/merge at runtime)."
                : "Feed-range count is unknown; discover feed ranges at execution time to bound parallel-load workers.";

            return parallelism +
                " Within each range, optionally sub-slice by `_ts` time windows for resumability and even-sized batches. " +
                "Window boundaries cannot be sized from static metadata (the `_ts` min/max range is unknown here); " +
                "determine the range at execution time using adaptive windowing.";
        }

        private static TemporalColumnCandidate BuildSyntheticCandidate() => new()
        {
            FieldName = "InitialLoadTimestamp",
            RecommendedSqlType = "datetime2",
            Confidence = TemporalColumnConfidence.Medium,
            MutabilityRisk = TemporalColumnMutabilityRisk.ImmutableLikely,
            SchemaPrevalence = 1.0,
            IsSyntheticFromInitialLoad = true,
            Rationale =
                "Synthetic column derived from the Cosmos `_ts` captured once at initial load. Stable after capture, " +
                "but it reflects last-modified-at-load time, not the document's original creation time.",
        };

        private static List<TemporalColumnCandidate> DetectTemporalCandidates(ContainerAnalysis container)
        {
            var totalPrevalence = container.DetectedSchemas.Sum(s => s.Prevalence);
            var useFractionFallback = totalPrevalence <= 0;
            var schemaCount = container.DetectedSchemas.Count;

            var byField = new Dictionary<string, TemporalFieldAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var schema in container.DetectedSchemas)
            {
                foreach (var (key, field) in schema.Fields)
                {
                    var name = string.IsNullOrWhiteSpace(field.FieldName) ? key : field.FieldName;
                    if (!IsTemporal(field))
                    {
                        continue;
                    }

                    if (!byField.TryGetValue(name, out var acc))
                    {
                        acc = new TemporalFieldAccumulator(name, field);
                        byField[name] = acc;
                    }

                    acc.PrevalenceWeight += useFractionFallback ? 1.0 : schema.Prevalence;
                    acc.SchemaOccurrences++;
                }
            }

            var candidates = new List<TemporalColumnCandidate>();
            foreach (var acc in byField.Values)
            {
                var prevalence = useFractionFallback
                    ? (schemaCount > 0 ? acc.SchemaOccurrences / (double)schemaCount : 0.0)
                    : Math.Min(1.0, acc.PrevalenceWeight / totalPrevalence);

                candidates.Add(Classify(acc, prevalence));
            }

            return candidates
                .OrderByDescending(c => c.Confidence)
                .ThenByDescending(c => c.MutabilityRisk == TemporalColumnMutabilityRisk.ImmutableLikely)
                .ThenByDescending(c => c.SchemaPrevalence)
                .ToList();
        }

        private static bool IsTemporal(FieldInfo field)
        {
            var sqlType = field.RecommendedSqlType?.ToLowerInvariant() ?? string.Empty;
            if (sqlType.Contains("date") || sqlType.Contains("time"))
            {
                return true;
            }

            if (field.DetectedTypes.Any(t =>
            {
                var lower = t.ToLowerInvariant();
                return lower.Contains("date") || lower.Contains("time");
            }))
            {
                return true;
            }

            return MatchesTemporalName(Normalize(field.FieldName));
        }

        private static TemporalColumnCandidate Classify(TemporalFieldAccumulator acc, double prevalence)
        {
            var norm = Normalize(acc.Name);
            var hasDateType = HasDateType(acc.Field);

            TemporalColumnConfidence confidence;
            TemporalColumnMutabilityRisk mutability;
            string rationale;

            if (IsMutableName(norm))
            {
                confidence = TemporalColumnConfidence.Low;
                mutability = TemporalColumnMutabilityRisk.MutableLikely;
                rationale = "Field name suggests a mutable last-modified or business-state timestamp; avoid as a partition key " +
                    "(updates would force cross-partition row movement).";
            }
            else if (IsCreationName(norm))
            {
                confidence = hasDateType ? TemporalColumnConfidence.High : TemporalColumnConfidence.Medium;
                mutability = TemporalColumnMutabilityRisk.ImmutableLikely;
                rationale = "Field name suggests an immutable creation/insertion timestamp" +
                    (hasDateType ? " with a date/time type." : " but no date/time type was detected; verify the type.");
            }
            else if (IsEventName(norm))
            {
                confidence = TemporalColumnConfidence.Medium;
                mutability = TemporalColumnMutabilityRisk.ImmutableLikely;
                rationale = "Field name suggests a write-once event/occurrence timestamp; usually immutable but domain-dependent.";
            }
            else
            {
                confidence = TemporalColumnConfidence.Low;
                mutability = TemporalColumnMutabilityRisk.Unknown;
                rationale = "Detected as a temporal field by type, but its name does not indicate whether the value is stable; " +
                    "validate immutability before using it as a partition key.";
            }

            if (prevalence < NearUniversalPrevalence && confidence == TemporalColumnConfidence.High)
            {
                confidence = TemporalColumnConfidence.Medium;
                rationale += $" Present in only ~{prevalence:P0} of schema variants, so a partition column built on it could be " +
                    "nullable/sparse — confirm full coverage.";
            }

            return new TemporalColumnCandidate
            {
                FieldName = acc.Name,
                RecommendedSqlType = string.IsNullOrWhiteSpace(acc.Field.RecommendedSqlType) ? "datetime2" : acc.Field.RecommendedSqlType,
                DetectedTypes = new List<string>(acc.Field.DetectedTypes),
                Confidence = confidence,
                MutabilityRisk = mutability,
                SchemaPrevalence = prevalence,
                Rationale = rationale,
            };
        }

        private static PartitionGranularity DetermineGranularity(
            long documentCount,
            double sizeGb,
            long monthlyDocs,
            long dailyDocs,
            double monthlySizeGb,
            double dailySizeGb)
        {
            if (documentCount >= dailyDocs || sizeGb >= dailySizeGb)
            {
                return PartitionGranularity.Day;
            }

            if (documentCount >= monthlyDocs || sizeGb >= monthlySizeGb)
            {
                return PartitionGranularity.Month;
            }

            // Year only for a still-substantial volume (a tenth of the monthly threshold or ~1 GB).
            if (documentCount >= Math.Max(1L, monthlyDocs / 10) || sizeGb >= Math.Min(monthlySizeGb, 1.0))
            {
                return PartitionGranularity.Year;
            }

            return PartitionGranularity.None;
        }

        private static SlidingWindowConsideration DetermineSlidingWindow(
            ContainerAnalysis container,
            ContainerPartitioningRecommendation recommendation)
        {
            if (container.DefaultTimeToLiveSeconds is null)
            {
                return SlidingWindowConsideration.NotApplicable;
            }

            var ttlDescription = container.DefaultTimeToLiveSeconds == -1
                ? "TTL is enabled with no container default (item-level TTL only), so not all documents necessarily expire"
                : $"TTL has a default of {container.DefaultTimeToLiveSeconds} second(s)";

            recommendation.Caveats.Add(
                $"{ttlDescription}. Cosmos TTL ages documents by `_ts` (last-modified) time, which differs from a creation-time " +
                "partition column. Dropping the oldest partition by creation time may delete rows Cosmos would still retain " +
                "(recently updated) or keep rows Cosmos has already expired. Validate that the SQL retention age basis matches " +
                "the TTL age basis before using sliding-window partition drops.");

            return SlidingWindowConsideration.ConsiderWithValidation;
        }

        private static void AddIndexAlignmentCaveats(ContainerPartitioningRecommendation recommendation)
        {
            recommendation.IndexAlignmentCaveats.Add(
                "The partition column must be part of the clustered index key for the table to be partition-aligned.");
            recommendation.IndexAlignmentCaveats.Add(
                "Any unique index or primary key must include the partition column to stay aligned and support partition SWITCH.");
            recommendation.IndexAlignmentCaveats.Add(
                "Avoid a nullable partition column: NULLs route to the leftmost boundary partition and undermine partition " +
                "elimination and retention assumptions.");
            recommendation.IndexAlignmentCaveats.Add(
                "Plan the partition column together with the primary key, clustered index, and the temporal predicates the " +
                "workload actually filters on; partitioning is a manageability tool, not an automatic performance win.");
        }

        private static void AddPartitioningCaveats(ContainerAnalysis container, ContainerPartitioningRecommendation recommendation)
        {
            if (container.DetectedSchemas.Count == 0)
            {
                recommendation.Caveats.Add(
                    "No document schema was captured for this container, so temporal column candidates are limited to the " +
                    "synthetic `_ts`-based column. Validate the target schema manually before partitioning.");
            }

            if (recommendation.Strength == PartitioningStrength.ConditionalManageability)
            {
                recommendation.Caveats.Add(
                    "Partitioning may help manageability at this scale but should be validated against the expected query " +
                    "predicates and the retention/archival policy before adoption.");
            }
        }

        private static bool HasDateType(FieldInfo field)
        {
            var sqlType = field.RecommendedSqlType?.ToLowerInvariant() ?? string.Empty;
            if (sqlType.Contains("date") || sqlType.Contains("time"))
            {
                return true;
            }

            return field.DetectedTypes.Any(t =>
            {
                var lower = t.ToLowerInvariant();
                return lower.Contains("date") || lower.Contains("time");
            });
        }

        private static bool MatchesTemporalName(string norm) =>
            IsCreationName(norm) || IsEventName(norm) || IsMutableName(norm)
            || norm.Contains("date") || norm.Contains("time");

        private static bool IsMutableName(string norm) =>
            norm.Contains("modif") || norm.Contains("updated") || norm.Contains("changed")
            || norm.StartsWith("last") || norm.Contains("expire") || norm.Contains("expiry")
            || norm.Contains("due") || norm.Contains("valid") || norm.Contains("deleted")
            || norm.Contains("processed") || norm.Contains("scheduled") || norm.Contains("effective")
            || norm.Contains("renew");

        private static bool IsCreationName(string norm) =>
            norm.Contains("creat") || norm.Contains("inserted") || norm.Contains("dateadded")
            || norm.Contains("addedon") || norm.Contains("addedat") || norm.Contains("registered");

        private static bool IsEventName(string norm) =>
            norm.Contains("event") || norm.Contains("occurred") || norm.Contains("occur")
            || norm.Contains("logged") || norm.Contains("logtime") || norm.Contains("recorded")
            || norm.Contains("timestamp") || norm.Contains("happened") || norm.Contains("capturedat")
            || norm.Contains("receivedat") || norm.Contains("sentat") || norm.Contains("postedat");

        private static string Normalize(string? name) =>
            new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private sealed class TemporalFieldAccumulator
        {
            public TemporalFieldAccumulator(string name, FieldInfo field)
            {
                Name = name;
                Field = field;
            }

            public string Name { get; }

            public FieldInfo Field { get; }

            public double PrevalenceWeight { get; set; }

            public int SchemaOccurrences { get; set; }
        }
    }
}
