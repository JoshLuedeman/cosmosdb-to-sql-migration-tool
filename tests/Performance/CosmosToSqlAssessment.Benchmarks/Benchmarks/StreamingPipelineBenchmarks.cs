using System.Text.Json;
using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Benchmarks.Fixtures;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Streaming pipeline benchmarks that measure end-to-end throughput of the
/// IAsyncEnumerable document streaming path at various page sizes.
///
/// Owned by sub-issue #208 (parent #129). Builds on the #79 BenchmarkDotNet framework.
///
/// Run with: dotnet run -c Release --project tests/Performance/ -- --filter *Streaming*
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
public class StreamingPipelineBenchmarks
{
    private string[][] _pagedDocuments = Array.Empty<string[]>();
    private string[] _allDocuments = Array.Empty<string>();

    private const int TotalDocuments = 5000;

    /// <summary>
    /// Simulates different MaxItemCount (page size) configurations.
    /// </summary>
    [Params(25, 50, 100, 200)]
    public int PageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Generate synthetic documents
        _allDocuments = new string[TotalDocuments];
        for (int i = 0; i < TotalDocuments; i++)
        {
            var size = (JsonDocumentFixtures.DocumentSize)(i % 3);
            using var doc = JsonDocumentFixtures.BuildDocument(size);
            _allDocuments[i] = doc.RootElement.GetRawText();
        }

        // Pre-partition into pages based on PageSize
        var pages = new List<string[]>();
        for (int offset = 0; offset < TotalDocuments; offset += PageSize)
        {
            int count = Math.Min(PageSize, TotalDocuments - offset);
            pages.Add(_allDocuments[offset..(offset + count)]);
        }
        _pagedDocuments = pages.ToArray();
    }

    /// <summary>
    /// Simulates streaming with Clone-per-document at the configured page size.
    /// This is the core pattern used by StreamDocumentsAsync.
    /// </summary>
    [Benchmark(Description = "StreamDocs: PageSize iteration")]
    public int StreamDocuments_PageSizeIteration()
    {
        int processed = 0;
        foreach (var page in _pagedDocuments)
        {
            foreach (var docString in page)
            {
                using var doc = JsonDocument.Parse(docString);
                var cloned = doc.RootElement.Clone();

                if (cloned.ValueKind == JsonValueKind.Object)
                    processed++;
            }
        }
        return processed;
    }

    /// <summary>
    /// Full streaming pipeline: parse → clone → schema extraction per document.
    /// Measures the realistic cost of AnalyzeDocumentSchemasAsync's hot loop.
    /// </summary>
    [Benchmark(Description = "StreamDocs: Full schema analysis pipeline")]
    public int StreamDocuments_WithSchemaAnalysis()
    {
        int processed = 0;
        var schemas = new Dictionary<string, DocumentSchema>();

        foreach (var page in _pagedDocuments)
        {
            foreach (var docString in page)
            {
                using var doc = JsonDocument.Parse(docString);
                var cloned = doc.RootElement.Clone();

                // Simulate the schema extraction hot path
                var fields = new Dictionary<string, FieldInfo>();
                CosmosDbAnalysisService.ExtractFieldsFlat(cloned, "", fields);

                // Build schema signature (same logic as AnalyzeDocumentStructure)
                var signature = string.Join("|", fields.Select(f =>
                    $"{f.Key}:{string.Join(",", f.Value.DetectedTypes)}"));

                if (!schemas.ContainsKey(signature))
                {
                    schemas[signature] = new DocumentSchema
                    {
                        SchemaName = $"Schema_{schemas.Count + 1}",
                        Fields = new Dictionary<string, FieldInfo>(fields),
                        SampleCount = 0
                    };
                }
                schemas[signature].SampleCount++;
                processed++;
            }
        }
        return processed;
    }

    /// <summary>
    /// Measures overhead of tracking continuation tokens alongside documents.
    /// Simulates StreamDocumentsWithContinuationAsync pattern.
    /// </summary>
    [Benchmark(Description = "StreamDocs: With continuation token tracking")]
    public int StreamDocuments_ContinuationTokenTracking()
    {
        int processed = 0;
        string? lastContinuation = null;

        for (int pageIdx = 0; pageIdx < _pagedDocuments.Length; pageIdx++)
        {
            var page = _pagedDocuments[pageIdx];
            // Simulate continuation token (opaque string per page)
            var continuation = pageIdx < _pagedDocuments.Length - 1
                ? $"token_{pageIdx}_{page.Length}"
                : null;

            foreach (var docString in page)
            {
                using var doc = JsonDocument.Parse(docString);
                var cloned = doc.RootElement.Clone();

                if (cloned.ValueKind == JsonValueKind.Object)
                    processed++;

                lastContinuation = continuation;
            }
        }

        // Prevent dead code elimination
        return lastContinuation != null ? processed : processed + 1;
    }

    /// <summary>
    /// Measures container enumeration streaming (lightweight — just string extraction).
    /// </summary>
    [Benchmark(Description = "StreamContainers: Name enumeration")]
    public int StreamContainers_Enumeration()
    {
        // Simulate container name enumeration (typically 10-100 containers)
        int count = 0;
        var containerNames = Enumerable.Range(0, PageSize)
            .Select(i => $"container-{i:D4}")
            .ToArray();

        foreach (var name in containerNames)
        {
            if (!string.IsNullOrEmpty(name))
                count++;
        }
        return count;
    }
}
