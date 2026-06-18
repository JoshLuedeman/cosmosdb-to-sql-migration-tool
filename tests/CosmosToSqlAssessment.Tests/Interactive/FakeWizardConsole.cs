using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

/// <summary>
/// Fake <see cref="IWizardConsole"/> that replays queued responses for unit testing.
/// </summary>
internal sealed class FakeWizardConsole : IWizardConsole
{
    private readonly Queue<string> _promptResponses = new();
    private readonly Queue<int> _selectResponses = new();
    private readonly Queue<bool> _confirmResponses = new();
    private readonly List<string> _output = new();

    public IReadOnlyList<string> Output => _output;

    public FakeWizardConsole QueuePrompt(string response)
    {
        _promptResponses.Enqueue(response);
        return this;
    }

    public FakeWizardConsole QueueSelect(int zeroBasedIndex)
    {
        _selectResponses.Enqueue(zeroBasedIndex);
        return this;
    }

    public FakeWizardConsole QueueConfirm(bool response)
    {
        _confirmResponses.Enqueue(response);
        return this;
    }

    public string Prompt(string message, string? defaultValue = null)
    {
        _output.Add($"PROMPT: {message}");
        if (_promptResponses.Count == 0)
            return defaultValue ?? string.Empty;
        return _promptResponses.Dequeue();
    }

    public T Select<T>(string title, IReadOnlyList<T> choices) where T : notnull
    {
        _output.Add($"SELECT: {title}");
        if (_selectResponses.Count == 0)
            return choices[0];
        var index = _selectResponses.Dequeue();
        return choices[index];
    }

    public bool Confirm(string message, bool defaultValue = true)
    {
        _output.Add($"CONFIRM: {message}");
        if (_confirmResponses.Count == 0)
            return defaultValue;
        return _confirmResponses.Dequeue();
    }

    public void WriteInfo(string message) => _output.Add($"INFO: {message}");
    public void WriteError(string message) => _output.Add($"ERROR: {message}");
    public void WriteLine() => _output.Add("");
}
