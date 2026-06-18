using System.Text.Json;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Manages wizard session state persistence for resume support.
/// </summary>
internal interface ISessionStateManager
{
    /// <summary>Saves the current state to disk.</summary>
    void Save(WizardSessionState state);

    /// <summary>Loads existing state, or null if none exists.</summary>
    WizardSessionState? Load();

    /// <summary>Removes the session state file (called on successful completion).</summary>
    void Clear();

    /// <summary>The path where session state is stored.</summary>
    string StatePath { get; }
}

/// <summary>
/// File-based session state manager.
/// </summary>
internal sealed class FileSessionStateManager : ISessionStateManager
{
    private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public string StatePath { get; }

    public FileSessionStateManager(string? statePath = null)
    {
        StatePath = statePath ?? Path.Combine(Directory.GetCurrentDirectory(), ".wizard-session.json");
    }

    public void Save(WizardSessionState state)
    {
        var json = JsonSerializer.Serialize(state, _options);
        File.WriteAllText(StatePath, json);
    }

    public WizardSessionState? Load()
    {
        if (!File.Exists(StatePath))
            return null;

        var json = File.ReadAllText(StatePath);
        return JsonSerializer.Deserialize<WizardSessionState>(json);
    }

    public void Clear()
    {
        if (File.Exists(StatePath))
            File.Delete(StatePath);
    }
}
