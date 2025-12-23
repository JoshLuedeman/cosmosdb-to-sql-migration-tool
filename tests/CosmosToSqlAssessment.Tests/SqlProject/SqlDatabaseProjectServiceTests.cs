using CosmosToSqlAssessment.SqlProject;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.SqlProject;

/// <summary>
/// Unit tests for SqlDatabaseProjectService focusing on transformation logic
/// </summary>
public class SqlDatabaseProjectServiceTests : TestBase
{
    private readonly string _testOutputPath;

    public SqlDatabaseProjectServiceTests()
    {
        _testOutputPath = Path.Combine(Path.GetTempPath(), "SqlProjectTests", Guid.NewGuid().ToString());
    }

    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();

        // Act
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateProjectAsync_WithValidAssessment_ShouldCreateProject()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var projectName = "TestMigrationProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            result.Should().NotBeNull();
            result.ProjectName.Should().Be(projectName);
            result.OutputPath.Should().Be(_testOutputPath);
            Directory.Exists(_testOutputPath).Should().BeTrue();
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_ShouldGenerateStoredProcedures()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var projectName = "TestMigrationProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            result.StoredProcedureScripts.Should().NotBeNull();
            result.StoredProcedureScripts.Should().NotBeEmpty();

            // Verify that stored procedure files were created
            var storedProcDir = Path.Combine(_testOutputPath, "StoredProcedures");
            Directory.Exists(storedProcDir).Should().BeTrue();

