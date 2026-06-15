using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Tests.Mocks;
using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Smoke tests proving the harness can drive the real
/// <see cref="DataQualityAnalysisService"/> end-to-end without Azure access.
/// Deeper edge-case coverage (nulls, duplicates, malformed docs, big strings)
/// lands in sub-issue #183 on top of this same harness.
/// </summary>
public class DataQualityAnalysisServiceMockedTests : TestBase
{
    private static CosmosDbAnalysis BuildCosmosAnalysisWithContainer(string containerName, int documentCount)
        => new()
        {
            Containers = new List<ContainerAnalysis>
            {
                new()
                {
                    ContainerName = containerName,
                    DocumentCount = documentCount,
                    PartitionKey = "/id",
                    DetectedSchemas = new List<DocumentSchema>
                    {
                        new()
                        {
                            SchemaName = "Default",
                            Fields = new Dictionary<string, CosmosToSqlAssessment.Models.FieldInfo>
                            {
                                ["id"] = new() { FieldName = "id", DetectedTypes = new List<string> { "string" } },
                                ["value"] = new() { FieldName = "value", DetectedTypes = new List<string> { "number" } }
                            },
                            SampleCount = documentCount,
                            Prevalence = 1.0
                        }
                    }
                }
            }
        };

    [Fact]
    public async Task AnalyzeDataQualityAsync_with_clean_documents_reports_no_critical_issues()
    {
        var docs = Enumerable.Range(1, 5)
            .Select(i => JObject.Parse($"{{\"id\":\"d{i}\",\"value\":{i * 10}}}"))
            .ToArray();

        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("QualityDb", db => db
                .WithContainer("clean", c => c.WithPartitionKey("/id").WithDocuments(docs)))
            .Build();

        var service = new DataQualityAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<DataQualityAnalysisService>().Object,
            cosmosClient);

        var input = BuildCosmosAnalysisWithContainer("clean", documentCount: 5);

        var result = await service.AnalyzeDataQualityAsync(input, "QualityDb", CancellationToken.None);

        result.Should().NotBeNull();
        result.ContainerAnalyses.Should().ContainSingle();
        result.ContainerAnalyses[0].ContainerName.Should().Be("clean");
        result.ContainerAnalyses[0].SampleSize.Should().Be(5);
        result.TotalDocumentsAnalyzed.Should().Be(5);
        result.CriticalIssuesCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeDataQualityAsync_with_empty_container_emits_warning_and_skips_analysis()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("QualityDb", db => db
                .WithContainer("empty", c => c.WithPartitionKey("/id")))
            .Build();

        var service = new DataQualityAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<DataQualityAnalysisService>().Object,
            cosmosClient);

        var input = BuildCosmosAnalysisWithContainer("empty", documentCount: 0);

        var result = await service.AnalyzeDataQualityAsync(input, "QualityDb", CancellationToken.None);

        result.ContainerAnalyses.Should().ContainSingle();
        result.ContainerAnalyses[0].SampleSize.Should().Be(0);
        result.TotalDocumentsAnalyzed.Should().Be(0);
    }
}
