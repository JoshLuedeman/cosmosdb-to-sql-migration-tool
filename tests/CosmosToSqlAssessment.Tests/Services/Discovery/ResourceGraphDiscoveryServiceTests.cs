using System.Text.Json;
using CosmosToSqlAssessment.Services.Discovery;
using Microsoft.Extensions.Logging;
using Moq;

namespace CosmosToSqlAssessment.Tests.Services.Discovery;

public class ResourceGraphDiscoveryServiceTests
{
    private readonly Mock<IResourceGraphQueryClient> _mockQueryClient;
    private readonly Mock<ILogger<ResourceGraphDiscoveryService>> _mockLogger;
    private readonly ResourceGraphDiscoveryService _service;

    public ResourceGraphDiscoveryServiceTests()
    {
        _mockQueryClient = new Mock<IResourceGraphQueryClient>();
        _mockLogger = new Mock<ILogger<ResourceGraphDiscoveryService>>();
        _service = new ResourceGraphDiscoveryService(_mockQueryClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task FindCosmosAccountAsync_AccountFound_ReturnsLocation()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(
            """[{"subscriptionId":"sub-123","resourceGroup":"rg-cosmos","name":"myaccount"}]""");
        var rows = new List<JsonElement>();
        foreach (var elem in json.EnumerateArray()) rows.Add(elem);

        _mockQueryClient
            .Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

        var result = await _service.FindCosmosAccountAsync("myaccount");

        Assert.NotNull(result);
        Assert.Equal("sub-123", result.SubscriptionId);
        Assert.Equal("rg-cosmos", result.ResourceGroup);
        Assert.Equal("myaccount", result.AccountName);
    }

    [Fact]
    public async Task FindCosmosAccountAsync_AccountNotFound_ReturnsNull()
    {
        _mockQueryClient
            .Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<JsonElement>());

        var result = await _service.FindCosmosAccountAsync("nonexistent-account");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindCosmosAccountAsync_MultipleResults_ReturnsFirstAndLogsWarning()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(
            """[{"subscriptionId":"sub-1","resourceGroup":"rg-1","name":"myaccount"},{"subscriptionId":"sub-2","resourceGroup":"rg-2","name":"myaccount"}]""");
        var rows = new List<JsonElement>();
        foreach (var elem in json.EnumerateArray()) rows.Add(elem);

        _mockQueryClient
            .Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

        var result = await _service.FindCosmosAccountAsync("myaccount");

        Assert.NotNull(result);
        Assert.Equal("sub-1", result.SubscriptionId);
        Assert.Equal("rg-1", result.ResourceGroup);
    }

    [Fact]
    public async Task FindCosmosAccountAsync_QueryThrows_ReturnsNull()
    {
        _mockQueryClient
            .Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        var result = await _service.FindCosmosAccountAsync("myaccount");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindCosmosAccountAsync_CancellationRequested_Throws()
    {
        _mockQueryClient
            .Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.FindCosmosAccountAsync("myaccount"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindCosmosAccountAsync_InvalidAccountName_ReturnsNull(string? accountName)
    {
        var result = await _service.FindCosmosAccountAsync(accountName!);

        Assert.Null(result);
        _mockQueryClient.Verify(
            c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("-invalid")]
    [InlineData("ab")]  // Too short
    [InlineData("invalid-")]
    public async Task FindCosmosAccountAsync_InvalidAccountNameFormat_ReturnsNull(string accountName)
    {
        var result = await _service.FindCosmosAccountAsync(accountName);

        Assert.Null(result);
        _mockQueryClient.Verify(
            c => c.QueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
