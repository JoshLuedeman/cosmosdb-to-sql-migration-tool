using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

public class InputValidatorsTests
{
    // ---- ValidateEndpoint ---------------------------------------------------

    [Theory]
    [InlineData("https://myaccount.documents.azure.com:443/")]
    [InlineData("https://localhost:8081/")]
    [InlineData("http://localhost:8081")]
    [InlineData("https://myaccount.cosmos.azure.com:443/")]
    public void ValidateEndpoint_ValidUrls_ReturnsNull(string input)
    {
        InputValidators.ValidateEndpoint(input).Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    [InlineData("not-a-url", "Invalid URL format")]
    [InlineData("ftp://myaccount.documents.azure.com", "must use HTTPS")]
    public void ValidateEndpoint_InvalidUrls_ReturnsErrorMessage(string input, string expectedFragment)
    {
        var result = InputValidators.ValidateEndpoint(input);
        result.Should().NotBeNull();
        result.Should().Contain(expectedFragment);
    }

    // ---- ValidateDatabaseName -----------------------------------------------

    [Theory]
    [InlineData("MyDatabase")]
    [InlineData("db-1")]
    [InlineData("a")]
    public void ValidateDatabaseName_ValidNames_ReturnsNull(string input)
    {
        InputValidators.ValidateDatabaseName(input).Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    public void ValidateDatabaseName_InvalidNames_ReturnsError(string input, string expectedFragment)
    {
        var result = InputValidators.ValidateDatabaseName(input);
        result.Should().NotBeNull();
        result.Should().Contain(expectedFragment);
    }

    [Fact]
    public void ValidateDatabaseName_TooLong_ReturnsError()
    {
        var longName = new string('x', 256);
        var result = InputValidators.ValidateDatabaseName(longName);
        result.Should().Contain("too long");
    }

    // ---- ValidateWorkspaceId ------------------------------------------------

    [Theory]
    [InlineData("12345678-1234-1234-1234-123456789012")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ValidateWorkspaceId_ValidGuids_ReturnsNull(string input)
    {
        InputValidators.ValidateWorkspaceId(input).Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("not-a-guid", "must be a valid GUID")]
    [InlineData("1234", "must be a valid GUID")]
    public void ValidateWorkspaceId_InvalidGuids_ReturnsError(string input, string expectedFragment)
    {
        var result = InputValidators.ValidateWorkspaceId(input);
        result.Should().NotBeNull();
        result.Should().Contain(expectedFragment);
    }

    // ---- ValidateOutputDirectory --------------------------------------------

    [Theory]
    [InlineData("./output")]
    [InlineData("C:\\Reports")]
    [InlineData("relative/path")]
    [InlineData("")] // empty is okay (uses default)
    public void ValidateOutputDirectory_ValidPaths_ReturnsNull(string input)
    {
        InputValidators.ValidateOutputDirectory(input).Should().BeNull();
    }

    [Fact]
    public void ValidateOutputDirectory_InvalidChars_ReturnsError()
    {
        // Use a character that's invalid in paths on all platforms
        var invalidPath = "output\0path";
        var result = InputValidators.ValidateOutputDirectory(invalidPath);
        result.Should().Contain("invalid characters");
    }
}
