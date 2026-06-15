using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using CosmosToSqlAssessment.Benchmarks.Tracking;

namespace CosmosToSqlAssessment.Benchmarks;

/// <summary>
/// Entry point for the BenchmarkDotNet harness.
/// Uses <see cref="BenchmarkSwitcher"/> so CLI args (e.g. <c>--filter</c>, <c>--list</c>,
/// <c>--job dry</c>) drive which benchmarks execute. See the project README for examples.
/// <para>
/// In addition to running benchmarks, this entry point reserves the first-arg subcommand
/// <c>compare-baseline</c> for the baseline regression tracking tool added in sub-issue #177.
/// </para>
/// </summary>
public static class Program
{
    public const string CompareBaselineSubcommand = "compare-baseline";

    public static int Main(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(args[0], CompareBaselineSubcommand, StringComparison.OrdinalIgnoreCase))
        {
            return BaselineComparer.Run(args[1..]);
        }

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, BuildConfig());
        return 0;
    }

    /// <summary>
    /// Default config for every benchmark in this assembly.
    /// <para>
    /// We pin BenchmarkDotNet to the in-process emit toolchain because the production project
    /// (<c>CosmosToSqlAssessment.csproj</c>) is itself an <c>OutputType=Exe</c> console app.
    /// BenchmarkDotNet's default out-of-process toolchain rebuilds the dependency graph in a
    /// boilerplate project under a generated <c>OutputPath</c>, which fails on Windows because
    /// the referenced Exe's <c>apphost.exe</c> can't be resolved into that nested path.
    /// Running in-process sidesteps the issue, is the right trade-off for our
    /// regression-detection use case (we care about relative deltas, not absolute precision),
    /// and means individual benchmark classes need no toolchain-specific attributes.
    /// </para>
    /// <para>
    /// <see cref="JsonExporter.Full"/> is added so every run emits
    /// <c>BenchmarkDotNet.Artifacts/results/{class}-report-full.json</c>, which the
    /// <c>compare-baseline</c> subcommand and CI workflow (#178) consume for regression checks.
    /// </para>
    /// </summary>
    private static IConfig BuildConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
            .AddExporter(JsonExporter.Full);
}

