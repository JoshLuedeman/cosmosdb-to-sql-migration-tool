using CosmosToSqlAssessment.Services.Discovery;

namespace CosmosToSqlAssessment.Tests.Services.Discovery;

public class CosmosEndpointParserTests
{
    [Theory]
    [InlineData("https://myaccount.documents.azure.com:443/", "myaccount")]
    [InlineData("https://myaccount.documents.azure.com/", "myaccount")]
    [InlineData("https://myaccount.documents.azure.com", "myaccount")]
    [InlineData("https://my-cosmos-db.documents.azure.com:443/", "my-cosmos-db")]
    [InlineData("https://abc.documents.azure.com/", "abc")]
    public void TryParseAccountName_ValidPublicEndpoints_ReturnsTrue(string endpoint, string expected)
    {
        var result = CosmosEndpointParser.TryParseAccountName(endpoint, out var accountName);

        Assert.True(result);
        Assert.Equal(expected, accountName);
    }

    [Theory]
    [InlineData("https://myaccount.privatelink.documents.azure.com:443/", "myaccount")]
    [InlineData("https://my-cosmos-db.privatelink.documents.azure.com/", "my-cosmos-db")]
    public void TryParseAccountName_PrivateLinkEndpoints_ReturnsTrue(string endpoint, string expected)
    {
        var result = CosmosEndpointParser.TryParseAccountName(endpoint, out var accountName);

        Assert.True(result);
        Assert.Equal(expected, accountName);
    }

    [Theory]
    [InlineData("https://myaccount.documents.azure.cn:443/", "myaccount")]
    [InlineData("https://myaccount.documents.azure.us:443/", "myaccount")]
    [InlineData("https://myaccount.privatelink.documents.azure.cn/", "myaccount")]
    [InlineData("https://myaccount.privatelink.documents.azure.us/", "myaccount")]
    public void TryParseAccountName_SovereignCloudEndpoints_ReturnsTrue(string endpoint, string expected)
    {
        var result = CosmosEndpointParser.TryParseAccountName(endpoint, out var accountName);

        Assert.True(result);
        Assert.Equal(expected, accountName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("http://myaccount.documents.azure.com/")] // HTTP not HTTPS
    [InlineData("https://localhost:8081/")] // Emulator
    [InlineData("https://documents.azure.com/")] // No account segment
    [InlineData("https://myaccount.documents.azure.com.evil.com/")] // Suffix spoofing
    [InlineData("https://-bad.documents.azure.com/")] // Leading hyphen
    [InlineData("https://bad-.documents.azure.com/")] // Trailing hyphen
    [InlineData("https://ab.documents.azure.com/")] // Too short (2 chars)
    public void TryParseAccountName_InvalidEndpoints_ReturnsFalse(string? endpoint)
    {
        var result = CosmosEndpointParser.TryParseAccountName(endpoint, out var accountName);

        Assert.False(result);
        Assert.Null(accountName);
    }

    [Theory]
    [InlineData("https://MYACCOUNT.documents.azure.com/", "myaccount")]
    [InlineData("https://MyAccount.Documents.Azure.Com/", "myaccount")]
    public void TryParseAccountName_CaseInsensitiveHost_ReturnsLowercase(string endpoint, string expected)
    {
        var result = CosmosEndpointParser.TryParseAccountName(endpoint, out var accountName);

        Assert.True(result);
        Assert.Equal(expected, accountName);
    }

    [Fact]
    public void GetAccountNameOrDefault_ValidEndpoint_ReturnsAccountName()
    {
        var result = CosmosEndpointParser.GetAccountNameOrDefault("https://myaccount.documents.azure.com:443/");

        Assert.Equal("myaccount", result);
    }

    [Fact]
    public void GetAccountNameOrDefault_InvalidEndpoint_ReturnsFallback()
    {
        var result = CosmosEndpointParser.GetAccountNameOrDefault(null);

        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void GetAccountNameOrDefault_InvalidEndpoint_ReturnsCustomFallback()
    {
        var result = CosmosEndpointParser.GetAccountNameOrDefault("invalid", "N/A");

        Assert.Equal("N/A", result);
    }
}
