using CosmosToSqlAssessment.Tests.Mocks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Meta-tests for the mock harness. These verify the primitives in <c>Mocks/</c>
/// behave as documented so later sub-issues (and Wave-2+ parents) can rely on them.
/// </summary>
public class MockHarnessTests
{
    [Fact]
    public async Task MockFeedIterator_OfDocuments_returns_items_in_single_page()
    {
        var docs = new[] { JObject.Parse("{\"id\":\"1\"}"), JObject.Parse("{\"id\":\"2\"}") };
        var iterator = MockFeedIterator.OfDocuments(docs);

        iterator.HasMoreResults.Should().BeTrue();
        var response = await iterator.ReadNextAsync(CancellationToken.None);
        response.Count().Should().Be(2);
        response.Select(j => (string?)j["id"]).Should().Equal("1", "2");

        iterator.HasMoreResults.Should().BeFalse();
    }

    [Fact]
    public async Task MockFeedIterator_OfPages_returns_each_page_in_order()
    {
        var page1 = new List<JObject> { JObject.Parse("{\"id\":\"a\"}") };
        var page2 = new List<JObject> { JObject.Parse("{\"id\":\"b\"}"), JObject.Parse("{\"id\":\"c\"}") };

        var iterator = MockFeedIterator.OfPages(new[] { page1, page2 });

        var allIds = new List<string?>();
        while (iterator.HasMoreResults)
        {
            var resp = await iterator.ReadNextAsync(CancellationToken.None);
            allIds.AddRange(resp.Select(j => (string?)j["id"]));
        }

        allIds.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task MockFeedIterator_Empty_HasMoreResults_is_false()
    {
        var iterator = MockFeedIterator.Empty<JObject>();
        iterator.HasMoreResults.Should().BeFalse();
        var resp = await iterator.ReadNextAsync(CancellationToken.None);
        resp.Count.Should().Be(0);
    }

    [Fact]
    public async Task MockFeedIterator_ThrowsOnRead_propagates_exception()
    {
        var ex = CosmosExceptionFactory.Throttled();
        var iterator = MockFeedIterator.ThrowsOnRead<JObject>(ex);

        iterator.HasMoreResults.Should().BeTrue();
        var act = async () => await iterator.ReadNextAsync(CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be((System.Net.HttpStatusCode)429);
    }

    [Fact]
    public void MockFeedResponse_can_be_enumerated_multiple_times()
    {
        // Critical: production code does response.Count() then foreach (...). Both must work.
        var docs = new[] { JObject.Parse("{\"id\":\"1\"}"), JObject.Parse("{\"id\":\"2\"}") };
        var response = new MockFeedResponse<JObject>(docs);

        response.Count().Should().Be(2);
        var collected = new List<string?>();
        foreach (var d in response)
        {
            collected.Add((string?)d["id"]);
        }
        collected.Should().Equal("1", "2");

        // Triple-enumerate to be sure.
        response.Sum(d => 1).Should().Be(2);
    }

    [Fact]
    public async Task CosmosClientMockBuilder_returns_configured_database()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("TestDb")
            .Build();

        var db = client.GetDatabase("TestDb");
        db.Should().NotBeNull();
        var resp = await db.ReadAsync();
        resp.Resource.Id.Should().Be("TestDb");
    }

    [Fact]
    public async Task CosmosClientMockBuilder_unknown_database_simulates_NotFound_on_read()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("KnownDb")
            .Build();

        var unknown = client.GetDatabase("Missing");
        var act = async () => await unknown.ReadAsync();
        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DatabaseMockBuilder_container_listing_returns_dynamic_with_id()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db
                .WithContainer("c1")
                .WithContainer("c2"))
            .Build();

        var db = client.GetDatabase("Db");
        var iterator = db.GetContainerQueryIterator<dynamic>();

        var names = new List<string>();
        while (iterator.HasMoreResults)
        {
            var resp = await iterator.ReadNextAsync(CancellationToken.None);
            foreach (var item in resp)
            {
                // Production code does: container.id.ToString()
                names.Add(((string)item.id.ToString()));
            }
        }

