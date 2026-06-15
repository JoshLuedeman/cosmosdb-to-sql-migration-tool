using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Fluent builder that stands up a complete mock <see cref="CosmosClient"/>
/// hierarchy (client → databases → containers → documents + throughput +
/// indexing policy + query results) in a handful of lines.
///
/// <para>
/// Designed for reuse across the test suite and by every Wave-2+ parent that
/// needs to drive production code through Cosmos SDK calls without hitting a
/// real Azure resource. See <c>tests/CosmosToSqlAssessment.Tests/Mocks/README.md</c>
/// for a quick start.
/// </para>
///
/// <example>
/// <code>
/// var cosmosClient = new CosmosClientMockBuilder()
///     .WithDatabase("MyDb", db => db
///         .WithContainer("orders", c => c
///             .WithPartitionKey("/orderId")
///             .WithThroughput(400)
///             .WithDocuments(
///                 JObject.Parse("{ \"id\": \"1\", \"total\": 42.0 }"),
///                 JObject.Parse("{ \"id\": \"2\", \"total\": 9.99 }"))))
///     .Build();
/// </code>
/// </example>
/// </summary>
public sealed class CosmosClientMockBuilder
{
    private readonly Dictionary<string, DatabaseMockBuilder> _databases = new(StringComparer.Ordinal);

    /// <summary>Adds a database; <paramref name="configure"/> sets up its containers.</summary>
    public CosmosClientMockBuilder WithDatabase(string databaseId, Action<DatabaseMockBuilder>? configure = null)
    {
        var builder = new DatabaseMockBuilder(databaseId);
        configure?.Invoke(builder);
        _databases[databaseId] = builder;
        return this;
    }

    /// <summary>Materialises the fully-wired mock <see cref="CosmosClient"/>.</summary>
    public CosmosClient Build()
    {
        var clientMock = new Mock<CosmosClient>();
        clientMock
            .Setup(c => c.GetDatabase(It.IsAny<string>()))
            .Returns<string>(id =>
            {
                if (_databases.TryGetValue(id, out var b))
                {
                    return b.Build();
                }
                // Unknown database -- still return a usable Database whose
                // ReadAsync / queries simulate a 404 so production code's
                // catch handlers can be exercised.
                return new DatabaseMockBuilder(id).BuildAsMissing();
            });
        return clientMock.Object;
    }
}

/// <summary>
/// Inner builder for a single mock <see cref="Database"/>. Use
/// <see cref="WithContainer"/> to add containers; the database's container-listing
/// query iterator is materialised from those entries.
/// </summary>
public sealed class DatabaseMockBuilder
{
    private readonly string _databaseId;
    private readonly Dictionary<string, ContainerMockBuilder> _containers = new(StringComparer.Ordinal);
    private Exception? _containerListException;

    internal DatabaseMockBuilder(string databaseId)
    {
        _databaseId = databaseId;
    }

    /// <summary>
    /// Causes <c>Database.GetContainerQueryIterator&lt;dynamic&gt;().ReadNextAsync</c>
    /// to throw the provided exception on first read. Useful for #184-style
    /// transient-failure tests against the container-discovery path
    /// (production has no try/catch around that call).
    /// </summary>
    public DatabaseMockBuilder WithContainerListError(Exception exception)
    {
        _containerListException = exception;
        return this;
    }

    public DatabaseMockBuilder WithContainer(string containerId, Action<ContainerMockBuilder>? configure = null)
    {
        var builder = new ContainerMockBuilder(containerId);
        configure?.Invoke(builder);
        _containers[containerId] = builder;
        return this;
    }

