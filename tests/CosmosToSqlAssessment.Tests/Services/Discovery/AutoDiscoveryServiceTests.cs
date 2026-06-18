using CosmosToSqlAssessment.Services.Discovery;
using Microsoft.Extensions.Logging;
using Moq;

namespace CosmosToSqlAssessment.Tests.Services.Discovery;

public class AutoDiscoveryServiceTests
{
    private readonly Mock<IResourceGraphDiscoveryService> _mockResourceGraph;
    private readonly Mock<IDiagnosticSettingsDiscoveryService> _mockDiagSettings;
    private readonly Mock<ILogger<AutoDiscoveryService>> _mockLogger;
    private readonly AutoDiscoveryService _service;

    private const string ValidEndpoint = "https://myaccount.documents.azure.com:443/";
    private static readonly CosmosAccountLocation TestLocation = new("sub-123", "rg-cosmos", "myaccount");
    private const string TestWorkspace = "/subscriptions/sub-456/resourceGroups/rg-mon/providers/Microsoft.OperationalInsights/workspaces/my-ws";

    public AutoDiscoveryServiceTests()
    {
        _mockResourceGraph = new Mock<IResourceGraphDiscoveryService>();
        _mockDiagSettings = new Mock<IDiagnosticSettingsDiscoveryService>();
        _mockLogger = new Mock<ILogger<AutoDiscoveryService>>();
        _service = new AutoDiscoveryService(_mockResourceGraph.Object, _mockDiagSettings.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task DiscoverAsync_FullPipelineSucceeds_ReturnsResult()
    {
        _mockResourceGraph
            .Setup(s => s.FindCosmosAccountAsync("myaccount", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestLocation);
        _mockDiagSettings
            .Setup(s => s.FindLinkedWorkspaceAsync(TestLocation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace);

        var result = await _service.DiscoverAsync(ValidEndpoint);

        Assert.NotNull(result);
        Assert.Equal(TestLocation, result.AccountLocation);
        Assert.Equal(TestWorkspace, result.WorkspaceResourceId);
    }

    [Fact]
    public async Task DiscoverAsync_CachedOnSecondCall_DoesNotCallServicesAgain()
    {
        _mockResourceGraph
            .Setup(s => s.FindCosmosAccountAsync("myaccount", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestLocation);
        _mockDiagSettings
            .Setup(s => s.FindLinkedWorkspaceAsync(TestLocation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspace);

        var result1 = await _service.DiscoverAsync(ValidEndpoint);
        var result2 = await _service.DiscoverAsync(ValidEndpoint);

        Assert.Equal(result1, result2);
        _mockResourceGraph.Verify(
            s => s.FindCosmosAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoverAsync_AccountNotFound_ReturnsNull()
    {
        _mockResourceGraph
            .Setup(s => s.FindCosmosAccountAsync("myaccount", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CosmosAccountLocation?)null);

        var result = await _service.DiscoverAsync(ValidEndpoint);

        Assert.Null(result);
    }

    [Fact]
    public async Task DiscoverAsync_NoWorkspace_ReturnsResultWithNullWorkspace()
    {
        _mockResourceGraph
            .Setup(s => s.FindCosmosAccountAsync("myaccount", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestLocation);
        _mockDiagSettings
            .Setup(s => s.FindLinkedWorkspaceAsync(TestLocation, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await _service.DiscoverAsync(ValidEndpoint);

        Assert.NotNull(result);
        Assert.Null(result.WorkspaceResourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public async Task DiscoverAsync_InvalidEndpoint_ReturnsNull(string? endpoint)
    {
        var result = await _service.DiscoverAsync(endpoint);

        Assert.Null(result);
        _mockResourceGraph.Verify(
            s => s.FindCosmosAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