            var spFiles = Directory.GetFiles(storedProcDir, "*.sql");
            spFiles.Should().NotBeEmpty();
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_WithFlattenTransformation_ShouldGenerateValidTSQL()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add a flatten transformation rule
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Flatten Address Objects",
            SourcePattern = "document.address.*",
            TargetPattern = "address_*",
            TransformationType = "Flatten",
            Logic = "Flatten nested address objects into flat columns",
            AffectedTables = new List<string> { "Customers" }
        });

        var projectName = "TestFlattenProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(s => s.Contains("FlattenAddressObjects"));
            spFile.Should().NotBeNull();
            
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify it's not a TODO anymore
            spContent.Should().NotContain("TODO");
            
            // Verify flatten-specific SQL logic is present
            spContent.Should().Contain("Flatten nested objects");
            spContent.Should().Contain("JSON_VALUE");
            spContent.Should().Contain("OPENJSON");
            spContent.Should().Contain("ProcessedFlag");
            spContent.Should().Contain("@BatchSize");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_WithSplitTransformation_ShouldGenerateValidTSQL()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add a split transformation rule
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Split Order Items Array",
            SourcePattern = "document.items[]",
            TargetPattern = "OrderItems table",
            TransformationType = "Split",
            Logic = "Split order items array into child table",
            AffectedTables = new List<string> { "Orders" }
        });

        var projectName = "TestSplitProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(s => s.Contains("SplitOrderItemsArray"));
            spFile.Should().NotBeNull();
            
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify it's not a TODO anymore
            spContent.Should().NotContain("TODO");
            
            // Verify split-specific SQL logic is present
            spContent.Should().Contain("Split arrays into separate");
            spContent.Should().Contain("OPENJSON");
            spContent.Should().Contain("INSERT INTO");
            spContent.Should().Contain("ParentId");
            spContent.Should().Contain("ArrayIndex");
            spContent.Should().Contain("ISJSON");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_WithCombineTransformation_ShouldGenerateValidTSQL()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add a combine transformation rule
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Combine Name Fields",
            SourcePattern = "firstName + lastName",
            TargetPattern = "fullName",
            TransformationType = "Combine",
            Logic = "Combine first and last name into full name",
            AffectedTables = new List<string> { "Customers" }
        });

        var projectName = "TestCombineProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(s => s.Contains("CombineNameFields"));
            spFile.Should().NotBeNull();
            
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify it's not a TODO anymore
            spContent.Should().NotContain("TODO");
            
            // Verify combine-specific SQL logic is present
            spContent.Should().Contain("Combine multiple fields");
            spContent.Should().Contain("CONCAT");
            spContent.Should().Contain("CONCAT_WS");
            spContent.Should().Contain("CombineProcessedFlag");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_WithTypeConvertTransformation_ShouldGenerateValidTSQL()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add a type convert transformation rule
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Convert Date Strings",
            SourcePattern = "string dateValue",
            TargetPattern = "DATETIME2",
            TransformationType = "TypeConvert",
            Logic = "Convert date strings to DATETIME2",
            AffectedTables = new List<string> { "Events" }
        });

        var projectName = "TestTypeConvertProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(s => s.Contains("ConvertDateStrings"));
            spFile.Should().NotBeNull();
            
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify it's not a TODO anymore
            spContent.Should().NotContain("TODO");
            
            // Verify type conversion-specific SQL logic is present
            spContent.Should().Contain("Convert data types");
            spContent.Should().Contain("TRY_CAST");
            spContent.Should().Contain("TRY_CONVERT");
            spContent.Should().Contain("ConvertProcessedFlag");
            spContent.Should().Contain("ConversionErrors");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_WithCustomTransformation_ShouldGenerateExtensibilityPoints()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add a custom transformation rule
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Custom Business Logic",
            SourcePattern = "custom logic",
            TargetPattern = "enriched data",
            TransformationType = "Custom",
            Logic = "Apply custom business rules",
            AffectedTables = new List<string> { "Products" }
        });

        var projectName = "TestCustomProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(s => s.Contains("CustomBusinessLogic"));
            spFile.Should().NotBeNull();
            
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify it's not a TODO anymore
            spContent.Should().NotContain("TODO");
            
            // Verify custom transformation has extensibility guidance
            spContent.Should().Contain("Custom transformation");
            spContent.Should().Contain("EXTENSIBILITY POINT");
            spContent.Should().Contain("CONFIGURATION GUIDANCE");
            spContent.Should().Contain("CustomProcessedFlag");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_AllTransformations_ShouldHandleEdgeCases()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        // Add transformation rules for all types
        assessment.TransformationRules.AddRange(new[]
        {
            new TransformationRule
            {
                RuleName = "Flatten Nested",
                TransformationType = "Flatten",
                Logic = "Handle null nested objects",
                AffectedTables = new List<string> { "Table1" },
                SourcePattern = "nested.*",
                TargetPattern = "flat_*"
            },
            new TransformationRule
            {
                RuleName = "Split Empty Arrays",
                TransformationType = "Split",
                Logic = "Handle empty arrays",
                AffectedTables = new List<string> { "Table2" },
                SourcePattern = "array[]",
                TargetPattern = "child_table"
            },
            new TransformationRule
            {
                RuleName = "Combine Nulls",
                TransformationType = "Combine",
                Logic = "Handle null values in combine",
                AffectedTables = new List<string> { "Table3" },
                SourcePattern = "field1 + field2",
                TargetPattern = "combined"
            },
            new TransformationRule
            {
                RuleName = "Convert Invalid Types",
                TransformationType = "TypeConvert",
                Logic = "Handle type mismatches",
                AffectedTables = new List<string> { "Table4" },
                SourcePattern = "string",
                TargetPattern = "int"
            }
        });

        var projectName = "TestEdgeCasesProject";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            result.StoredProcedureScripts.Should().HaveCount(assessment.TransformationRules.Count);

            foreach (var spFile in result.StoredProcedureScripts)
            {
                var spContent = await File.ReadAllTextAsync(spFile);
                
                // All procedures should have error handling
                spContent.Should().Contain("BEGIN TRY");
                spContent.Should().Contain("BEGIN CATCH");
                spContent.Should().Contain("ROLLBACK TRANSACTION");
                
                // All procedures should handle batch processing
                spContent.Should().Contain("@BatchSize");
                spContent.Should().Contain("WHILE");
                
                // All procedures should have logging
                spContent.Should().Contain("@LogProgress");
                spContent.Should().Contain("PRINT");
                
                // Should not have TODO comments
                spContent.Should().NotContain("TODO");
            }
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_TransformationRules_ShouldIncludeNullHandling()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Test Null Handling",
            TransformationType = "TypeConvert",
            Logic = "Handle null values properly",
            AffectedTables = new List<string> { "TestTable" },
            SourcePattern = "source",
            TargetPattern = "target"
        });

        var projectName = "TestNullHandling";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(sp => sp.Contains("TestNullHandling"));
            spFile.Should().NotBeNull();
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify null handling patterns
            spContent.Should().Contain("IS NOT NULL");
            spContent.Should().Contain("IS NULL");
            
            // Verify safe conversion functions that handle nulls
            spContent.Should().Contain("TRY_CAST");
            spContent.Should().Contain("TRY_CONVERT");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_TransformationRules_ShouldHaveTransactionManagement()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Transaction Test",
            TransformationType = "Flatten",
            Logic = "Test transaction management",
            AffectedTables = new List<string> { "TestTable" },
            SourcePattern = "test",
            TargetPattern = "test"
        });

        var projectName = "TestTransactions";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(sp => sp.Contains("TransactionTest"));
            spFile.Should().NotBeNull();
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify transaction management
            spContent.Should().Contain("BEGIN TRANSACTION");
            spContent.Should().Contain("COMMIT TRANSACTION");
            spContent.Should().Contain("ROLLBACK TRANSACTION");
            spContent.Should().Contain("@@TRANCOUNT");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }

    [Fact]
    public async Task GenerateProjectAsync_MultipleAffectedTables_ShouldGenerateLogicForEach()
    {
        // Arrange
        var logger = CreateMockLogger<SqlDatabaseProjectService>();
        var service = new SqlDatabaseProjectService(MockConfiguration.Object, logger.Object);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "Multi Table Transform",
            TransformationType = "Flatten",
            Logic = "Apply to multiple tables",
            AffectedTables = new List<string> { "Table1", "Table2", "Table3" },
            SourcePattern = "test",
            TargetPattern = "test"
        });

        var projectName = "TestMultiTable";

        try
        {
            Directory.CreateDirectory(_testOutputPath);

            // Act
            var result = await service.GenerateProjectAsync(assessment, projectName, _testOutputPath);

            // Assert
            var spFile = result.StoredProcedureScripts.FirstOrDefault(sp => sp.Contains("MultiTableTransform"));
            spFile.Should().NotBeNull();
            var spContent = await File.ReadAllTextAsync(spFile!);
            
            // Verify all tables are processed
            spContent.Should().Contain("Table1");
            spContent.Should().Contain("Table2");
            spContent.Should().Contain("Table3");
            
            // Each table should have its own batch processing
            spContent.Should().Contain("@CurrentBatch_Table1");
            spContent.Should().Contain("@CurrentBatch_Table2");
            spContent.Should().Contain("@CurrentBatch_Table3");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(_testOutputPath))
            {
                Directory.Delete(_testOutputPath, true);
            }
        }
    }
}
