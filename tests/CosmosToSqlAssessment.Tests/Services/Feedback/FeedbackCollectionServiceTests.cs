using System.Runtime.CompilerServices;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.Feedback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class FeedbackCollectionServiceTests
{
    private static FeedbackCollectionService CreateService(
        FeedbackOptions options,
        Mock<IFeedbackStore>? store = null,
        Mock<IFeedbackTelemetrySink>? sink = null)
    {
        store ??= new Mock<IFeedbackStore>();
        store.SetupGet(s => s.Location).Returns("test-location.jsonl");
        sink ??= new Mock<IFeedbackTelemetrySink>();
        return new FeedbackCollectionService(
            store.Object, sink.Object, options, NullLogger<FeedbackCollectionService>.Instance);
    }

    [Fact]
    public async Task RecordOutcomeAsync_ConsentDenied_DoesNotPersistAndReturnsFalse()
    {
        var store = new Mock<IFeedbackStore>();
        var sink = new Mock<IFeedbackTelemetrySink>();
        var service = CreateService(new FeedbackOptions(), store, sink);

        var recorded = await service.RecordOutcomeAsync(new MigrationOutcome());

        recorded.Should().BeFalse();
        store.Verify(s => s.AppendAsync(It.IsAny<MigrationOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
        sink.Verify(s => s.SendAsync(It.IsAny<CoarsenedOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordOutcomeAsync_ConsentGrantedViaCli_PersistsLocally()
    {
        var store = new Mock<IFeedbackStore>();
        var sink = new Mock<IFeedbackTelemetrySink>();
        var service = CreateService(new FeedbackOptions(), store, sink);

        var recorded = await service.RecordOutcomeAsync(new MigrationOutcome(), commandLineOptIn: true);

        recorded.Should().BeTrue();
        store.Verify(s => s.AppendAsync(It.IsAny<MigrationOutcome>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordOutcomeAsync_ConsentGrantedViaConfig_PersistsLocally()
    {
        var store = new Mock<IFeedbackStore>();
        var service = CreateService(new FeedbackOptions { Enabled = true }, store);

        var recorded = await service.RecordOutcomeAsync(new MigrationOutcome());

        recorded.Should().BeTrue();
        store.Verify(s => s.AppendAsync(It.IsAny<MigrationOutcome>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordOutcomeAsync_NoTelemetryEndpoint_DoesNotTransmit()
    {
        var sink = new Mock<IFeedbackTelemetrySink>();
        var service = CreateService(new FeedbackOptions { Enabled = true }, sink: sink);

        await service.RecordOutcomeAsync(new MigrationOutcome());

        sink.Verify(s => s.SendAsync(It.IsAny<CoarsenedOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordOutcomeAsync_WithTelemetryEndpoint_TransmitsCoarsenedPayload()
    {
        var sink = new Mock<IFeedbackTelemetrySink>();
        var options = new FeedbackOptions { Enabled = true, TelemetryEndpoint = "https://example.test/ingest" };
        var service = CreateService(options, sink: sink);

        await service.RecordOutcomeAsync(new MigrationOutcome());

        sink.Verify(s => s.SendAsync(It.IsAny<CoarsenedOutcome>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordOutcomeAsync_NullOutcome_Throws()
    {
        var service = CreateService(new FeedbackOptions { Enabled = true });

        var act = () => service.RecordOutcomeAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetOutcomesAsync_DelegatesToStore()
    {
        var stored = new[]
        {
            new MigrationOutcome { OutcomeId = "x1" },
            new MigrationOutcome { OutcomeId = "x2" }
        };
        var store = new Mock<IFeedbackStore>();
        store.Setup(s => s.ReadAllAsync(It.IsAny<CancellationToken>())).Returns(ToAsync(stored));
        var service = CreateService(new FeedbackOptions(), store);

        var collected = new List<MigrationOutcome>();
        await foreach (var outcome in service.GetOutcomesAsync())
        {
            collected.Add(outcome);
        }

        collected.Select(o => o.OutcomeId).Should().Equal("x1", "x2");
    }

    [Fact]
    public void ResolveConsent_NoInputs_DefaultsToDisabled()
    {
        var service = CreateService(new FeedbackOptions());

        var consent = service.ResolveConsent();

        consent.IsGranted.Should().BeFalse();
        consent.Source.Should().Be(FeedbackConsentSource.Default);
    }

    [Fact]
    public void HasTelemetryEndpoint_ReflectsConfiguration()
    {
        CreateService(new FeedbackOptions()).HasTelemetryEndpoint.Should().BeFalse();
        CreateService(new FeedbackOptions { TelemetryEndpoint = "https://x.test" }).HasTelemetryEndpoint.Should().BeTrue();
    }

    [Fact]
    public void WriteConsentNotice_Granted_DescribesCollectionAndOptOut()
    {
        var service = CreateService(new FeedbackOptions());
        var writer = new StringWriter();

        service.WriteConsentNotice(writer, new FeedbackConsent(true, FeedbackConsentSource.CommandLine));

        var text = writer.ToString();
        text.Should().Contain("ENABLED");
        text.Should().Contain("test-location.jsonl");
        text.Should().Contain("NOT collected");
        text.Should().Contain("--disable-feedback");
    }

    [Fact]
    public void WriteConsentNotice_Denied_DescribesOptIn()
    {
        var service = CreateService(new FeedbackOptions());
        var writer = new StringWriter();

        service.WriteConsentNotice(writer, new FeedbackConsent(false, FeedbackConsentSource.Default));

        var text = writer.ToString();
        text.Should().Contain("DISABLED");
        text.Should().Contain("--enable-feedback");
    }

    private static async IAsyncEnumerable<MigrationOutcome> ToAsync(
        IEnumerable<MigrationOutcome> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask;
    }
}
