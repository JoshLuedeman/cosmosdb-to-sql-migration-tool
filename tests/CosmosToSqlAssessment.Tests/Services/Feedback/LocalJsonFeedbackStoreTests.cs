using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.Feedback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class LocalJsonFeedbackStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storePath;

    public LocalJsonFeedbackStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cosmos2sql-feedback-tests", Guid.NewGuid().ToString("N"));
        _storePath = Path.Combine(_tempDir, "outcomes.jsonl");
    }

    private LocalJsonFeedbackStore CreateStore() =>
        new(new FeedbackOptions { StorePath = _storePath }, NullLogger<LocalJsonFeedbackStore>.Instance);

    [Fact]
    public void Location_UsesConfiguredStorePath()
    {
        CreateStore().Location.Should().Be(_storePath);
    }

    [Fact]
    public void GetDefaultPath_ReturnsAbsolutePathUnderAppData()
    {
        var path = LocalJsonFeedbackStore.GetDefaultPath();

        Path.IsPathRooted(path).Should().BeTrue();
        path.Should().EndWith(Path.Combine("CosmosToSqlAssessment", "feedback", "migration-outcomes.jsonl"));
    }

    [Fact]
    public async Task ReadAllAsync_MissingFile_YieldsEmpty()
    {
        var store = CreateStore();

        var outcomes = await CollectAsync(store);

        outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_ThenReadAll_RoundTripsRecords()
    {
        var store = CreateStore();
        var first = new MigrationOutcome { OutcomeId = "a1", Status = MigrationOutcomeStatus.Succeeded, ActualMigrationDays = 4 };
        var second = new MigrationOutcome { OutcomeId = "b2", Status = MigrationOutcomeStatus.Failed, ActualMigrationDays = 12 };

        await store.AppendAsync(first);
        await store.AppendAsync(second);
        var outcomes = await CollectAsync(store);

        outcomes.Should().HaveCount(2);
        outcomes[0].OutcomeId.Should().Be("a1");
        outcomes[0].Status.Should().Be(MigrationOutcomeStatus.Succeeded);
        outcomes[1].OutcomeId.Should().Be("b2");
        outcomes[1].ActualMigrationDays.Should().Be(12);
    }

    [Fact]
    public async Task ReadAllAsync_SkipsMalformedAndBlankLines()
    {
        var store = CreateStore();
        await store.AppendAsync(new MigrationOutcome { OutcomeId = "good1" });

        // Inject a malformed line and a blank line, then a valid record.
        await File.AppendAllTextAsync(_storePath, "this is not json" + Environment.NewLine);
        await File.AppendAllTextAsync(_storePath, Environment.NewLine);
        await store.AppendAsync(new MigrationOutcome { OutcomeId = "good2" });

        var outcomes = await CollectAsync(store);

        outcomes.Should().HaveCount(2);
        outcomes.Select(o => o.OutcomeId).Should().Equal("good1", "good2");
    }

    [Fact]
    public async Task AppendAsync_ConcurrentWrites_PersistAllRecords()
    {
        var store = CreateStore();

        var tasks = Enumerable.Range(0, 25)
            .Select(i => store.AppendAsync(new MigrationOutcome { OutcomeId = $"c{i}" }));
        await Task.WhenAll(tasks);

        var outcomes = await CollectAsync(store);
        outcomes.Should().HaveCount(25);
    }

    private static async Task<List<MigrationOutcome>> CollectAsync(IFeedbackStore store)
    {
        var list = new List<MigrationOutcome>();
        await foreach (var outcome in store.ReadAllAsync())
        {
            list.Add(outcome);
        }

        return list;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