    internal Database Build()
    {
        var dbMock = new Mock<Database>();

        dbMock.SetupGet(d => d.Id).Returns(_databaseId);

        dbMock
            .Setup(d => d.ReadAsync(It.IsAny<RequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockDatabaseResponse.Build(_databaseId));

        // Container listing iterator -- production calls
        // database.GetContainerQueryIterator<dynamic>() with no args. The compiled
        // call site supplies (null, null, null).
        // Container list pages: each element exposes an `id` member via dynamic.
        // JObject implements IDynamicMetaObjectProvider so `container.id` works.
        var containerListItems = _containers.Keys
            .Select(id => (dynamic)JObject.FromObject(new { id }))
            .ToList();

        if (_containerListException != null)
        {
            dbMock
                .Setup(d => d.GetContainerQueryIterator<dynamic>(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<QueryRequestOptions?>()))
                .Returns(() => MockFeedIterator.ThrowsOnRead<dynamic>(_containerListException));
        }
        else
        {
            dbMock
                .Setup(d => d.GetContainerQueryIterator<dynamic>(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<QueryRequestOptions?>()))
                .Returns(() => MockFeedIterator.OfDocuments(containerListItems));
        }

        // GetContainer(name) -- return wired container or a "missing" stub.
        dbMock
            .Setup(d => d.GetContainer(It.IsAny<string>()))
            .Returns<string>(name =>
            {
                if (_containers.TryGetValue(name, out var cb))
                {
                    return cb.Build();
                }
                return new ContainerMockBuilder(name).BuildAsMissing();
            });

        return dbMock.Object;
    }

    /// <summary>
    /// Builds a Database that simulates a 404 (NotFound) on any operation.
    /// Used when production calls <c>GetDatabase</c> for an id that wasn't
    /// configured on the builder, so tests can exercise error-handling paths.
    /// </summary>
    internal Database BuildAsMissing()
    {
        var dbMock = new Mock<Database>();
        dbMock.SetupGet(d => d.Id).Returns(_databaseId);
        dbMock
            .Setup(d => d.ReadAsync(It.IsAny<RequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(CosmosExceptionFactory.NotFound());
        dbMock
            .Setup(d => d.GetContainerQueryIterator<dynamic>(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<QueryRequestOptions?>()))
            .Returns(MockFeedIterator.Empty<dynamic>());
        dbMock
            .Setup(d => d.GetContainer(It.IsAny<string>()))
            .Returns<string>(name => new ContainerMockBuilder(name).BuildAsMissing());
        return dbMock.Object;
    }
}

/// <summary>
/// Inner builder for a single mock <see cref="Container"/>. Captures partition
/// key, throughput, indexing policy and the documents that should be returned
/// from <c>GetItemQueryIterator</c> calls.
/// </summary>
public sealed class ContainerMockBuilder
{
    private readonly string _containerId;
    private string _partitionKeyPath = "/id";
    private int? _throughput;
    private IndexingPolicy? _indexingPolicy;
    private readonly List<JObject> _documents = new();
    private Exception? _throughputException;
    private Exception? _readContainerException;
    private Exception? _queryException;
    private readonly Dictionary<Type, Exception> _typedQueryExceptions = new();

    internal ContainerMockBuilder(string containerId)
    {
        _containerId = containerId;
    }

    public ContainerMockBuilder WithPartitionKey(string path)
    {
        _partitionKeyPath = path;
        return this;
    }

    /// <summary>Sets the value returned from <c>ReadThroughputAsync()</c> (the <c>int?</c> overload).</summary>
    public ContainerMockBuilder WithThroughput(int? throughput)
    {
        _throughput = throughput;
        _throughputException = null;
        return this;
    }

    /// <summary>Causes <c>ReadThroughputAsync()</c> to throw the provided exception.</summary>
    public ContainerMockBuilder WithThroughputError(Exception exception)
    {
        _throughputException = exception;
        return this;
    }

    public ContainerMockBuilder WithIndexingPolicy(IndexingPolicy policy)
    {
        _indexingPolicy = policy;
        return this;
    }

    /// <summary>
    /// Adds documents that will be returned by <c>GetItemQueryIterator&lt;T&gt;</c> calls.
    /// Documents must be <see cref="JObject"/> so they satisfy both:
    /// dynamic member access (<c>doc.id</c>) and JSON round-trip via <c>ToString()</c>.
    /// </summary>
    public ContainerMockBuilder WithDocuments(params JObject[] documents)
    {
        _documents.AddRange(documents);
        return this;
    }

    public ContainerMockBuilder WithDocuments(IEnumerable<JObject> documents)
    {
        _documents.AddRange(documents);
        return this;
    }

    /// <summary>Causes <c>ReadContainerAsync</c> to throw the provided exception.</summary>
    public ContainerMockBuilder WithReadContainerError(Exception exception)
    {
        _readContainerException = exception;
        return this;
    }

    /// <summary>
    /// Causes <c>GetItemQueryIterator&lt;T&gt;.ReadNextAsync</c> to throw the
    /// provided exception on first read, for **every** generic parameter
    /// (<c>dynamic</c>, <c>JsonDocument</c>, <c>int</c>). Use the typed overload
    /// <see cref="WithQueryError{T}(Exception)"/> when you need to fail only
    /// one specific call site (e.g. schema-sample but not the count query).
    /// Useful for transient-failure / retry tests (#184).
    /// </summary>
    public ContainerMockBuilder WithQueryError(Exception exception)
    {
        _queryException = exception;
        return this;
    }

    /// <summary>
    /// Causes <c>GetItemQueryIterator&lt;T&gt;.ReadNextAsync</c> to throw the
    /// provided exception on first read **only for the requested generic
    /// parameter <typeparamref name="T"/></b>. The other overloads keep
    /// returning their configured documents. Required to isolate
    /// production branches that swallow a query failure on one type but
    /// propagate on another (e.g. <c>AnalyzeDocumentSchemasAsync</c>
    /// swallows <c>&lt;dynamic&gt;</c> failures but
    /// <c>AnalyzeContainerAsync</c>'s count query
    /// <c>&lt;int&gt;</c> propagates them).
    /// </summary>
    public ContainerMockBuilder WithQueryError<T>(Exception exception)
    {
        _typedQueryExceptions[typeof(T)] = exception;
        return this;
    }

    internal Container Build()
    {
        var containerMock = new Mock<Container>();

        containerMock.SetupGet(c => c.Id).Returns(_containerId);

        // ReadContainerAsync
        if (_readContainerException != null)
        {
            containerMock
                .Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(_readContainerException);
        }
        else
        {
            var containerResponse = MockContainerResponse.Build(
                _containerId,
                _partitionKeyPath,
                _indexingPolicy ?? new IndexingPolicy());
            containerMock
                .Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(containerResponse);
        }

        // ReadThroughputAsync (int? overload). Production binds to:
        //   container.ReadThroughputAsync(cancellationToken: ct)
        // which resolves to the (CancellationToken) overload returning Task<int?>.
        if (_throughputException != null)
        {
            containerMock
                .Setup(c => c.ReadThroughputAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(_throughputException);
        }
        else
        {
            containerMock
                .Setup(c => c.ReadThroughputAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_throughput);
        }

        // GetItemQueryIterator<dynamic>(QueryDefinition, ...) -- schema sampling
        SetupItemQueryIterator<dynamic>(containerMock, _documents.Cast<dynamic>().ToList());

        // GetItemQueryIterator<JsonDocument>(QueryDefinition, ...) -- DataQualityAnalysisService
        var jsonDocs = _documents
            .Select(j => System.Text.Json.JsonDocument.Parse(j.ToString()))
            .ToList();
        SetupItemQueryIterator<System.Text.Json.JsonDocument>(containerMock, jsonDocs);

        // GetItemQueryIterator<int>(QueryDefinition, ...) -- count queries
        SetupItemQueryIterator<int>(containerMock, new List<int> { _documents.Count });

        return containerMock.Object;
    }

    private void SetupItemQueryIterator<T>(Mock<Container> containerMock, List<T> items)
    {
        if (_typedQueryExceptions.TryGetValue(typeof(T), out var typedEx))
        {
            containerMock
                .Setup(c => c.GetItemQueryIterator<T>(
                    It.IsAny<QueryDefinition>(),
                    It.IsAny<string?>(),
                    It.IsAny<QueryRequestOptions?>()))
                .Returns(MockFeedIterator.ThrowsOnRead<T>(typedEx));
        }
        else if (_queryException != null)
        {
            containerMock
                .Setup(c => c.GetItemQueryIterator<T>(
                    It.IsAny<QueryDefinition>(),
                    It.IsAny<string?>(),
                    It.IsAny<QueryRequestOptions?>()))
                .Returns(MockFeedIterator.ThrowsOnRead<T>(_queryException));
        }
        else
        {
            containerMock
                .Setup(c => c.GetItemQueryIterator<T>(
                    It.IsAny<QueryDefinition>(),
                    It.IsAny<string?>(),
                    It.IsAny<QueryRequestOptions?>()))
                .Returns(() => MockFeedIterator.OfDocuments(items));
        }
    }

    /// <summary>
    /// Builds a Container whose every operation throws <see cref="CosmosExceptionFactory.NotFound"/>.
    /// Used when production calls <c>GetContainer</c> for an id that wasn't configured.
    /// </summary>
    internal Container BuildAsMissing()
    {
        var containerMock = new Mock<Container>();
        containerMock.SetupGet(c => c.Id).Returns(_containerId);
        containerMock
            .Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(CosmosExceptionFactory.NotFound());
        containerMock
            .Setup(c => c.ReadThroughputAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(CosmosExceptionFactory.NotFound());
        return containerMock.Object;
    }
}
