namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Console-based progress reporter that displays step status with
/// simple text indicators (✓, ✗, ⏳).
/// </summary>
internal sealed class ConsoleProgressReporter : IProgressReporter
{
    private readonly TextWriter _output;
    private DateTime _stepStartTime;

    public ConsoleProgressReporter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void StartStep(string stepName)
    {
        _stepStartTime = DateTime.UtcNow;
        _output.WriteLine($"⏳ {stepName}...");
    }

    public void CompleteStep(string stepName)
    {
        var elapsed = DateTime.UtcNow - _stepStartTime;
        _output.WriteLine($"✅ {stepName} (completed in {FormatElapsed(elapsed)})");
    }

    public void FailStep(string stepName, string errorMessage)
    {
        _output.WriteLine($"❌ {stepName} failed: {errorMessage}");
    }

    public void ReportProgress(string message)
    {
        _output.WriteLine($"   ℹ️ {message}");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{elapsed.TotalMilliseconds:F0}ms";
        if (elapsed.TotalMinutes < 1)
            return $"{elapsed.TotalSeconds:F1}s";
        return $"{elapsed.TotalMinutes:F1}min";
    }
}
