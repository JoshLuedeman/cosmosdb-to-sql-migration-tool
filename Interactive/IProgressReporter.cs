namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Abstraction for reporting progress during analysis operations.
/// Enables unit testing without live console output.
/// </summary>
internal interface IProgressReporter
{
    /// <summary>Signals that a named step is starting.</summary>
    void StartStep(string stepName);

    /// <summary>Signals that the current step completed successfully.</summary>
    void CompleteStep(string stepName);

    /// <summary>Signals that the current step failed.</summary>
    void FailStep(string stepName, string errorMessage);

    /// <summary>Reports an intermediate progress message within a step.</summary>
    void ReportProgress(string message);
}
