using Azure.Core;
using CosmosToSqlAssessment.Services.Discovery;
using Microsoft.Extensions.Logging;
using Moq;

namespace CosmosToSqlAssessment.Tests.Services.Discovery;

public class DiagnosticSettingsDiscoveryServiceTests
{
    private readonly Mock<IDiagnosticSettingsClient> _mockClient;
    private readonly Mock<ILogger<DiagnosticSettingsDiscoveryService>> _mockLogger;
    private readonly DiagnosticSettingsDiscoveryService _service;

    private static readonly CosmosAccountLocation TestLocation = new("sub-123", "rg-cosmos", "myaccount");

    public DiagnosticSettingsDiscoveryServiceTests()
    {
        _mockClient = new Mock<IDiagnosticSettingsClient>();
        _mockLogger = new Mock<ILogger<DiagnosticSettingsDiscoveryService>>();
        _service = new DiagnosticSettingsDiscoveryService(_mockClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_WorkspaceFound_ReturnsResourceId()
    {
        var workspaceResourceId = "/subscriptions/sub-456/resourceGroups/rg-monitor/providers/Microsoft.OperationalInsights/workspaces/my-workspace";
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiagnosticSettingInfo>
            {
                new("diag-setting-1", workspaceResourceId)
            });

        var result = await _service.FindLinkedWorkspaceAsync(TestLocation);

        Assert.Equal(workspaceResourceId, result);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_NoDiagnosticSettings_ReturnsNull()
    {
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiagnosticSettingInfo>());

        var result = await _service.FindLinkedWorkspaceAsync(TestLocation);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_SettingsWithoutWorkspace_ReturnsNull()
    {
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiagnosticSettingInfo>
            {
                new("diag-setting-1", null),
                new("diag-setting-2", "")
            });

        var result = await _service.FindLinkedWorkspaceAsync(TestLocation);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_MultipleSettings_ReturnsFirstWithWorkspace()
    {
        var workspace1 = "/subscriptions/sub-456/resourceGroups/rg-1/providers/Microsoft.OperationalInsights/workspaces/ws-1";
        var workspace2 = "/subscriptions/sub-456/resourceGroups/rg-2/providers/Microsoft.OperationalInsights/workspaces/ws-2";
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiagnosticSettingInfo>
            {
                new("diag-no-ws", null),
                new("diag-ws-1", workspace1),
                new("diag-ws-2", workspace2)
            });

        var result = await _service.FindLinkedWorkspaceAsync(TestLocation);

        Assert.Equal(workspace1, result);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_ClientThrows_ReturnsNull()
    {
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Access denied"));

        var result = await _service.FindLinkedWorkspaceAsync(TestLocation);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindLinkedWorkspaceAsync_CancellationRequested_Throws()
    {
        _mockClient
            .Setup(c => c.GetDiagnosticSettingsAsync(It.IsAny<ResourceIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.FindLinkedWorkspaceAsync(TestLocation));
    }

    [Fact]
    public void ExtractWorkspaceId_ValidResourceId_ReturnsFullId()
    {
        var resourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/my-ws";

        var result = DiagnosticSettingsDiscoveryService.ExtractWorkspaceId(resourceId);

        Assert.Equal(resourceId, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/invalid/path")]
    public void ExtractWorkspaceId_InvalidResourceId_ReturnsNull(string? resourceId)
    {
        var result = DiagnosticSettingsDiscoveryService.ExtractWorkspaceId(resourceId!);

        Assert.Null(result);
    }
}
