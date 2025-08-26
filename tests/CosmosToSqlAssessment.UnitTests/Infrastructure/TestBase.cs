using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CosmosToSqlAssessment.UnitTests.Infrastructure
{
    /// <summary>
    /// Base class for unit tests providing common mock setups
    /// </summary>
    public abstract class TestBase
    {
        protected Mock<IConfiguration> MockConfiguration { get; }

        protected TestBase()
        {
            MockConfiguration = CreateMockConfiguration();
        }

        /// <summary>
        /// Creates a mock configuration with common test settings
        /// </summary>
        protected virtual Mock<IConfiguration> CreateMockConfiguration()
        {
            var mockConfig = new Mock<IConfiguration>();
            
            // Setup common configuration values for tests
            mockConfig.Setup(x => x["CosmosDb:ConnectionString"])
                .Returns("AccountEndpoint=https://test.documents.azure.com:443/;AccountKey=testkey;");
            mockConfig.Setup(x => x["CosmosDb:DatabaseName"])
                .Returns("test-database");
            mockConfig.Setup(x => x["Azure:SubscriptionId"])
                .Returns("test-subscription-id");
            mockConfig.Setup(x => x["Azure:ResourceGroupName"])
                .Returns("test-resource-group");

            return mockConfig;
        }

        /// <summary>
        /// Creates a mock logger for the specified type
        /// </summary>
        protected Mock<ILogger<T>> CreateMockLogger<T>()
        {
            return new Mock<ILogger<T>>();
        }
    }
}
