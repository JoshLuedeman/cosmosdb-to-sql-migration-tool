using System.Text;
using System.Text.Json;

namespace CosmosToSqlAssessment.Benchmarks.Fixtures;

/// <summary>
/// Deterministic synthetic <see cref="JsonDocument"/> generator used by the Cosmos analysis
/// benchmarks. Document sizes are calibrated to be representative of real Cosmos workloads
/// without ballooning benchmark run time. Generators are deterministic (seeded loops, no
/// <see cref="Random"/>) so benchmark results are reproducible across runs.
/// </summary>
public static class JsonDocumentFixtures
{
    public enum DocumentSize
    {
        Small,
        Medium,
        Large
    }

    /// <summary>
    /// Returns a parsed <see cref="JsonDocument"/>. Callers own the document and must dispose it.
    /// </summary>
    public static JsonDocument BuildDocument(DocumentSize size)
    {
        var json = size switch
        {
            DocumentSize.Small => BuildSmallJson(),
            DocumentSize.Medium => BuildMediumJson(),
            DocumentSize.Large => BuildLargeJson(),
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };

        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Builds a parsed JSON array of mixed primitive values used by the type-mapping benchmark.
    /// Caller owns the document.
    /// </summary>
    public static JsonDocument BuildPrimitiveBank()
    {
        return JsonDocument.Parse(
            """
            [
              "short string",
              "this is a moderately longer string used to push the AnalyzeStringType length switch",
              "8f14e45f-ceea-467a-9575-d5b3c4ed83f3",
              "2024-09-15T08:30:00Z",
              42,
              2147483648,
              3.14159,
              123.4567,
              true,
              false,
              null
            ]
            """);
    }

    /// <summary>
    /// Returns a tag-style array (string primitives, names matching the
    /// IsLikelyTagsOrCategories heuristic) used by the array-structure benchmark.
    /// </summary>
    public static JsonDocument BuildTagArray()
    {
        return JsonDocument.Parse(
            """
            ["billing","marketing","ops","support","engineering","legal","hr","finance"]
            """);
    }

    /// <summary>
    /// Returns an object array used by the array-structure benchmark to exercise the
    /// complex-structure branch (each item is an object → ShouldCreateTable = true).
    /// </summary>
    public static JsonDocument BuildObjectArray()
    {
        return JsonDocument.Parse(
            """
            [
              {"id": 1, "label": "alpha", "weight": 1.1},
              {"id": 2, "label": "beta",  "weight": 2.2},
              {"id": 3, "label": "gamma", "weight": 3.3},
              {"id": 4, "label": "delta", "weight": 4.4},
              {"id": 5, "label": "epsilon","weight": 5.5}
            ]
            """);
    }

    /// <summary>
    /// Representative detected-type lists matching what AnalyzeStringType /
    /// AnalyzeNumberType actually emit in production. Used by the GetRecommendedSqlType
    /// benchmark.
    /// </summary>
    public static List<string>[] BuildDetectedTypeSamples()
    {
        return new[]
        {
            new List<string> { "NVARCHAR(50)" },
            new List<string> { "NVARCHAR(50)", "NVARCHAR(255)" },
            new List<string> { "TINYINT", "SMALLINT", "INT" },
            new List<string> { "DECIMAL(18,2)", "DECIMAL(18,4)" },
            new List<string> { "DATETIME2" },
            new List<string> { "UNIQUEIDENTIFIER" },
            new List<string> { "BIT" },
            new List<string> { "NVARCHAR(MAX)" }
        };
    }

    private static string BuildSmallJson()
    {
        // 5 simple top-level fields; no nesting; no arrays.
        return """
            {
              "id": "doc-1",
              "name": "Acme widget",
              "createdAt": "2024-09-15T08:30:00Z",
              "quantity": 17,
              "active": true
            }
            """;
    }

    private static string BuildMediumJson()
    {
        // ~22 fields, 2 levels of nesting, one small array.
        return """
            {
              "id": "doc-medium-2",
              "tenantId": "8f14e45f-ceea-467a-9575-d5b3c4ed83f3",
              "name": "Wholesale order",
              "description": "Medium fixture exercising the recursive object traversal path.",
              "createdAt": "2024-09-15T08:30:00Z",
              "updatedAt": "2024-09-15T12:01:00Z",
              "status": "shipped",
              "total": 1247.95,
              "currency": "USD",
              "lineCount": 14,
              "isPriority": false,
              "customer": {
                "customerId": "C-9281",
                "displayName": "Globex Corporation",
                "tier": "Gold",
                "address": {
                  "street": "742 Evergreen Terrace",
                  "city": "Springfield",
                  "state": "IL",
                  "postalCode": "62704",
                  "country": "USA"
                }
              },
              "shipping": {
                "carrier": "UPS",
                "service": "Ground",
                "trackingCode": "1Z999AA10123456784"
              },
              "tags": ["billing","priority","international","oversized"]
            }
            """;
    }

    private static string BuildLargeJson()
    {
        // ~100 fields with deep nesting and several arrays. Built programmatically so it stays
        // representative without bloating the test source. Deterministic — no Random.
        var builder = new StringBuilder(8192);
        builder.Append('{');
        for (var i = 0; i < 40; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append('"').Append("field").Append(i).Append("\":");
            switch (i % 6)
            {
                case 0:
                    builder.Append('"').Append("string-value-").Append(i).Append('"');
                    break;
                case 1:
                    builder.Append(i * 17);
                    break;
                case 2:
                    builder.Append((i * 0.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 3:
                    builder.Append(i % 2 == 0 ? "true" : "false");
                    break;
                case 4:
                    builder.Append('"').Append("2024-09-15T08:30:0").Append(i % 10).Append("Z\"");
                    break;
                case 5:
                    builder.Append('"').Append("8f14e45f-ceea-467a-9575-d5b3c4ed83f").Append(i % 10).Append('"');
                    break;
            }
        }

        // Nested object 5 levels deep.
        builder.Append(",\"nested\":");
        for (var depth = 0; depth < 5; depth++)
        {
            builder.Append("{\"level").Append(depth).Append("\":");
        }
        builder.Append("\"leaf-value\"");
        for (var depth = 0; depth < 5; depth++)
        {
            builder.Append('}');
        }

        // Object array (10 items).
        builder.Append(",\"items\":[");
        for (var i = 0; i < 10; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append("{\"sku\":\"SKU-").Append(i).Append("\",\"qty\":").Append(i + 1)
                .Append(",\"unitPrice\":")
                .Append((9.99 + i).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                .Append('}');
        }
        builder.Append(']');

        // Tag array.
        builder.Append(",\"categories\":[\"alpha\",\"beta\",\"gamma\",\"delta\",\"epsilon\"]");

        builder.Append('}');
        return builder.ToString();
    }
}
