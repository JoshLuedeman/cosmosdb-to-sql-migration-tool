using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

public class WizardRunnerTests
{
    [Fact]
    public void Run_AllDatabases_WithMonitor_ReturnsCorrectOptions()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://myaccount.documents.azure.com:443/") // endpoint
            .QueueSelect(0) // "Analyze all databases"
            .QueueConfirm(true) // include monitor
            .QueuePrompt("12345678-1234-1234-1234-123456789012") // workspace ID
            .QueueConfirm(false) // no auto-discover
            .QueuePrompt("./reports") // output dir
            .QueueSelect(0) // "Both assessment reports and SQL projects"
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.Interactive.Should().BeTrue();
        options.AccountEndpoint.Should().Be("https://myaccount.documents.azure.com:443/");
        options.AnalyzeAllDatabases.Should().BeTrue();
        options.DatabaseName.Should().BeNull();
        options.WorkspaceId.Should().Be("12345678-1234-1234-1234-123456789012");
        options.AutoDiscoverMonitoring.Should().BeFalse();
        options.OutputDirectory.Should().Be("./reports");
        options.AssessmentOnly.Should().BeFalse();
        options.ProjectOnly.Should().BeFalse();
    }

    [Fact]
    public void Run_SpecificDatabase_NoMonitor_AssessmentOnly_ReturnsCorrectOptions()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://other.documents.azure.com:443/") // endpoint
            .QueueSelect(1) // "Specify a single database"
            .QueuePrompt("MyDatabase") // database name
            .QueueConfirm(false) // no monitor
            .QueuePrompt("C:\\output") // output dir
            .QueueSelect(1) // "Assessment reports only"
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.AccountEndpoint.Should().Be("https://other.documents.azure.com:443/");
        options.AnalyzeAllDatabases.Should().BeFalse();
        options.DatabaseName.Should().Be("MyDatabase");
        options.WorkspaceId.Should().BeNull();
        options.AutoDiscoverMonitoring.Should().BeFalse();
        options.OutputDirectory.Should().Be("C:\\output");
        options.AssessmentOnly.Should().BeTrue();
        options.ProjectOnly.Should().BeFalse();
    }

    [Fact]
    public void Run_ProjectOnly_ReturnsCorrectOptions()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/") // endpoint
            .QueueSelect(0) // all databases
            .QueueConfirm(false) // no monitor
            .QueuePrompt("") // output dir (will use default)
            .QueueSelect(2) // "SQL projects only"
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.ProjectOnly.Should().BeTrue();
        options.AssessmentOnly.Should().BeFalse();
    }

    [Fact]
    public void Run_DefaultOutputDirectory_UsesDefault()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0) // all databases
            .QueueConfirm(false) // no monitor
            // no prompt response queued → returns defaultValue ("./output")
            .QueueSelect(0) // both
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.OutputDirectory.Should().Be("./output");
    }

    [Fact]
    public void Run_WithAutoDiscover_SetsFlag()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0) // all databases
            .QueueConfirm(true) // include monitor
            .QueuePrompt("12345678-1234-1234-1234-123456789012") // workspace ID
            .QueueConfirm(true) // auto-discover = yes
            .QueuePrompt("./out")
            .QueueSelect(0) // both
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.AutoDiscoverMonitoring.Should().BeTrue();
        options.WorkspaceId.Should().Be("12345678-1234-1234-1234-123456789012");
    }

    [Fact]
    public void Run_Cancellation_ThrowsOperationCanceledException()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new WizardRunner(console);

        var act = () => runner.Run(cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Run_DisplaysWelcomeMessage()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0)
            .QueueConfirm(false)
            .QueuePrompt("./out")
            .QueueSelect(0)
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        runner.Run();

        console.Output.Should().Contain(s => s.Contains("Wizard"));
        console.Output.Should().Contain(s => s.Contains("Step 1"));
        console.Output.Should().Contain(s => s.Contains("Step 2"));
    }

    [Fact]
    public void Constructor_NullConsole_ThrowsArgumentNullException()
    {
        var act = () => new WizardRunner(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Run_InvalidEndpointThenValid_RetriesAndSucceeds()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("not-a-url") // first attempt - invalid
            .QueuePrompt("https://acct.documents.azure.com:443/") // second attempt - valid
            .QueueSelect(0) // all databases
            .QueueConfirm(false) // no monitor
            .QueuePrompt("./out")
            .QueueSelect(0)
            .QueueConfirm(true); // confirm summary

        var runner = new WizardRunner(console);
        var options = runner.Run();

        options.AccountEndpoint.Should().Be("https://acct.documents.azure.com:443/");
        console.Output.Should().Contain(s => s.Contains("VALIDATION_ERROR"));
    }

    [Fact]
    public void Run_UserDeclinesSummary_ThrowsOperationCanceledException()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://acct.documents.azure.com:443/")
            .QueueSelect(0) // all databases
            .QueueConfirm(false) // no monitor
            .QueuePrompt("./out")
            .QueueSelect(0)
            .QueueConfirm(false); // decline summary

        var runner = new WizardRunner(console);

        var act = () => runner.Run();
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Run_DisplaysSummaryWithConfiguration()
    {
        var console = new FakeWizardConsole()
            .QueuePrompt("https://myaccount.documents.azure.com:443/")
            .QueueSelect(0) // all databases
            .QueueConfirm(true) // include monitor
            .QueuePrompt("12345678-1234-1234-1234-123456789012")
            .QueueConfirm(false) // no auto-discover
            .QueuePrompt("./reports")
            .QueueSelect(0)
            .QueueConfirm(true); // confirm

        var runner = new WizardRunner(console);
        runner.Run();

        console.Output.Should().Contain(s => s.Contains("Configuration Summary"));
        console.Output.Should().Contain(s => s.Contains("myaccount"));
        console.Output.Should().Contain(s => s.Contains("All databases"));
        console.Output.Should().Contain(s => s.Contains("./reports"));
    }
}
