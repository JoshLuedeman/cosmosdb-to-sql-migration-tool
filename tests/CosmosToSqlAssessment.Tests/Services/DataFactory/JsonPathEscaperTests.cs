using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class JsonPathEscaperTests
{
    [Fact]
    public void ToJsonPath_SimpleField_ReturnsBracketNotation()
    {
        JsonPathEscaper.ToJsonPath("email").Should().Be("$['email']");
    }

    [Fact]
    public void ToJsonPath_DottedField_EmitsNestedBrackets()
    {
        JsonPathEscaper.ToJsonPath("address.city").Should().Be("$['address']['city']");
    }

    [Fact]
    public void ToJsonPath_StripsLeadingDollar()
    {
        JsonPathEscaper.ToJsonPath("$.user.id").Should().Be("$['user']['id']");
    }

    [Fact]
    public void ToJsonPath_StripsLeadingSlash()
    {
        JsonPathEscaper.ToJsonPath("/user/id").Should().Be("$['user']['id']");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$")]
    public void ToJsonPath_EmptyInputs_ReturnRoot(string input)
    {
        JsonPathEscaper.ToJsonPath(input).Should().Be("$");
    }

    [Fact]
    public void ToJsonPath_EscapesSingleQuotesInSegment()
    {
        JsonPathEscaper.ToJsonPath("o'brien").Should().Be(@"$['o\'brien']");
    }
}
