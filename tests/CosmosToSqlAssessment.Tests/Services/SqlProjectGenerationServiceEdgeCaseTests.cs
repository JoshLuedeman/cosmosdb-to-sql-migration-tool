using System.Xml.Linq;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Edge-case tests for <see cref="SqlProjectGenerationService"/> targeting
/// production branches that the happy-path test suite in
/// <see cref="SqlProjectGenerationServiceTests"/> does not reach:
/// nested-object / array child tables, mixed-type fields rendered as
/// <c>NVARCHAR(MAX)</c>, shared-schema deduplication, every index type,
/// assessment-level foreign keys, non-default schemas, identifier
/// sanitisation, and the orphan-shared-child FK leak.
/// </summary>
public class SqlProjectGenerationServiceEdgeCaseTests : TestBase, IDisposable
{
    private readonly SqlProjectGenerationService _service;
    private readonly string _tempDirectory;

    public SqlProjectGenerationServiceEdgeCaseTests()
    {
        _service = new SqlProjectGenerationService(
            MockConfiguration.Object,
            CreateMockLogger<SqlProjectGenerationService>().Object);
        _tempDirectory = Directory.CreateTempSubdirectory("sqlgen-edge-").FullName;
    }

    private static AssessmentResult BuildBase(string targetDb = "EdgeDb_SQL") =>
        new()
        {
            CosmosAccountName = "test",
            DatabaseName = "EdgeDb",
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        SourceDatabase = "EdgeDb",
                        TargetDatabase = targetDb,
                        ContainerMappings = new List<ContainerMapping>()
                    }
                }
            }
        };

    private async Task<string> RunAndGetProjectDirectoryAsync(AssessmentResult assessment)
    {
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        return Directory.EnumerateDirectories(sqlProjectsDir).Single();
    }

    [Fact]
    public async Task Nested_object_child_table_generates_table_header_with_field_path()
    {
        var assessment = BuildBase();
        var container = new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            FieldMappings =
            {
                new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)", IsNullable = false }
            },
            ChildTableMappings =
            {
                new ChildTableMapping
                {
                    SourceFieldPath = "users.address",
                    ChildTableType = "NestedObject",
                    TargetTable = "UserAddresses",
                    ParentKeyColumn = "UserCosmosId",
                    FieldMappings =
                    {
                        new FieldMapping { TargetColumn = "Id", TargetType = "BIGINT IDENTITY(1,1)", IsNullable = false },
                        new FieldMapping { TargetColumn = "UserCosmosId", TargetType = "NVARCHAR(255)", IsNullable = false },
                        new FieldMapping { TargetColumn = "Street", TargetType = "NVARCHAR(255)" }
                    }
                }
            }
        };
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(container);

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var childFile = Path.Combine(projectDir, "Tables", "UserAddresses.sql");
        var sql = await File.ReadAllTextAsync(childFile);

        sql.Should().Contain("-- Child table for NestedObject: users.address");
        sql.Should().Contain("-- Parent container: users");
        sql.Should().Contain("CREATE TABLE [dbo].[UserAddresses]");
        sql.Should().Contain("[UserCosmosId]");
        sql.Should().Contain("Foreign key to Users");
        sql.Should().Contain("PRIMARY KEY");
    }

    [Fact]
    public async Task Array_child_table_header_distinguishes_from_nested_object()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            ChildTableMappings =
            {
                new ChildTableMapping
                {
                    SourceFieldPath = "users.tags",
                    ChildTableType = "Array",
                    TargetTable = "UserTags",
                    ParentKeyColumn = "UserCosmosId",
                    FieldMappings =
                    {
                        new FieldMapping { TargetColumn = "Id", TargetType = "BIGINT IDENTITY(1,1)", IsNullable = false },
                        new FieldMapping { TargetColumn = "UserCosmosId", TargetType = "NVARCHAR(255)", IsNullable = false },
                        new FieldMapping { TargetColumn = "Tag", TargetType = "NVARCHAR(100)" }
                    }
                }
            }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var sql = await File.ReadAllTextAsync(Path.Combine(projectDir, "Tables", "UserTags.sql"));

        sql.Should().Contain("-- Child table for Array: users.tags");
        sql.Should().Contain("CREATE TABLE [dbo].[UserTags]");
    }

    [Fact]
    public async Task Field_mapping_with_NVARCHAR_MAX_target_type_is_emitted_verbatim()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "events",
            TargetTable = "Events",
            FieldMappings =
            {
                new FieldMapping
                {
                    SourceField = "payload",
                    SourceType = "object|number|string", // mixed Cosmos source
                    TargetColumn = "Payload",
                    TargetType = "NVARCHAR(MAX)",
                    IsNullable = true
                }
            }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var sql = await File.ReadAllTextAsync(Path.Combine(projectDir, "Tables", "Events.sql"));

        sql.Should().Contain("[Payload] NVARCHAR(MAX) NULL");
    }

    [Fact]
    public async Task Shared_schema_dedup_collapses_duplicate_target_table_to_one_file()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            FieldMappings = { new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)" } }
        });

        // Two shared schemas with the same TargetTable trigger both dedup
        // gates: the .sqlproj addedTables HashSet AND the
        // generatedSharedTables HashSet inside GenerateTableScriptsAsync.
        assessment.SqlAssessment.SharedSchemas.Add(new SharedSchema
        {
            SchemaId = "addr-v1",
            SchemaName = "Address",
            TargetTable = "SharedAddress",
            SchemaHash = "h1",
            UsageCount = 2,
            SourceContainers = { "users" },
            SourceFieldPaths = { "users.address" },
            FieldMappings = { new FieldMapping { TargetColumn = "Street", TargetType = "NVARCHAR(255)" } }
        });
        assessment.SqlAssessment.SharedSchemas.Add(new SharedSchema
        {
            SchemaId = "addr-v2",
            SchemaName = "Address",
            TargetTable = "SharedAddress", // same target -> must dedup
            SchemaHash = "h2",
            UsageCount = 1,
            SourceContainers = { "orders" },
            SourceFieldPaths = { "orders.shippingAddress" },
            FieldMappings = { new FieldMapping { TargetColumn = "City", TargetType = "NVARCHAR(100)" } }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var tablesDir = Path.Combine(projectDir, "Tables");

        Directory.EnumerateFiles(tablesDir, "SharedAddress.sql").Should().ContainSingle();

        var sqlproj = XDocument.Load(Directory.EnumerateFiles(projectDir, "*.sqlproj").Single());
        var ns = sqlproj.Root!.Name.Namespace;
        var sharedBuildItems = sqlproj.Descendants(ns + "Build")
            .Where(b => string.Equals((string?)b.Attribute("Include"), @"Tables\SharedAddress.sql", StringComparison.OrdinalIgnoreCase))
            .ToList();
        sharedBuildItems.Should().ContainSingle("the .sqlproj's addedTables HashSet should dedup duplicate shared targets");
    }

    [Fact]
    public async Task Index_recommendations_render_each_index_type_with_correct_keyword()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            FieldMappings = { new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)" } }
        });
        assessment.SqlAssessment.IndexRecommendations.AddRange(new[]
        {
            new IndexRecommendation { TableName = "Users", IndexName = "PK_Users_Id",   IndexType = "CLUSTERED",    Columns = { "Id" },        Priority = 1, Justification = "PK" },
            new IndexRecommendation { TableName = "Users", IndexName = "UX_Users_Email",IndexType = "UNIQUE",       Columns = { "Email" },      Priority = 2, Justification = "Unique email" },
            new IndexRecommendation { TableName = "Users", IndexName = "CS_Users_All",  IndexType = "COLUMNSTORE",  Columns = { "Id", "Name" }, Priority = 3, Justification = "Analytics" },
            new IndexRecommendation { TableName = "Users", IndexName = "IX_Users_Name", IndexType = "WHATEVER",     Columns = { "Name" }, IncludedColumns = { "Email" }, Priority = 4, Justification = "Lookup" }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var indexes = await File.ReadAllTextAsync(Path.Combine(projectDir, "Indexes", "Indexes.sql"));

        indexes.Should().Contain("CREATE CLUSTERED INDEX [PK_Users_Id] ON [Users]");
        indexes.Should().Contain("CREATE UNIQUE NONCLUSTERED INDEX [UX_Users_Email] ON [Users]");
        indexes.Should().Contain("CREATE CLUSTERED COLUMNSTORE INDEX [CS_Users_All] ON [Users]");
        indexes.Should().Contain("CREATE NONCLUSTERED INDEX [IX_Users_Name] ON [Users] ([Name]) INCLUDE ([Email])");
    }

    [Fact]
    public async Task Assessment_level_foreign_key_is_emitted_in_foreign_keys_script()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.AddRange(new[]
        {
            new ContainerMapping { SourceContainer = "users",  TargetTable = "Users",  FieldMappings = { new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)" } } },
            new ContainerMapping { SourceContainer = "orders", TargetTable = "Orders", FieldMappings = { new FieldMapping { TargetColumn = "Total", TargetType = "DECIMAL(18,2)" } } }
        });
        assessment.SqlAssessment.ForeignKeyConstraints.Add(new ForeignKeyConstraint
        {
            ConstraintName = "FK_Orders_Users",
            ChildTable = "Orders",
            ChildColumn = "UserCosmosId",
            ParentTable = "Users",
            ParentColumn = "CosmosId",
            OnDeleteAction = "NO ACTION",
            OnUpdateAction = "CASCADE",
            Justification = "Each order belongs to a user"
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var fk = await File.ReadAllTextAsync(Path.Combine(projectDir, "ForeignKeys", "ForeignKeys.sql"));

        fk.Should().Contain("FK_Orders_Users");
        fk.Should().Contain("ALTER TABLE [Orders]");
        fk.Should().Contain("FOREIGN KEY ([UserCosmosId])");
        fk.Should().Contain("REFERENCES [Users] ([CosmosId])");
        fk.Should().Contain("ON DELETE NO ACTION ON UPDATE CASCADE");
        fk.Should().Contain("Each order belongs to a user");
    }

    [Fact]
    public async Task Sanitize_name_prefixes_leading_digit_and_replaces_invalid_chars()
    {
        var assessment = BuildBase("1-data warehouse.test");
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "x",
            TargetTable = "X",
            FieldMappings = { new FieldMapping { TargetColumn = "Y", TargetType = "INT" } }
        });

        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var dirs = Directory.EnumerateDirectories(sqlProjectsDir).ToList();

        dirs.Should().ContainSingle();
        var folderName = Path.GetFileName(dirs[0]);
        // Spaces, dashes, dots replaced with underscores; leading digit forces DB_ prefix.
        folderName.Should().Be("DB_1_data_warehouse_test.Database");
    }

    [Fact]
    public async Task Child_table_required_transformations_render_as_comments()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            ChildTableMappings =
            {
                new ChildTableMapping
                {
                    SourceFieldPath = "users.tags",
                    ChildTableType = "Array",
                    TargetTable = "UserTags",
                    ParentKeyColumn = "UserCosmosId",
                    FieldMappings =
                    {
                        new FieldMapping { TargetColumn = "Id", TargetType = "BIGINT IDENTITY(1,1)", IsNullable = false },
                        new FieldMapping { TargetColumn = "UserCosmosId", TargetType = "NVARCHAR(255)", IsNullable = false },
                        new FieldMapping { TargetColumn = "Tag", TargetType = "NVARCHAR(100)" }
                    },
                    RequiredTransformations =
                    {
                        "Trim whitespace from tag values",
                        "Lowercase before insert"
                    }
                }
            }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var sql = await File.ReadAllTextAsync(Path.Combine(projectDir, "Tables", "UserTags.sql"));

        sql.Should().Contain("-- Required Transformations:");
        sql.Should().Contain("-- * Trim whitespace from tag values");
        sql.Should().Contain("-- * Lowercase before insert");
    }

    [Fact]
    public async Task Child_with_matching_shared_schema_skips_local_table_and_emits_shared_one()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            FieldMappings = { new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)" } },
            ChildTableMappings =
            {
                new ChildTableMapping
                {
                    SourceFieldPath = "users.address",
                    ChildTableType = "NestedObject",
                    TargetTable = "UserAddresses_Local", // would generate as local if SharedSchemaId were null
                    ParentKeyColumn = "UserCosmosId",
                    SharedSchemaId = "shared-address",
                    FieldMappings =
                    {
                        new FieldMapping { TargetColumn = "UserCosmosId", TargetType = "NVARCHAR(255)" }
                    }
                }
            }
        });
        assessment.SqlAssessment.SharedSchemas.Add(new SharedSchema
        {
            SchemaId = "shared-address",
            SchemaName = "Address",
            TargetTable = "SharedAddress",
            SchemaHash = "h-addr",
            UsageCount = 1,
            SourceContainers = { "users" },
            SourceFieldPaths = { "users.address" },
            FieldMappings = { new FieldMapping { TargetColumn = "Street", TargetType = "NVARCHAR(255)" } }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var tablesDir = Path.Combine(projectDir, "Tables");

        File.Exists(Path.Combine(tablesDir, "UserAddresses_Local.sql")).Should().BeFalse(
            "the local child table should be skipped when SharedSchemaId is set and a SharedSchema entry exists");
        File.Exists(Path.Combine(tablesDir, "SharedAddress.sql")).Should().BeTrue();

        var sqlproj = XDocument.Load(Directory.EnumerateFiles(projectDir, "*.sqlproj").Single());
        var ns = sqlproj.Root!.Name.Namespace;
        var includes = sqlproj.Descendants(ns + "Build")
            .Select(b => (string?)b.Attribute("Include"))
            .ToList();
        includes.Should().Contain(@"Tables\SharedAddress.sql");
        includes.Should().NotContain(@"Tables\UserAddresses_Local.sql");
    }

    [Fact]
    public async Task Shared_child_mapping_currently_leaks_into_foreign_key_script()
    {
        // Documents current behaviour: GenerateForeignKeyScriptsAsync iterates
        // ALL ChildTableMappings regardless of SharedSchemaId, so an FK is
        // emitted even though the local child table was intentionally not
        // generated. If/when production checks SharedSchemaId in the FK loop,
        // this test should flip to assert the FK is NOT emitted.
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "users",
            TargetTable = "Users",
            FieldMappings = { new FieldMapping { TargetColumn = "Name", TargetType = "NVARCHAR(255)" } },
            ChildTableMappings =
            {
                new ChildTableMapping
                {
                    SourceFieldPath = "users.address",
                    ChildTableType = "NestedObject",
                    TargetTable = "UserAddresses_Local",
                    ParentKeyColumn = "UserCosmosId",
                    SharedSchemaId = "shared-address",
                    FieldMappings =
                    {
                        new FieldMapping { TargetColumn = "UserCosmosId", TargetType = "NVARCHAR(255)" }
                    }
                }
            }
        });
        assessment.SqlAssessment.SharedSchemas.Add(new SharedSchema
        {
            SchemaId = "shared-address",
            SchemaName = "Address",
            TargetTable = "SharedAddress",
            SchemaHash = "h-addr",
            UsageCount = 1,
            FieldMappings = { new FieldMapping { TargetColumn = "Street", TargetType = "NVARCHAR(255)" } }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var fkPath = Path.Combine(projectDir, "ForeignKeys", "ForeignKeys.sql");

        // Current behaviour: FK file is created and references the skipped local child table.
        File.Exists(fkPath).Should().BeTrue();
        var fk = await File.ReadAllTextAsync(fkPath);
        fk.Should().Contain("FK_UserAddresses_Local_Users");
    }

    [Fact]
    public async Task Non_default_target_schema_flows_into_create_table_statement()
    {
        var assessment = BuildBase();
        assessment.SqlAssessment.DatabaseMappings[0].ContainerMappings.Add(new ContainerMapping
        {
            SourceContainer = "events",
            TargetSchema = "audit",
            TargetTable = "Events",
            FieldMappings =
            {
                new FieldMapping { TargetColumn = "Payload", TargetType = "NVARCHAR(MAX)" }
            }
        });

        var projectDir = await RunAndGetProjectDirectoryAsync(assessment);
        var sql = await File.ReadAllTextAsync(Path.Combine(projectDir, "Tables", "Events.sql"));

        sql.Should().Contain("CREATE TABLE [audit].[Events]");
        sql.Should().Contain("ALTER TABLE [audit].[Events]");
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
