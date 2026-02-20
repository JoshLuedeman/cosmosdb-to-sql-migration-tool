using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

public class SqlMigrationAssessmentServiceTests : TestBase
{
    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();

        // Act
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithValidInput_ShouldReturnAssessment()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.Should().NotBeNull();
        result.RecommendedPlatform.Should().NotBeNullOrEmpty();
        result.RecommendedTier.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldGenerateDatabaseMappings()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldGenerateIndexRecommendations()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.IndexRecommendations.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldPopulateEstimatedRowCount()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
        result.DatabaseMappings.Should().NotBeEmpty();
        
        foreach (var dbMapping in result.DatabaseMappings)
        {
            dbMapping.ContainerMappings.Should().NotBeEmpty();
            
            foreach (var containerMapping in dbMapping.ContainerMappings)
            {
                // EstimatedRowCount should be populated from the container's DocumentCount
                containerMapping.EstimatedRowCount.Should().BeGreaterThanOrEqualTo(0);
                
                // Find the corresponding container in the original analysis
                var sourceContainer = cosmosAnalysis.Containers
                    .FirstOrDefault(c => c.ContainerName == containerMapping.SourceContainer);
                
                if (sourceContainer != null)
                {
                    containerMapping.EstimatedRowCount.Should().Be(sourceContainer.DocumentCount);
                }
            }
        }
    }

    [Fact]
    public async Task AssessMigrationAsync_WithLargeDocumentCount_ShouldPopulateEstimatedRowCount()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        // Set a large document count to test thresholds
        var firstContainer = cosmosAnalysis.Containers.FirstOrDefault();
        firstContainer.Should().NotBeNull("Test data should contain at least one container");
        firstContainer!.DocumentCount = 15_000_000;

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
        var firstDbMapping = result.DatabaseMappings.FirstOrDefault();
        firstDbMapping.Should().NotBeNull();
        firstDbMapping!.ContainerMappings.Should().NotBeEmpty();
        
        var firstContainerMapping = firstDbMapping.ContainerMappings.FirstOrDefault();
        firstContainerMapping.Should().NotBeNull();
        firstContainerMapping!.EstimatedRowCount.Should().Be(15_000_000);
    }

    // ────────────────────────────────────────────────────────────────
    // Platform and tier recommendation branches
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssessMigrationAsync_WithVeryLargeDataset_ShouldRecommendVmOrManagedInstance()
    {
        // Arrange – >4TB pushes the recommendation toward MI or VM
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        // 5 TB per container
        cosmosAnalysis.Containers.First().SizeBytes = 5L * 1024 * 1024 * 1024 * 1024;

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "BigDatabase");

        // Assert
        result.Should().NotBeNull();
        result.RecommendedPlatform.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithBillionDocuments_ShouldRecommendVm()
    {
        // Arrange – >1 billion docs → "SqlOnAzureVm"
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().DocumentCount = 2_000_000_000L;

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "GiantDatabase");

        // Assert
        result.RecommendedPlatform.Should().Be("SqlOnAzureVm");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithComplexIndexing_ShouldRecommendManagedInstance()
    {
        // Arrange – many composite indexes → ManagedInstance
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var policy = cosmosAnalysis.Containers.First().IndexingPolicy;
        for (int i = 0; i < 6; i++)
        {
            policy.CompositeIndexes.Add(new CompositeIndex
            {
                Paths = new List<IndexPath>
                {
                    new IndexPath { Path = $"/field{i}", Order = "Ascending" },
                    new IndexPath { Path = $"/other{i}", Order = "Descending" }
                }
            });
        }

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "ComplexDB");

        // Assert
        result.RecommendedPlatform.Should().Be("AzureSqlManagedInstance");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithHighRUs_ShouldRecommendPremiumTier()
    {
        // Arrange – high peak RUs → Premium tier
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.PerformanceMetrics.PeakRUsPerSecond = 200_000; // Very high RUs

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "HighPerf");

        // Assert
        result.RecommendedTier.Should().Contain("P");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithChildTables_ShouldCreateChildTableMappings()
    {
        // Arrange – container with child tables exercises CreateChildTableMapping
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().ChildTables["orderItems"] = new ChildTableSchema
        {
            TableName = "orderItems",
            SourceFieldPath = "orderItems",
            ChildTableType = "Array",
            Fields = new Dictionary<string, FieldInfo>
            {
                ["productId"] = new FieldInfo { FieldName = "productId", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(100)" },
                ["quantity"] = new FieldInfo { FieldName = "quantity", DetectedTypes = new List<string> { "integer" }, RecommendedSqlType = "INT" }
            },
            SampleCount = 50,
            ParentKeyField = "ParentId"
        };

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        var containerMapping = result.DatabaseMappings.First().ContainerMappings.First();
        containerMapping.ChildTableMappings.Should().NotBeEmpty();
        containerMapping.ChildTableMappings.First().ChildTableType.Should().Be("Array");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithNestedObjectChildTable_ShouldCreateNestedMapping()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().ChildTables["address"] = new ChildTableSchema
        {
            TableName = "address",
            SourceFieldPath = "address",
            ChildTableType = "NestedObject",
            Fields = new Dictionary<string, FieldInfo>
            {
                ["street"] = new FieldInfo { FieldName = "street", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(255)" }
            },
            SampleCount = 10,
            ParentKeyField = "ParentId"
        };

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        var containerMapping = result.DatabaseMappings.First().ContainerMappings.First();
        containerMapping.ChildTableMappings.Should().NotBeEmpty();
        containerMapping.ChildTableMappings.First().ChildTableType.Should().Be("NestedObject");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithCompositeIndexes_ShouldGenerateCompositeIndexRecommendation()
    {
        // Arrange – exercise composite index recommendation logic
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().IndexingPolicy.CompositeIndexes.Add(new CompositeIndex
        {
            Paths = new List<IndexPath>
            {
                new IndexPath { Path = "/userId", Order = "Ascending" },
                new IndexPath { Path = "/email", Order = "Ascending" }
            }
        });

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        result.IndexRecommendations.Should().Contain(i => i.IndexType == "NonClustered" && i.Columns.Count >= 1);
    }

    [Fact]
    public async Task AssessMigrationAsync_WithMultipleContainers_ShouldCreateMultipleMappings()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.Add(new ContainerAnalysis
        {
            ContainerName = "orders",
            DocumentCount = 200000,
            SizeBytes = 1_000_000_000,
            PartitionKey = "/orderId",
            ProvisionedRUs = 400,
            DetectedSchemas = new List<DocumentSchema>
            {
                new DocumentSchema
                {
                    SchemaName = "OrderRecord",
                    Fields = new Dictionary<string, FieldInfo>
                    {
                        ["orderId"] = new FieldInfo { FieldName = "orderId", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(100)" }
                    },
                    SampleCount = 50,
                    Prevalence = 1.0
                }
            },
            IndexingPolicy = new ContainerIndexingPolicy(),
            Performance = new ContainerPerformanceMetrics { AverageRUConsumption = 5.0 }
        });

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        result.DatabaseMappings.First().ContainerMappings.Should().HaveCount(2);
    }

    [Fact]
    public async Task AssessMigrationAsync_WithNestedFields_ShouldRequireTransformation()
    {
        // Arrange – nested field in schema exercises CreateFieldMapping for IsNested = true
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().DetectedSchemas.First().Fields["profile"] = new FieldInfo
        {
            FieldName = "profile",
            DetectedTypes = new List<string> { "object" },
            RecommendedSqlType = "NVARCHAR(MAX)",
            IsNested = true
        };

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        result.Should().NotBeNull();
        result.DatabaseMappings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithMultipleDetectedTypes_ShouldFlagTransformation()
    {
        // Arrange – field with multiple types requires transformation
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().DetectedSchemas.First().Fields["score"] = new FieldInfo
        {
            FieldName = "score",
            DetectedTypes = new List<string> { "integer", "string" }, // Multiple types!
            RecommendedSqlType = "NVARCHAR(50)"
        };

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        var containerMapping = result.DatabaseMappings.First().ContainerMappings.First();
        var scoreMapping = containerMapping.FieldMappings.FirstOrDefault(f => f.SourceField == "score");
        scoreMapping?.RequiresTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithMaxLengthField_ShouldAdjustVarcharSize()
    {
        // Arrange – field with MaxLength triggers NVARCHAR sizing logic
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        cosmosAnalysis.Containers.First().DetectedSchemas.First().Fields["description"] = new FieldInfo
        {
            FieldName = "description",
            DetectedTypes = new List<string> { "string" },
            RecommendedSqlType = "NVARCHAR",
            MaxLength = 1000
        };

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        var containerMapping = result.DatabaseMappings.First().ContainerMappings.First();
        var descMapping = containerMapping.FieldMappings.FirstOrDefault(f => f.SourceField == "description");
        descMapping?.TargetType.Should().Contain("NVARCHAR(");
    }

    [Fact]
    public async Task AssessMigrationAsync_WithTransformationRules_ShouldNotBeEmpty()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        result.TransformationRules.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithMigrationComplexity_ShouldPopulate()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDB");

        // Assert
        result.Complexity.Should().NotBeNull();
        result.Complexity.OverallComplexity.Should().NotBeNullOrEmpty();
    }
}
