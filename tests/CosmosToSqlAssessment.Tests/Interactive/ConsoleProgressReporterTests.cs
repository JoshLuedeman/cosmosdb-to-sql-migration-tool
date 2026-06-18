using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

public class ConsoleProgressReporterTests
{
    [Fact]
    public void StartStep_WritesStepNameWithSpinner()
    {
        var output = new StringWriter();
        var reporter = new ConsoleProgressReporter(output);

        reporter.StartStep("Connecting");

        output.ToString().Should().Contain("⏳ Connecting...");
    }

    [Fact]
    public void CompleteStep_WritesCheckmarkAndElapsed()
    {
        var output = new StringWriter();
        var reporter = new ConsoleProgressReporter(output);

        reporter.StartStep("Analyzing");
        reporter.CompleteStep("Analyzing");

        var text = output.ToString();
        text.Should().Contain("✅ Analyzing");
        text.Should().Contain("completed in");
    }

    [Fact]
    public void FailStep_WritesErrorWithMessage()
    {
        var output = new StringWriter();
        var reporter = new ConsoleProgressReporter(output);

        reporter.StartStep("Connecting");
        reporter.FailStep("Connecting", "Connection refused");

        var text = output.ToString();
        text.Should().Contain("❌ Connecting failed: Connection refused");
    }

    [Fact]
    public void ReportProgress_WritesIntermediateMessage()
    {
        var output = new StringWriter();
        var reporter = new ConsoleProgressReporter(output);

        reporter.ReportProgress("Found 3 databases");

        output.ToString().Should().Contain("Found 3 databases");
    }

    [Fact]
    public void CompleteStep_ShortDuration_ShowsMilliseconds()
    {
        var output = new StringWriter();
        var reporter = new ConsoleProgressReporter(output);

        reporter.StartStep("Quick");
        reporter.CompleteStep("Quick");

        // Should show ms for very fast operations
        output.ToString().Should().Contain("ms");
    }
}
