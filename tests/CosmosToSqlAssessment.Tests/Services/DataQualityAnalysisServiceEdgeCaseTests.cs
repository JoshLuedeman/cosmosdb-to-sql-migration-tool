using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Tests.Mocks;
using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Edge-case coverage for <see cref="DataQualityAnalysisService"/>'s analyzer
/// branches: null thresholds, duplicate detection (id / partition key /
/// business key), type-consistency mismatches, string-length recommendation
/// boundaries, encoding (non-ASCII / control / emoji), date validation, and
/// per-analyzer enable/disable toggles. Sits on top of the mock harness from
/// sub-issue #180 and is the data-quality companion to the
/// <see cref="SqlProjectGenerationServiceEdgeCaseTests"/> file added under
/// sub-issue #182.
/// </summary>
public class DataQualityAnalysisServiceEdgeCaseTests : TestBase
{
    private DataQualityAnalysisService BuildService(
        Microsoft.Azure.Cosmos.CosmosClient cosmosClient,
        DataQualityAnalysisOptions? options = null)
        => new(
            MockConfiguration.Object,
            CreateMockLogger<DataQualityAnalysisService>().Object,
            cosmosClient,
            options);

    private static CosmosDbAnalysis BuildCosmosAnalysis(
        string containerName,
        int documentCount,
        string partitionKey = "/id",
        params string[] extraFields)
    {
        var fields = new Dictionary<string, CosmosToSqlAssessment.Models.FieldInfo>
        {
            ["id"] = new() { FieldName = "id", DetectedTypes = new List<string> { "string" } }
        };
        foreach (var f in extraFields)
        {
            fields[f] = new CosmosToSqlAssessment.Models.FieldInfo
            {
                FieldName = f,
                DetectedTypes = new List<string> { "string" }
            };
        }

        return new CosmosDbAnalysis
        {
            Containers = new List<ContainerAnalysis>
            {
                new()
                {
                    ContainerName = containerName,
                    DocumentCount = documentCount,
                    PartitionKey = partitionKey,
                    DetectedSchemas = new List<DocumentSchema>
                    {
                        new()
                        {
                            SchemaName = "Default",
                            Fields = fields,
                            SampleCount = documentCount,
                            Prevalence = 1.0
                        }
                    }
                }
            }
        };
    }

    private static Microsoft.Azure.Cosmos.CosmosClient BuildClient(
        string database,
        string container,
        string partitionKey,
        params JObject[] docs)
        => new CosmosClientMockBuilder()
            .WithDatabase(database, db => db
                .WithContainer(container, c => c
                    .WithPartitionKey(partitionKey)
                    .WithDocuments(docs)))
            .Build();

