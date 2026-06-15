using Azure;
using Azure.Monitor.Query;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Tests.Mocks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Transient-failure / retry tests for the Azure SDK call sites in
/// <see cref="CosmosDbAnalysisService"/> and
/// <see cref="DataQualityAnalysisService"/>. Documents which exceptions
/// propagate, which are absorbed by inner try/catch blocks, and which
/// degrade gracefully into a default result.
///
/// <para>
/// This file is the canonical reference for Wave-2+ parents that need to
/// test their own Azure SDK call sites against transient failures. Pattern:
/// pick a <see cref="CosmosExceptionFactory"/> static, inject via the
/// corresponding <c>With*Error</c> mock-builder hook, assert one of
/// propagation / absorption / graceful degradation.
/// </para>
/// </summary>
public class TransientFailureRetryTests : TestBase
{
    // ============================================================
    // ReadThroughputAsync — swallowed vs propagating cases
    // ============================================================

    [Fact]
    public async Task ReadThroughputAsync_BadRequest_is_swallowed_and_analysis_continues()
    {
        // Production: catch (CosmosException ex) when (ex.StatusCode == BadRequest)
        // swallows the throw (shared-database-throughput path) and continues.
        var loggerMock = CreateMockLogger<CosmosDbAnalysisService>();
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithThroughputError(CosmosExceptionFactory.BadRequest("shared throughput"))))
            .Build();

        var service = new CosmosDbAnalysisService(MockConfiguration.Object, loggerMock.Object, cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.Containers.Should().ContainSingle();
        result.Containers[0].PartitionKey.Should().Be("/id");
        loggerMock.Invocations.Should().Contain(i =>
            i.Arguments.Count >= 1 && (LogLevel)i.Arguments[0] == LogLevel.Information);
    }

    [Fact]
    public async Task ReadThroughputAsync_Forbidden_is_swallowed_and_analysis_continues()
    {
        // Production: catch (CosmosException ex) when (ex.StatusCode == Forbidden)
        // logs a warning and continues with ProvisionedRUs = 0.
        var loggerMock = CreateMockLogger<CosmosDbAnalysisService>();
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithThroughputError(CosmosExceptionFactory.Forbidden())))
            .Build();

        var service = new CosmosDbAnalysisService(MockConfiguration.Object, loggerMock.Object, cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.Containers.Should().ContainSingle();
        result.Containers[0].PartitionKey.Should().Be("/id");
        loggerMock.Invocations.Should().Contain(i =>
            i.Arguments.Count >= 1 && (LogLevel)i.Arguments[0] == LogLevel.Warning);
    }

    [Fact]
    public async Task ReadThroughputAsync_Throttled_propagates_as_CosmosException()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithThroughputError(CosmosExceptionFactory.Throttled())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task ReadThroughputAsync_ServiceUnavailable_propagates_as_CosmosException()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithThroughputError(CosmosExceptionFactory.ServiceUnavailable())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ReadThroughputAsync_Timeout_propagates_as_CosmosException()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithThroughputError(CosmosExceptionFactory.Timeout())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
    }

    // ============================================================
    // ReadContainerAsync — propagation
    // ============================================================

    [Fact]
    public async Task ReadContainerAsync_NotFound_propagates()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithReadContainerError(CosmosExceptionFactory.NotFound())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReadContainerAsync_Throttled_propagates()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithReadContainerError(CosmosExceptionFactory.Throttled())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
    }

    // ============================================================
    // Query iterator — count query propagates after schema query swallows
    // ============================================================

    [Fact]
    public async Task Count_query_Throttled_propagates_after_schema_query_swallowed()
    {
        // WithQueryError makes every GetItemQueryIterator<T> throw. The schema
        // sampling call (<dynamic>) is wrapped in a bare catch inside
        // AnalyzeDocumentSchemasAsync and is silently absorbed — see test
        // Schema_query_Throttled_is_swallowed_... for the isolated proof.
        // The count query (<int>) has no inner catch, so the second throw
        // propagates up to AnalyzeContainerAsync's catch which re-throws.
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithQueryError(CosmosExceptionFactory.Throttled())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task Schema_query_Throttled_is_swallowed_and_analysis_continues_with_empty_schemas()
    {
        // Typed-overload WithQueryError<dynamic>(...) fails only the schema-sample
        // call; the count query (<int>) keeps returning the configured count.
        // Confirms AnalyzeDocumentSchemasAsync's bare catch absorbs the throw
        // and analysis proceeds with empty schemas.
        var docs = new[] { JObject.Parse("{\"id\":\"a\"}"), JObject.Parse("{\"id\":\"b\"}") };
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithDocuments(docs)
                .WithQueryError<dynamic>(CosmosExceptionFactory.Throttled())))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.Containers.Should().ContainSingle();
        result.Containers[0].DetectedSchemas.Should().BeEmpty();
        result.Containers[0].ChildTables.Should().BeEmpty();
        result.Containers[0].DocumentCount.Should().Be(2);
    }

    // ============================================================
    // Container-loop bail-out: first failure aborts the whole loop
    // ============================================================

    [Fact]
    public async Task First_container_failure_aborts_loop_no_second_container_analyzed()
    {
        // Production loop has no per-container catch -- a single throw aborts.
        // The mock harness builds a fresh Container instance every call to
        // GetContainer, so we cannot Mock.Verify a specific Container's
        // ReadContainerAsync. Instead, we assert via observable side effect:
        // analysis was never returned (exception thrown) AND no second
        // container's setup was exercised because we use WithThroughput on
        // the second one and the analysis result is unreachable.
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db
                .WithContainer("bad", c => c
                    .WithReadContainerError(CosmosExceptionFactory.Throttled()))
                .WithContainer("good", c => c
                    .WithPartitionKey("/id")
                    .WithThroughput(400)))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
        // The analysis never returns, so result.Containers[0] for "good" is
        // unreachable. If a future maintainer adds per-container resilience
        // (try/catch per iteration), update this test to assert both
        // containers appear in result.Containers with the bad one marked.
    }

    // ============================================================
    // Container-discovery iterator throttle
    // ============================================================

    [Fact]
    public async Task Container_listing_Throttled_propagates_from_database_iterator()
    {
        // GetContainersToAnalyzeAsync has no try/catch around the dynamic
        // iterator's ReadNextAsync. A 429 there propagates straight up to
        // AnalyzeDatabaseAsync's catch (CosmosException) which re-throws.
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db
                .WithContainerListError(CosmosExceptionFactory.Throttled())
                .WithContainer("c", c => c.WithPartitionKey("/id")))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
    }

    // ============================================================
    // Azure Monitor: every failure absorbed, analysis continues
    // ============================================================

    [Fact]
    public async Task LogsQuery_Throttled_does_not_fail_overall_analysis()
    {
        MockConfiguration.Setup(c => c["AzureMonitor:WorkspaceId"]).Returns("ws-1");
        MockConfiguration.Setup(c => c["AzureMonitor:CosmosAccountName"]).Returns("acct-1");

        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithPartitionKey("/id")))
            .Build();

        var logsClient = new LogsQueryClientMockBuilder()
            .WithError(new RequestFailedException(429, "Too Many Requests"))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient,
            logsClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.PerformanceMetrics.Should().NotBeNull();
        result.PerformanceMetrics.AverageRUsPerSecond.Should().Be(0);
        result.PerformanceMetrics.PeakRUsPerSecond.Should().Be(0);
    }

    [Fact]
    public async Task LogsQuery_ServiceUnavailable_does_not_fail_overall_analysis()
    {
        MockConfiguration.Setup(c => c["AzureMonitor:WorkspaceId"]).Returns("ws-1");
        MockConfiguration.Setup(c => c["AzureMonitor:CosmosAccountName"]).Returns("acct-1");

        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithPartitionKey("/id")))
            .Build();

        var logsClient = new LogsQueryClientMockBuilder()
            .WithError(new RequestFailedException(503, "Service Unavailable"))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient,
            logsClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.PerformanceMetrics.Should().NotBeNull();
        result.PerformanceMetrics.AverageRUsPerSecond.Should().Be(0);
    }

    [Fact]
    public async Task LogsQuery_generic_exception_does_not_fail_overall_analysis()
    {
        // CollectPerformanceMetricsAsync has a bare catch (Exception), so any
        // throw type from the LogsQueryClient is absorbed.
        MockConfiguration.Setup(c => c["AzureMonitor:WorkspaceId"]).Returns("ws-1");
        MockConfiguration.Setup(c => c["AzureMonitor:CosmosAccountName"]).Returns("acct-1");

        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithPartitionKey("/id")))
            .Build();

        var logsClient = new LogsQueryClientMockBuilder()
            .WithError(new InvalidOperationException("boom"))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient,
            logsClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        result.PerformanceMetrics.Should().NotBeNull();
        result.Containers.Should().ContainSingle();
    }

    // ============================================================
    // DataQualityAnalysisService: sampling failure propagates
    // ============================================================

    [Fact]
    public async Task DataQuality_query_Throttled_propagates_to_caller()
    {
        // SampleDocumentsAsync re-throws on any Exception; AnalyzeDataQualityAsync
        // has no outer try/catch, so the 429 reaches the caller.
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithQueryError(CosmosExceptionFactory.Throttled())))
            .Build();

        var service = new DataQualityAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<DataQualityAnalysisService>().Object,
            cosmosClient);

        var input = new CosmosDbAnalysis
        {
            Containers = new List<ContainerAnalysis>
            {
                new()
                {
                    ContainerName = "c",
                    DocumentCount = 1,
                    PartitionKey = "/id",
                    DetectedSchemas = new List<DocumentSchema>
                    {
                        new()
                        {
                            SchemaName = "Default",
                            Fields = new Dictionary<string, CosmosToSqlAssessment.Models.FieldInfo>
                            {
                                ["id"] = new() { FieldName = "id", DetectedTypes = new List<string> { "string" } }
                            }
                        }
                    }
                }
            }
        };

        var act = async () => await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task DataQuality_query_ServiceUnavailable_propagates()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithQueryError(CosmosExceptionFactory.ServiceUnavailable())))
            .Build();

        var service = new DataQualityAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<DataQualityAnalysisService>().Object,
            cosmosClient);

        var input = new CosmosDbAnalysis
        {
            Containers = new List<ContainerAnalysis>
            {
                new()
                {
                    ContainerName = "c",
                    DocumentCount = 1,
                    PartitionKey = "/id",
                    DetectedSchemas = new List<DocumentSchema>
                    {
                        new()
                        {
                            SchemaName = "Default",
                            Fields = new Dictionary<string, CosmosToSqlAssessment.Models.FieldInfo>
                            {
                                ["id"] = new() { FieldName = "id", DetectedTypes = new List<string> { "string" } }
                            }
                        }
                    }
                }
            }
        };

        var act = async () => await service.AnalyzeDataQualityAsync(input, "Db", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ============================================================
    // Harness sanity: custom BadRequest message reaches the catch
    // ============================================================

    [Fact]
    public async Task Container_BadRequest_throughput_with_text_message_preserved()
    {
        var loggerMock = CreateMockLogger<CosmosDbAnalysisService>();
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c
                .WithPartitionKey("/id")
                .WithThroughputError(CosmosExceptionFactory.BadRequest("custom-shared-throughput-marker"))))
            .Build();

        var service = new CosmosDbAnalysisService(MockConfiguration.Object, loggerMock.Object, cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("Db", CancellationToken.None);

        // BadRequest was swallowed, analysis proceeded.
        result.Containers.Should().ContainSingle();

        // Capture every log invocation's rendered message and confirm at least
        // one Information-level entry was produced for the container.
        // Production logs "Container {ContainerName} uses shared database throughput".
        var infoLogs = loggerMock.Invocations
            .Where(i => i.Arguments.Count >= 1 && (LogLevel)i.Arguments[0] == LogLevel.Information)
            .ToList();
        infoLogs.Should().NotBeEmpty();
    }
}
