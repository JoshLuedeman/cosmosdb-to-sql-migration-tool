using System.Collections.Generic;
using CosmosToSqlAssessment.Models.DataFactory;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class LinkedServiceBuilderTests
{
    private static LinkedServiceBuilder NewBuilder() => new();

    [Fact]
    public void BuildCosmosLinkedService_DefaultMsi_UsesAccountEndpointAndDatabase_NoConnectionString_NoAccountKey()
    {
        var registry = new AdfNameRegistry();
        var ls = NewBuilder().BuildCosmosLinkedService("MyDb", registry, new DataFactoryGenerationOptions());

        ls.Name.Should().Be("CosmosDb_MyDb_LinkedService");
        ls.Properties.Type.Should().Be("CosmosDb");
        ls.Properties.TypeProperties.Should().ContainKey("accountEndpoint")
            .WhoseValue.Should().Be($"@{{linkedService().{ParameterCatalog.CosmosAccountEndpoint}}}");
        ls.Properties.TypeProperties.Should().ContainKey("database")
            .WhoseValue.Should().Be($"@{{linkedService().{ParameterCatalog.CosmosDatabaseName}}}");
        ls.Properties.TypeProperties.Should().NotContainKey("connectionString");
        ls.Properties.TypeProperties.Should().NotContainKey("accountKey");
    }

    [Fact]
    public void BuildCosmosLinkedService_DeclaresParametersBlock()
    {
        var registry = new AdfNameRegistry();
        var ls = NewBuilder().BuildCosmosLinkedService("MyDb", registry, new DataFactoryGenerationOptions());

        ls.Properties.AdditionalProperties.Should().NotBeNull();
        var parameters = (IDictionary<string, object?>)ls.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(ParameterCatalog.CosmosAccountEndpoint);
        parameters.Should().ContainKey(ParameterCatalog.CosmosDatabaseName);
        // No secret-name param when MI is in use.
        parameters.Should().NotContainKey(ParameterCatalog.CosmosAccountKeySecretName);
    }

    [Fact]
    public void BuildCosmosLinkedService_KeyVaultOptIn_UsesAzureKeyVaultSecretForAccountKey()
    {
        var registry = new AdfNameRegistry();
        var opts = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForCosmos = false,
            UseAzureKeyVault = true,
        };

        var ls = NewBuilder().BuildCosmosLinkedService("MyDb", registry, opts);

        ls.Properties.TypeProperties.Should().ContainKey("connectionString");
        ls.Properties.TypeProperties["connectionString"].Should().BeOfType<string>()
            .Which.Should().NotContain("AccountKey="); // key comes from AKV, not from concat()
        var accountKey = (IDictionary<string, object?>)ls.Properties.TypeProperties["accountKey"]!;
        accountKey["type"].Should().Be("AzureKeyVaultSecret");
        var parameters = (IDictionary<string, object?>)ls.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(ParameterCatalog.CosmosAccountKeySecretName);
    }

    [Fact]
    public void BuildCosmosLinkedService_PlaceholderMode_FallsBackToReplaceMeConnectionString()
    {
        var registry = new AdfNameRegistry();
        var opts = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForCosmos = false,
            UseAzureKeyVault = false,
        };

        var ls = NewBuilder().BuildCosmosLinkedService("MyDb", registry, opts);

        ls.Properties.TypeProperties["connectionString"].Should().BeOfType<string>()
            .Which.Should().Contain("<replace-with-account-key>");
        ls.Properties.TypeProperties.Should().NotContainKey("accountKey");
    }

    [Fact]
    public void BuildAzureSqlLinkedService_DefaultMsi_HasServerDatabaseAuthenticationType_NoConnectionString()
    {
        var registry = new AdfNameRegistry();
        var ls = NewBuilder().BuildAzureSqlLinkedService(registry, new DataFactoryGenerationOptions());

        ls.Name.Should().Be("AzureSqlDatabaseLinkedService");
        ls.Properties.Type.Should().Be("AzureSqlDatabase");
        ls.Properties.TypeProperties.Should().NotContainKey("connectionString");
        ls.Properties.TypeProperties.Should().NotContainKey("password");
        ls.Properties.TypeProperties["authenticationType"].Should().Be("SystemAssignedManagedIdentity");
        ls.Properties.TypeProperties["server"].Should().BeOfType<string>()
            .Which.Should().Contain($"linkedService().{ParameterCatalog.SqlServerName}");
        ls.Properties.TypeProperties["database"].Should().BeOfType<string>()
            .Which.Should().Contain($"linkedService().{ParameterCatalog.SqlDatabaseName}");
        ls.Properties.TypeProperties["encrypt"].Should().Be("mandatory");
        ls.Properties.TypeProperties["trustServerCertificate"].Should().Be(false);
    }

    [Fact]
    public void BuildAzureSqlLinkedService_KeyVaultOptIn_BindsPasswordViaKeyVaultSecret()
    {
        var registry = new AdfNameRegistry();
        var opts = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForSql = false,
            UseAzureKeyVault = true,
        };

        var ls = NewBuilder().BuildAzureSqlLinkedService(registry, opts);

        ls.Properties.TypeProperties.Should().ContainKey("connectionString");
        ls.Properties.TypeProperties.Should().ContainKey("password");
        var password = (IDictionary<string, object?>)ls.Properties.TypeProperties["password"]!;
        password["type"].Should().Be("AzureKeyVaultSecret");
        var parameters = (IDictionary<string, object?>)ls.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(ParameterCatalog.SqlUserName);
        parameters.Should().ContainKey(ParameterCatalog.SqlPasswordSecretName);
    }

    [Fact]
    public void BuildAzureSqlLinkedService_PlaceholderMode_EmitsConnectionStringStub_NoPassword()
    {
        var registry = new AdfNameRegistry();
        var opts = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForSql = false,
            UseAzureKeyVault = false,
        };

        var ls = NewBuilder().BuildAzureSqlLinkedService(registry, opts);

        ls.Properties.TypeProperties.Should().ContainKey("connectionString");
        ls.Properties.TypeProperties.Should().NotContainKey("password");
    }

    [Fact]
    public void BuildKeyVaultLinkedService_HasParameterisedBaseUrl()
    {
        var registry = new AdfNameRegistry();
        var ls = NewBuilder().BuildKeyVaultLinkedService(registry);

        ls.Name.Should().Be("KeyVaultLinkedService");
        ls.Properties.Type.Should().Be("AzureKeyVault");
        ls.Properties.TypeProperties["baseUrl"].Should().BeOfType<string>()
            .Which.Should().Contain($"linkedService().{ParameterCatalog.KeyVaultBaseUrl}");
        var parameters = (IDictionary<string, object?>)ls.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(ParameterCatalog.KeyVaultBaseUrl);
    }
}