    // -------------------------------------------------------------------
    // Null analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task Null_threshold_below_info_emits_no_null_issue()
    {
        var docs = Enumerable.Range(1, 100)
            .Select(i => JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i}}}"))
            .ToArray();

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", 100, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var container = result.ContainerAnalyses.Single();
        container.NullAnalysis.Should().NotContain(n => n.FieldName == "value");
    }

    [Fact]
    public async Task Null_threshold_between_info_and_warning_emits_info_severity()
    {
        var docs = new List<JObject>();
        for (var i = 1; i <= 97; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i}}}"));
        for (var i = 98; i <= 100; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":null}}"));

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 100, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var container = result.ContainerAnalyses.Single();
        container.NullAnalysis.Should().Contain(n => n.FieldName == "value");

        var issue = container.AllIssues.Should()
            .ContainSingle(i => i.Category == "Null" && i.FieldName == "value").Subject;
        issue.Severity.Should().Be(DataQualitySeverity.Info);
    }

    [Fact]
    public async Task Null_threshold_above_critical_emits_critical_and_blocks_migration()
    {
        var docs = new List<JObject>();
        for (var i = 1; i <= 80; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i}}}"));
        for (var i = 81; i <= 100; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":null}}"));

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 100, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.CriticalIssuesCount.Should().BeGreaterThan(0);
        result.Summary.ReadyForMigration.Should().BeFalse();
        result.Summary.BlockingIssues.Should().NotBeEmpty();
        result.ContainerAnalyses.Single()
            .AllIssues.Should().Contain(i => i.Category == "Null" && i.Severity == DataQualitySeverity.Critical);
    }

    [Fact]
    public async Task Null_value_detection_treats_string_null_literal_and_whitespace_as_null()
    {
        var docs = new List<JObject>
        {
            JObject.Parse("{\"id\":\"d1\",\"value\":\"actual\"}"),
            JObject.Parse("{\"id\":\"d2\",\"value\":\"null\"}"),
            JObject.Parse("{\"id\":\"d3\",\"value\":\"   \"}"),
            JObject.Parse("{\"id\":\"d4\",\"value\":\"\"}"),
        };
        for (var i = 5; i <= 50; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":\"v{i}\"}}"));

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 50, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var nullEntry = result.ContainerAnalyses.Single()
            .NullAnalysis.Should().Contain(n => n.FieldName == "value").Subject;
        nullEntry.NullCount.Should().Be(3);
        nullEntry.MissingCount.Should().Be(0);
    }

    // -------------------------------------------------------------------
    // Duplicate analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_ids_emit_id_duplicate_with_critical_recommendation()
    {
        var docs = Enumerable.Range(1, 10)
            .Select(i => JObject.Parse($"{{\"id\":\"shared\",\"pk\":\"p{i}\"}}"))
            .ToArray();

        var service = BuildService(BuildClient("Db", "c", "/pk", docs));
        var input = BuildCosmosAnalysis("c", 10, "/pk", "pk");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var container = result.ContainerAnalyses.Single();
        var idDup = container.DuplicateAnalysis.Should()
            .ContainSingle(d => d.KeyType == "ID").Subject;
        idDup.DuplicateGroupCount.Should().Be(1);
        idDup.TotalDuplicateRecords.Should().Be(10);
        idDup.RecommendedResolution.Should().StartWith("Critical");
        container.AllIssues.Should().Contain(i => i.Category == "Duplicate" && i.Severity == DataQualitySeverity.Critical);
    }

    [Fact]
    public async Task Duplicate_business_keys_detect_email_username_code()
    {
        var docs = new List<JObject>();
        for (var i = 1; i <= 3; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"e{i}\",\"userId\":\"u{i}\",\"email\":\"shared@example.com\",\"username\":\"alice{i}\",\"code\":\"c{i}\"}}"));
        for (var i = 1; i <= 3; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"u{i}\",\"userId\":\"v{i}\",\"email\":\"a{i}@example.com\",\"username\":\"sharedUser\",\"code\":\"d{i}\"}}"));
        for (var i = 1; i <= 3; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"c{i}\",\"userId\":\"w{i}\",\"email\":\"b{i}@example.com\",\"username\":\"bob{i}\",\"code\":\"SHARED\"}}"));

        var service = BuildService(BuildClient("Db", "c", "/userId", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 9, "/userId", "email", "username", "code", "userId");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var businessKeyEntries = result.ContainerAnalyses.Single()
            .DuplicateAnalysis.Where(d => d.KeyType == "BusinessKey").ToList();
        var fields = businessKeyEntries.SelectMany(d => d.KeyFields).Distinct().ToList();
        fields.Should().Contain(new[] { "email", "username", "code" });
    }

    [Fact]
    public async Task Duplicate_detection_disabled_skips_analyzer()
    {
        var docs = Enumerable.Range(1, 10)
            .Select(i => JObject.Parse($"{{\"id\":\"shared\",\"pk\":\"p{i}\"}}"))
            .ToArray();

        var options = new DataQualityAnalysisOptions { IncludeDuplicateDetection = false };
        var service = BuildService(BuildClient("Db", "c", "/pk", docs), options);
        var input = BuildCosmosAnalysis("c", 10, "/pk", "pk");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.ContainerAnalyses.Single().DuplicateAnalysis.Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    // Type consistency analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task Type_consistency_with_mixed_types_below_threshold_records_mismatches()
    {
        var docs = new List<JObject>();
        for (var i = 1; i <= 7; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":\"text{i}\"}}"));
        for (var i = 8; i <= 10; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i}}}"));

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 10, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var typeResult = result.ContainerAnalyses.Single()
            .TypeConsistency.Should().ContainSingle(t => t.FieldName == "value").Subject;
        typeResult.IsConsistent.Should().BeFalse();
        typeResult.DominantType.Should().Be("string");
        typeResult.Mismatches.Should().NotBeEmpty();
        typeResult.RecommendedSqlType.Should().Be("NVARCHAR(MAX)");
    }

    [Fact]
    public async Task Type_consistency_above_threshold_emits_nothing()
    {
        var docs = Enumerable.Range(1, 100)
            .Select(i => JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i}}}"))
            .ToArray();

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", 100, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.ContainerAnalyses.Single()
            .TypeConsistency.Should().NotContain(t => t.FieldName == "value");
    }

    // -------------------------------------------------------------------
    // String-length analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task String_length_recommendation_picks_NVARCHAR_max_for_large_strings()
    {
        var big = new string('a', 5000);
        var docs = Enumerable.Range(1, 10)
            .Select(i => new JObject
            {
                ["id"] = $"d{i}",
                ["description"] = big
            })
            .ToArray();

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", 10, "/id", "description");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var len = result.ContainerAnalyses.Single()
            .StringLengthAnalysis.Should().ContainSingle(l => l.FieldName == "description").Subject;
        len.RecommendedSqlType.Should().Be("NVARCHAR(MAX)");
        len.RecommendedAction.Should().Contain("NVARCHAR(MAX)");
        result.ContainerAnalyses.Single()
            .AllIssues.Should().Contain(i => i.Category == "Length" && i.FieldName == "description");
    }

    [Fact]
    public async Task String_length_recommendation_picks_NVARCHAR_with_p95_below_4000()
    {
        var lengths = new[] { 10, 20, 30, 40, 50, 60, 80, 100, 150, 200 };
        var docs = lengths.Select((l, i) => new JObject
        {
            ["id"] = $"d{i}",
            ["name"] = new string('n', l)
        }).ToArray();

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", lengths.Length, "/id", "name");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var len = result.ContainerAnalyses.Single()
            .StringLengthAnalysis.Should().ContainSingle(l => l.FieldName == "name").Subject;
        len.RecommendedSqlType.Should().StartWith("NVARCHAR(");
        len.RecommendedSqlType.Should().NotBe("NVARCHAR(MAX)");
        len.MaxLength.Should().Be(200);
        len.MinLength.Should().Be(10);
    }

    [Fact]
    public async Task String_length_skips_when_below_minimum_strings()
    {
        var docs = Enumerable.Range(1, 4)
            .Select(i => new JObject
            {
                ["id"] = $"d{i}",
                ["name"] = $"value{i}"
            })
            .ToArray();

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", 4, "/id", "name");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.ContainerAnalyses.Single()
            .StringLengthAnalysis.Should().NotContain(l => l.FieldName == "name");
    }

    // -------------------------------------------------------------------
    // Outlier analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task Outlier_detection_disabled_skips_analyzer()
    {
        var docs = new List<JObject>();
        for (var i = 1; i <= 50; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":100}}"));
        docs.Add(JObject.Parse("{\"id\":\"hi\",\"value\":1000000}"));

        var options = new DataQualityAnalysisOptions { IncludeOutlierDetection = false };
        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()), options);
        var input = BuildCosmosAnalysis("c", docs.Count, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.ContainerAnalyses.Single().OutlierAnalysis.Should().BeEmpty();
    }

    [Fact]
    public async Task Outlier_detection_finds_iqr_outliers_above_minimum_samples()
    {
        var docs = new List<JObject>();
        var rng = new Random(42);
        for (var i = 1; i <= 50; i++)
            docs.Add(JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{100 + rng.Next(-2, 3)}}}"));
        docs.Add(JObject.Parse("{\"id\":\"hi\",\"value\":10000}"));
        docs.Add(JObject.Parse("{\"id\":\"lo\",\"value\":-10000}"));

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", docs.Count, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var outlier = result.ContainerAnalyses.Single()
            .OutlierAnalysis.Should().ContainSingle(o => o.FieldName == "value").Subject;
        outlier.OutlierCount.Should().BeGreaterThanOrEqualTo(2);
        outlier.OutlierPercentage.Should().BeGreaterThan(1.0);
        outlier.OutlierSamples.Select(s => s.OutlierType).Should()
            .Contain("High").And.Contain("Low");
    }

    // -------------------------------------------------------------------
    // Encoding analyzer
    // -------------------------------------------------------------------

    [Fact]
    public async Task Encoding_analyzer_detects_non_ascii_control_and_emoji()
    {
        // U+2600 (☀) is in ContainsEmoji's 0x2600-0x26FF BMP range AND non-ASCII
        // U+0001 is a control char (and ASCII), excluded from \n\r\t guard
        // "café" is non-ASCII but no emoji / control
        var docs = new[]
        {
            JObject.Parse("{\"id\":\"d1\",\"text\":\"café\"}"),
            JObject.Parse("{\"id\":\"d2\",\"text\":\"line\\u0001separator\"}"),
            new JObject { ["id"] = "d3", ["text"] = "weather \u2600 today" },
            JObject.Parse("{\"id\":\"d4\",\"text\":\"plain\"}"),
            JObject.Parse("{\"id\":\"d5\",\"text\":\"another\"}"),
        };

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", docs.Length, "/id", "text");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var encoding = result.ContainerAnalyses.Single().EncodingIssues
            .Where(e => e.FieldName == "text")
            .ToList();
        var types = encoding.Select(e => e.IssueType).Distinct().ToList();
        types.Should().Contain(new[] { "NonASCII", "ControlCharacters", "Emoji" });
    }

    [Fact]
    public async Task Encoding_checks_disabled_skips_analyzer()
    {
        var docs = new[]
        {
            new JObject { ["id"] = "d1", ["text"] = "café" },
            new JObject { ["id"] = "d2", ["text"] = "weather \u2600 today" },
            new JObject { ["id"] = "d3", ["text"] = "plain1" },
            new JObject { ["id"] = "d4", ["text"] = "plain2" },
            new JObject { ["id"] = "d5", ["text"] = "plain3" },
        };

        var options = new DataQualityAnalysisOptions { IncludeEncodingChecks = false };
        var service = BuildService(BuildClient("Db", "c", "/id", docs), options);
        var input = BuildCosmosAnalysis("c", docs.Length, "/id", "text");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.ContainerAnalyses.Single().EncodingIssues.Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    // Date validation
    // -------------------------------------------------------------------

    [Fact]
    public async Task Date_validation_flags_invalid_future_and_old_dates()
    {
        var docs = new[]
        {
            JObject.Parse("{\"id\":\"valid\",\"when\":\"2010-06-15\"}"),
            JObject.Parse("{\"id\":\"invalid\",\"when\":\"2024-13-99\"}"),
            JObject.Parse("{\"id\":\"future\",\"when\":\"2050-01-01\"}"),
            JObject.Parse("{\"id\":\"old\",\"when\":\"1850-01-01\"}"),
            JObject.Parse("{\"id\":\"valid2\",\"when\":\"2015-12-25\"}"),
        };

        // Deterministic window: future = > 2020, very-old = < 2000
        var options = new DataQualityAnalysisOptions
        {
            MinReasonableDate = new DateTime(2000, 1, 1),
            MaxReasonableDate = new DateTime(2020, 12, 31)
        };

        var service = BuildService(BuildClient("Db", "c", "/id", docs), options);
        var input = BuildCosmosAnalysis("c", docs.Length, "/id", "when");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var dateResult = result.ContainerAnalyses.Single()
            .DateValidation.Should().ContainSingle(d => d.FieldName == "when").Subject;
        dateResult.InvalidDateCount.Should().Be(1);
        dateResult.FutureDateCount.Should().Be(1);
        dateResult.VeryOldDateCount.Should().Be(1);
        result.ContainerAnalyses.Single().AllIssues
            .Should().Contain(i => i.Category == "Date" && i.Severity == DataQualitySeverity.Critical);
    }

    // -------------------------------------------------------------------
    // Malformed documents
    // -------------------------------------------------------------------

    [Fact]
    public async Task Malformed_document_without_id_uses_unknown_marker_in_samples()
    {
        var docs = new[]
        {
            JObject.Parse("{\"value\":\"v1\"}"),
            JObject.Parse("{\"value\":null}"),
            JObject.Parse("{\"value\":null}"),
            JObject.Parse("{\"value\":\"v2\"}"),
            JObject.Parse("{\"value\":null}"),
        };

        var service = BuildService(BuildClient("Db", "c", "/id", docs));
        var input = BuildCosmosAnalysis("c", docs.Length, "/id", "value");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var nullEntry = result.ContainerAnalyses.Single()
            .NullAnalysis.Should().Contain(n => n.FieldName == "value").Subject;
        nullEntry.SampleNullDocumentIds.Should().NotBeEmpty();
        nullEntry.SampleNullDocumentIds.Should().Contain("unknown");
    }

    // -------------------------------------------------------------------
    // TopIssues ordering (replaces original #19 per rubber-duck R6)
    // -------------------------------------------------------------------

    [Fact]
    public async Task Top_issues_are_sorted_with_critical_first()
    {
        // Field "criticalNull" gets 20 % nulls -> Critical
        // Field "warningNull"  gets 10 % nulls -> Warning
        // Field "infoNull"     gets 3  % nulls -> Info
        var docs = new List<JObject>();
        for (var i = 1; i <= 100; i++)
        {
            var critical = i <= 80 ? $"\"c{i}\"" : "null";
            var warning  = i <= 90 ? $"\"w{i}\"" : "null";
            var info     = i <= 97 ? $"\"f{i}\"" : "null";
            docs.Add(JObject.Parse(
                $"{{\"id\":\"d{i}\",\"criticalNull\":{critical},\"warningNull\":{warning},\"infoNull\":{info}}}"));
        }

        var service = BuildService(BuildClient("Db", "c", "/id", docs.ToArray()));
        var input = BuildCosmosAnalysis("c", 100, "/id", "criticalNull", "warningNull", "infoNull");

        var result = await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        result.TopIssues.Should().NotBeEmpty();
        result.TopIssues.Count.Should().BeLessThanOrEqualTo(20);
        result.TopIssues[0].Severity.Should().Be(DataQualitySeverity.Critical);

        var severities = result.TopIssues.Select(i => (int)i.Severity).ToList();
        severities.Should().BeInDescendingOrder();
    }
}
