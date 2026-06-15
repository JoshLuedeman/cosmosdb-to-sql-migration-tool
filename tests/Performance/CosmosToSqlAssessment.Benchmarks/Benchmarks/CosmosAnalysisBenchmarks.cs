using System.Text.Json;
using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Benchmarks.Fixtures;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Microbenchmarks for the pure CPU-bound hot paths of
/// <see cref="CosmosDbAnalysisService"/> — per-document JSON schema inference, SQL type
/// mapping, and array-shape classification. These run against synthetic in-memory fixtures so
/// no live Cosmos endpoint is required. Owned by parent #79 (performance benchmarking
/// framework) / sub-issue #174.
/// </summary>
[MemoryDiagnoser]
public class CosmosAnalysisBenchmarks
{
    // Owning JsonDocuments — held in fields so JsonElement views remain valid for the lifetime
    // of the benchmark run. Disposed in [GlobalCleanup].
    private JsonDocument? _smallDoc;
    private JsonDocument? _mediumDoc;
    private JsonDocument? _largeDoc;
    private JsonDocument? _primitiveBank;
    private JsonDocument? _tagArray;
    private JsonDocument? _objectArray;

    private JsonElement[] _primitives = Array.Empty<JsonElement>();
    private JsonElement _documentRoot;
    private JsonElement _tagArrayRoot;
    private JsonElement _objectArrayRoot;
    private List<string>[] _detectedTypeSamples = Array.Empty<List<string>>();

    // Per-call cost reporting: BDN divides measured iteration time by this count, so the
    // reported Mean is "per primitive mapping" rather than "per loop over the bank".
    private const int PrimitivesPerInvocation = 11;

    [Params(
        JsonDocumentFixtures.DocumentSize.Small,
        JsonDocumentFixtures.DocumentSize.Medium,
        JsonDocumentFixtures.DocumentSize.Large)]
    public JsonDocumentFixtures.DocumentSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _smallDoc = JsonDocumentFixtures.BuildDocument(JsonDocumentFixtures.DocumentSize.Small);
        _mediumDoc = JsonDocumentFixtures.BuildDocument(JsonDocumentFixtures.DocumentSize.Medium);
        _largeDoc = JsonDocumentFixtures.BuildDocument(JsonDocumentFixtures.DocumentSize.Large);
        _primitiveBank = JsonDocumentFixtures.BuildPrimitiveBank();
        _tagArray = JsonDocumentFixtures.BuildTagArray();
        _objectArray = JsonDocumentFixtures.BuildObjectArray();

        _primitives = _primitiveBank.RootElement.EnumerateArray().ToArray();
        _tagArrayRoot = _tagArray.RootElement;
        _objectArrayRoot = _objectArray.RootElement;
        _detectedTypeSamples = JsonDocumentFixtures.BuildDetectedTypeSamples();

        _documentRoot = Size switch
        {
            JsonDocumentFixtures.DocumentSize.Small => _smallDoc.RootElement,
            JsonDocumentFixtures.DocumentSize.Medium => _mediumDoc.RootElement,
            JsonDocumentFixtures.DocumentSize.Large => _largeDoc.RootElement,
            _ => _smallDoc.RootElement
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _smallDoc?.Dispose();
        _mediumDoc?.Dispose();
        _largeDoc?.Dispose();
        _primitiveBank?.Dispose();
        _tagArray?.Dispose();
        _objectArray?.Dispose();
    }

    /// <summary>
    /// Measures the per-primitive cost of <see cref="CosmosDbAnalysisService.MapJsonTypeToSqlTypeEnhanced"/>.
    /// Loops over the primitive bank inside the benchmark and uses
    /// <see cref="BenchmarkAttribute.OperationsPerInvoke"/> so BDN reports cost per call.
    /// </summary>
    [Benchmark(OperationsPerInvoke = PrimitivesPerInvocation)]
    public int MapJsonTypeToSqlTypeEnhanced_Primitives()
    {
        var digest = 0;
        for (var i = 0; i < _primitives.Length; i++)
        {
            digest += CosmosDbAnalysisService.MapJsonTypeToSqlTypeEnhanced(_primitives[i]).Length;
        }

        return digest;
    }

    /// <summary>
    /// Measures the recursive per-document field-extraction hot path. Allocates a fresh
    /// dictionary per invocation so allocations attributable to the traversal are correctly
    /// counted by MemoryDiagnoser.
    /// </summary>
    [Benchmark]
    public int ExtractFieldsFlat_Document()
    {
        var fields = new Dictionary<string, FieldInfo>(capacity: 32);
        CosmosDbAnalysisService.ExtractFieldsFlat(_documentRoot, string.Empty, fields);

        // Digest that depends on both field count AND inferred types — catches accidental
        // type-inference regressions, not just field-count drift.
        var digest = fields.Count;
        foreach (var field in fields.Values)
        {
            digest += field.DetectedTypes.Count;
        }

        return digest;
    }

    /// <summary>
    /// Exercises the delimited-string branch of <see cref="CosmosDbAnalysisService.AnalyzeArrayStructure"/>
    /// (string-only array whose name matches the tags/categories heuristic).
    /// </summary>
    [Benchmark]
    public int AnalyzeArrayStructure_Tags()
    {
        var analysis = CosmosDbAnalysisService.AnalyzeArrayStructure(_tagArrayRoot, "categories");
        return analysis.RecommendedStorage.Length + analysis.ItemCount;
    }

    /// <summary>
    /// Exercises the complex-structure branch (object array → ShouldCreateTable = true).
    /// </summary>
    [Benchmark]
    public int AnalyzeArrayStructure_Objects()
    {
        var analysis = CosmosDbAnalysisService.AnalyzeArrayStructure(_objectArrayRoot, "items");
        return analysis.RecommendedStorage.Length + analysis.ItemCount;
    }

    /// <summary>
    /// Exercises the priority-lookup logic in
    /// <see cref="CosmosDbAnalysisService.GetRecommendedSqlType"/> across the representative
    /// detected-type lists that the production code actually produces.
    /// </summary>
    [Benchmark]
    public int GetRecommendedSqlType_Mixed()
    {
        var digest = 0;
        for (var i = 0; i < _detectedTypeSamples.Length; i++)
        {
            digest += CosmosDbAnalysisService.GetRecommendedSqlType(_detectedTypeSamples[i]).Length;
        }

        return digest;
    }
}
