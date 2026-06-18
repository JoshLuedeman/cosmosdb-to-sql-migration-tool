using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

public class SessionStateManagerTests : IDisposable
{
    private readonly string _tempDir;

    public SessionStateManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Save_And_Load_RoundTrips()
    {
        var path = Path.Combine(_tempDir, ".wizard-session.json");
        var manager = new FileSessionStateManager(path);

        var state = new WizardSessionState
        {
            CurrentStep = 3,
            Endpoint = "https://test.documents.azure.com:443/",
            AnalyzeAllDatabases = true,
            WorkspaceId = "12345678-1234-1234-1234-123456789012",
            OutputDirectory = "./out"
        };

        manager.Save(state);
        var loaded = manager.Load();

        loaded.Should().NotBeNull();
        loaded!.CurrentStep.Should().Be(3);
        loaded.Endpoint.Should().Be("https://test.documents.azure.com:443/");
        loaded.AnalyzeAllDatabases.Should().BeTrue();
        loaded.WorkspaceId.Should().Be("12345678-1234-1234-1234-123456789012");
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var manager = new FileSessionStateManager(path);

        manager.Load().Should().BeNull();
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        var path = Path.Combine(_tempDir, ".wizard-session.json");
        var manager = new FileSessionStateManager(path);

        manager.Save(new WizardSessionState { CurrentStep = 1 });
        File.Exists(path).Should().BeTrue();

        manager.Clear();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Clear_NoFile_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var manager = new FileSessionStateManager(path);

        var act = () => manager.Clear();
        act.Should().NotThrow();
    }
}

public class WizardRunnerResumeTests
{
    [Fact]
    public void Run_ResumeFromStep3_SkipsEarlierSteps()
    {
        // State says we completed steps 0-2 (endpoint, db, monitor)
        var state = new WizardSessionState
        {
            CurrentStep = 3,
            Endpoint = "https://saved.documents.azure.com:443/",
            AnalyzeAllDatabases = true,
            IncludeMonitor = false
        };

        // Only need to provide answers for steps 3+ (output dir, report type, confirmations)
        var console = new FakeWizardConsole()
            .QueuePrompt("./resumed-output") // output dir
            .QueueSelect(0) // both reports
            .QueueConfirm(true) // confirm summary
            .QueueConfirm(false); // don't save config

        var runner = new WizardRunner(console);
        var options = runner.Run(resumeState: state);

        options.AccountEndpoint.Should().Be("https://saved.documents.azure.com:443/");
        options.AnalyzeAllDatabases.Should().BeTrue();
        options.OutputDirectory.Should().Be("./resumed-output");
        console.Output.Should().Contain(s => s.Contains("Resuming"));
    }

    [Fact]
    public void Run_WithSessionManager_SavesStateAfterEachStep()
    {
        var manager = new InMemorySessionStateManager();
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0) // all databases
            .QueueConfirm(false) // no monitor
            .QueuePrompt("./out")
            .QueueSelect(0) // both
            .QueueConfirm(true) // confirm
            .QueueConfirm(false); // don't save config

        var runner = new WizardRunner(console, sessionManager: manager);
        runner.Run();

        // State was saved multiple times (once per step) then cleared
        manager.SaveCount.Should().BeGreaterThanOrEqualTo(5);
        manager.WasCleared.Should().BeTrue();
    }

    [Fact]
    public void Run_NoResumeState_StartsFromBeginning()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0)
            .QueueConfirm(false)
            .QueuePrompt("./out")
            .QueueSelect(0)
            .QueueConfirm(true)
            .QueueConfirm(false);

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.AccountEndpoint.Should().Be("https://acct.documents.azure.com:443/");
        console.Output.Should().NotContain(s => s.Contains("Resuming"));
    }
}

/// <summary>In-memory session state manager for testing.</summary>
internal sealed class InMemorySessionStateManager : ISessionStateManager
{
    public int SaveCount { get; private set; }
    public bool WasCleared { get; private set; }
    public WizardSessionState? LastState { get; private set; }
    public string StatePath => "<in-memory>";

    public void Save(WizardSessionState state)
    {
        SaveCount++;
        LastState = state;
    }

    public WizardSessionState? Load() => LastState;

    public void Clear()
    {
        WasCleared = true;
        LastState = null;
    }
}
