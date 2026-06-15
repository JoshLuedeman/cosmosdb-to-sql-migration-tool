namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

using CosmosToSqlAssessment.Services.DataFactory;

public class SqlIdentifierEscaperTests
{
    [Theory]
    [InlineData("Users", "[Users]")]
    [InlineData("dbo", "[dbo]")]
    [InlineData("with space", "[with space]")]
    [InlineData("with.dot", "[with.dot]")]
    public void Bracket_PlainIdentifier_Wraps(string input, string expected)
    {
        SqlIdentifierEscaper.Bracket(input).Should().Be(expected);
    }

    [Fact]
    public void Bracket_EscapesClosingBracket()
    {
        // ] must be doubled to ]]
        SqlIdentifierEscaper.Bracket("Users]").Should().Be("[Users]]]");
        SqlIdentifierEscaper.Bracket("a]b]c").Should().Be("[a]]b]]c]");
    }

    [Fact]
    public void Bracket_DoesNotEscapeOpeningBracketOrOtherChars()
    {
        SqlIdentifierEscaper.Bracket("a[b").Should().Be("[a[b]");
        SqlIdentifierEscaper.Bracket("a'b").Should().Be("[a'b]");
        SqlIdentifierEscaper.Bracket("a\"b").Should().Be("[a\"b]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Bracket_NullOrWhitespace_Throws(string? input)
    {
        var act = () => SqlIdentifierEscaper.Bracket(input!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TwoPart_BracketsEachPartIndependently()
    {
        SqlIdentifierEscaper.TwoPart("dbo", "Users").Should().Be("[dbo].[Users]");
        SqlIdentifierEscaper.TwoPart("my schema", "with]bracket").Should().Be("[my schema].[with]]bracket]");
    }
}
