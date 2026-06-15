using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Harness smoke benchmark. Does not exercise any production hot path — its sole purpose is to
/// prove that BenchmarkDotNet can discover and execute benchmarks and that the project
/// reference to <c>CosmosToSqlAssessment</c> loads at runtime. Real service benchmarks live in
/// sibling files added by the follow-up sub-issues (#174 Cosmos analysis, #175 SQL assessment,
/// #176 report generation).
/// </summary>
[MemoryDiagnoser]
public class SmokeBenchmarks
{
    private const int RecommendationCount = 16;

    [Benchmark]
    public int BuildAssessmentResult()
    {
        var result = new AssessmentResult
        {
            CosmosAccountName = "benchmark-account",
            DatabaseName = "benchmark-db"
        };

        for (var i = 0; i < RecommendationCount; i++)
        {
            result.Recommendations.Add(new RecommendationItem
            {
                Title = $"Recommendation {i}",
                Description = "Smoke-test recommendation used only to exercise the model graph.",
                Priority = "Low",
                Category = "Smoke"
            });
        }

        return result.Recommendations.Count;
    }
}
