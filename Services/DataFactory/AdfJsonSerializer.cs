using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Single source of truth for ADF JSON serialization options. All artifact writers MUST
/// route through this class so output is deterministic, camelCase, indented, and skips
/// nulls — the shape Azure Data Factory imports/deploys cleanly.
/// </summary>
public static class AdfJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
