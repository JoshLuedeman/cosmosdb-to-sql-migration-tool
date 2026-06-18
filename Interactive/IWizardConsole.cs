namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Abstraction over console I/O for the interactive wizard.
/// Enables unit testing without a live TTY.
/// </summary>
internal interface IWizardConsole
{
    /// <summary>
    /// Prompts the user for a text value. Returns the user's input or
    /// <paramref name="defaultValue"/> if the user enters nothing.
    /// </summary>
    string Prompt(string message, string? defaultValue = null);

    /// <summary>
    /// Presents a numbered list of choices and returns the selected item.
    /// </summary>
    T Select<T>(string title, IReadOnlyList<T> choices) where T : notnull;

    /// <summary>
    /// Asks a yes/no confirmation question.
    /// </summary>
    bool Confirm(string message, bool defaultValue = true);

    /// <summary>Writes an informational message.</summary>
    void WriteInfo(string message);

    /// <summary>Writes an error message.</summary>
    void WriteError(string message);

    /// <summary>Writes a blank line.</summary>
    void WriteLine();

    /// <summary>
    /// Prompts repeatedly until the validator returns null (valid).
    /// </summary>
    string PromptWithValidation(string message, Func<string, string?> validator, string? defaultValue = null);
}