        names.Should().BeEquivalentTo(new[] { "c1", "c2" });
    }

    [Fact]
    public async Task ContainerMockBuilder_throughput_and_partition_key_round_trip()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db
                .WithContainer("orders", c => c
                    .WithPartitionKey("/customerId")
                    .WithThroughput(800)))
            .Build();

        var container = client.GetDatabase("Db").GetContainer("orders");
        container.Id.Should().Be("orders");

        var props = await container.ReadContainerAsync();
        props.Resource.PartitionKeyPath.Should().Be("/customerId");

        var throughput = await container.ReadThroughputAsync(cancellationToken: CancellationToken.None);
        throughput.Should().Be(800);
    }

    [Fact]
    public async Task ContainerMockBuilder_documents_can_be_round_tripped_as_dynamic_and_json()
    {
        // The two ways production accesses dynamic documents:
        //  1. doc.ToString() must return valid JSON parseable by JsonDocument.Parse.
        //  2. doc.id (dynamic member access) must yield the id field.
        var doc = JObject.Parse("{\"id\":\"abc\",\"value\":42}");
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithDocuments(doc)))
            .Build();

        var container = client.GetDatabase("Db").GetContainer("c");
        var iterator = container.GetItemQueryIterator<dynamic>(new QueryDefinition("SELECT * FROM c"));
        var resp = await iterator.ReadNextAsync(CancellationToken.None);

        foreach (var d in resp)
        {
            string json = d.ToString();
            json.Should().Contain("\"id\"").And.Contain("\"abc\"");
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            parsed.RootElement.GetProperty("id").GetString().Should().Be("abc");

            // Dynamic member access via Newtonsoft.Json's IDynamicMetaObjectProvider.
            ((string)d.id).Should().Be("abc");
        }
    }

    [Fact]
    public async Task ContainerMockBuilder_count_query_returns_document_count()
    {
        var docs = new[]
        {
            JObject.Parse("{\"id\":\"1\"}"),
            JObject.Parse("{\"id\":\"2\"}"),
            JObject.Parse("{\"id\":\"3\"}")
        };
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithDocuments(docs)))
            .Build();

        var container = client.GetDatabase("Db").GetContainer("c");
        var iterator = container.GetItemQueryIterator<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c"));

        iterator.HasMoreResults.Should().BeTrue();
        var resp = await iterator.ReadNextAsync(CancellationToken.None);
        resp.FirstOrDefault().Should().Be(3);
    }

    [Fact]
    public async Task ContainerMockBuilder_query_error_is_thrown_on_ReadNext()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithQueryError(CosmosExceptionFactory.ServiceUnavailable())))
            .Build();

        var container = client.GetDatabase("Db").GetContainer("c");
        var iterator = container.GetItemQueryIterator<dynamic>(new QueryDefinition("SELECT * FROM c"));

        var act = async () => await iterator.ReadNextAsync(CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ContainerMockBuilder_throughput_error_is_thrown()
    {
        var client = new CosmosClientMockBuilder()
            .WithDatabase("Db", db => db.WithContainer("c", c => c.WithThroughputError(CosmosExceptionFactory.Forbidden())))
            .Build();

        var container = client.GetDatabase("Db").GetContainer("c");
        var act = async () => await container.ReadThroughputAsync(cancellationToken: CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<CosmosException>();
        thrown.Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public void CosmosExceptionFactory_produces_expected_status_codes()
    {
        CosmosExceptionFactory.Throttled().StatusCode.Should().Be((System.Net.HttpStatusCode)429);
        CosmosExceptionFactory.ServiceUnavailable().StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        CosmosExceptionFactory.Timeout().StatusCode.Should().Be(System.Net.HttpStatusCode.RequestTimeout);
        CosmosExceptionFactory.Forbidden().StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        CosmosExceptionFactory.NotFound().StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        CosmosExceptionFactory.BadRequest().StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}
