using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class AdfNameRegistryTests
{
    [Fact]
    public void Sanitize_ReplacesInvalidCharacters()
    {
        AdfNameRegistry.Sanitize("dbo.Users-Profile (1)").Should().Be("dbo_Users_Profile__1_");
    }

    [Fact]
    public void Sanitize_PrefixesNamesStartingWithDigit()
    {
        AdfNameRegistry.Sanitize("123_Container").Should().StartWith("T_");
    }

    [Fact]
    public void Sanitize_PrefixesEmptyOrWhitespace()
    {
        AdfNameRegistry.Sanitize("").Should().Be("T_");
        AdfNameRegistry.Sanitize("   ").Should().Be("T_");
    }

    [Fact]
    public void Allocate_TruncatesToAdfMaxLength()
    {
        var registry = new AdfNameRegistry();
        var longName = new string('a', 200);

        var name = registry.Allocate(longName, "k1");

        name.Length.Should().BeLessThanOrEqualTo(AdfNameRegistry.MaxNameLength);
    }

    [Fact]
    public void Allocate_DeterministicAcrossInstances_GivenSameInputs()
    {
        var r1 = new AdfNameRegistry();
        var r2 = new AdfNameRegistry();

        r1.Allocate("Cosmos_users", "key1").Should().Be(r2.Allocate("Cosmos_users", "key1"));
    }

    [Fact]
    public void Allocate_AppendsHashSuffix_OnExactCollision()
    {
        var registry = new AdfNameRegistry();

        var first = registry.Allocate("Cosmos_users", "key1");
        var second = registry.Allocate("Cosmos_users", "key2");

        second.Should().NotBe(first);
        second.Should().StartWith("Cosmos_users_");
    }

    [Fact]
    public void Allocate_DetectsCaseInsensitiveCollision()
    {
        var registry = new AdfNameRegistry();

        var first = registry.Allocate("Cosmos_Users", "key1");
        var second = registry.Allocate("cosmos_users", "key2");

        second.Should().NotBe(first, "case-only differences must not produce two ADF resources sharing a name on a case-insensitive filesystem");
    }

    [Fact]
    public void Allocate_HandlesTruncationCollisions()
    {
        var registry = new AdfNameRegistry();
        var prefix = new string('a', AdfNameRegistry.MaxNameLength);

        var first = registry.Allocate(prefix + "_v1", "alpha");
        var second = registry.Allocate(prefix + "_v2", "beta");

        first.Should().NotBe(second);
        first.Length.Should().BeLessThanOrEqualTo(AdfNameRegistry.MaxNameLength);
        second.Length.Should().BeLessThanOrEqualTo(AdfNameRegistry.MaxNameLength);
    }
}
