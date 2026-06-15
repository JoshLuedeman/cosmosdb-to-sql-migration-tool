using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace CosmosToSqlAssessment.Benchmarks;

/// <summary>
/// Entry point for the BenchmarkDotNet harness.
/// Uses <see cref="BenchmarkSwitcher"/> so CLI args (e.g. <c>--filter</c>, <c>--list</c>,
/// <c>--job dry</c>) drive which benchmarks execute. See the project README for examples.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, BuildConfig());
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
    /// Result exporters and baseline tracking are intentionally not configured here — those
    /// land in sub-issue #177.
    /// </para>
    /// </summary>
    private static IConfig BuildConfig() =>
        DefaultConfig.Instance.AddJob(
            Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
}

