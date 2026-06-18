using System.Text.Json;
using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Benchmarks.Fixtures;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Memory profiling benchmarks for streaming document enumeration patterns.
/// Measures allocation per document and GC pressure for the IAsyncEnumerable
/// streaming path vs. the legacy buffered approach.
///
/// Owned by sub-issue #207 (parent #129). These benchmarks simulate processing
/// documents at scale (10K per iteration, extrapolated to 10M) and report:
/// - Bytes allocated per document
/// - Gen0/Gen1/Gen2 GC collections
/// - Peak working set approximation
///
/// Run with: dotnet run -c Release --project tests/Performance/ -- --filter *Streaming*
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class StreamingMemoryProfileBenchmarks
{
    private JsonDocument[] _syntheticDocuments = Array.Empty<JsonDocument>();
    private string[] _serializedDocuments = Array.Empty<string>();

    /// <summary>
    /// Number of documents per benchmark iteration. 10K is sufficient to measure
    /// steady-state allocation rates — extrapolate linearly to 10M.
    /// </summary>
    [Params(1000, 10000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _syntheticDocuments = new JsonDocument[DocumentCount];
        _serializedDocuments = new string[DocumentCount];

        for (int i = 0; i < DocumentCount; i++)
        {
            // Cycle through Small/Medium/Large to simulate real variety
            var size = (JsonDocumentFixtures.DocumentSize)(i % 3);
            _syntheticDocuments[i] = JsonDocumentFixtures.BuildDocument(size);
            _serializedDocuments[i] = _syntheticDocuments[i].RootElement.GetRawText();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var doc in _syntheticDocuments)
        {
            doc?.Dispose();
        }
    }

    /// <summary>
    /// Simulates the streaming pattern: parse → clone → dispose per document.
    /// This is what StreamDocumentsAsync does internally.
    /// </summary>
    [Benchmark(Description = "Streaming: Clone per document")]
    public int StreamingClonePattern()
    {
        int processed = 0;
        for (int i = 0; i < _serializedDocuments.Length; i++)
        {
            using var doc = JsonDocument.Parse(_serializedDocuments[i]);
            var cloned = doc.RootElement.Clone();

            // Simulate schema analysis work on the cloned element
            if (cloned.ValueKind == JsonValueKind.Object)
            {
                processed++;
            }
        }
        return processed;
    }

    /// <summary>
    /// Simulates the legacy buffered pattern: parse all documents into a list
    /// before processing. This retains all JsonDocuments in memory simultaneously.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Buffered: Retain all documents")]
    public int BufferedRetainAllPattern()
    {
        var allDocuments = new List<JsonElement>(DocumentCount);

        // Phase 1: Parse and buffer all
        var parsedDocs = new List<JsonDocument>(DocumentCount);
        for (int i = 0; i < _serializedDocuments.Length; i++)
        {
            var doc = JsonDocument.Parse(_serializedDocuments[i]);
            parsedDocs.Add(doc);
            allDocuments.Add(doc.RootElement);
        }

        // Phase 2: Process
        int processed = 0;
        foreach (var element in allDocuments)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                processed++;
            }
        }

        // Cleanup
        foreach (var doc in parsedDocs)
        {
            doc.Dispose();
        }

        return processed;
    }

    /// <summary>
    /// Measures the per-document cost of ExtractFieldsFlat (the hot path in schema analysis).
    /// </summary>
    [Benchmark(Description = "Streaming: Clone + ExtractFieldsFlat")]
    public int StreamingWithSchemaExtraction()
    {
        int processed = 0;
        for (int i = 0; i < _serializedDocuments.Length; i++)
        {
            using var doc = JsonDocument.Parse(_serializedDocuments[i]);
            var cloned = doc.RootElement.Clone();

            var fields = new Dictionary<string, FieldInfo>();
            CosmosDbAnalysisService.ExtractFieldsFlat(cloned, "", fields);
            processed += fields.Count;
        }
        return processed;
    }

    /// <summary>
    /// Measures the per-document cost of type mapping (MapJsonTypeToSqlTypeEnhanced)
    /// within a streaming iteration.
    /// </summary>
    [Benchmark(Description = "Streaming: Clone + TypeMapping")]
    public int StreamingWithTypeMapping()
    {
        int processed = 0;
        for (int i = 0; i < _serializedDocuments.Length; i++)
        {
            using var doc = JsonDocument.Parse(_serializedDocuments[i]);
            var cloned = doc.RootElement.Clone();

            if (cloned.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cloned.EnumerateObject())
                {
                    _ = CosmosDbAnalysisService.MapJsonTypeToSqlTypeEnhanced(prop.Value);
                    processed++;
                }
            }
        }
        return processed;
    }
}
