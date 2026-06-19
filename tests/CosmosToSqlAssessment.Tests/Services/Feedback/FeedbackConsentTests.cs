using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class FeedbackConsentTests
{
    [Fact]
    public void Resolve_NoInputs_DefaultsToDisabled()
    {
        var consent = FeedbackConsent.Resolve(commandLineOptIn: null, configEnabled: null, envOptOut: false, envOptIn: false);

        consent.IsGranted.Should().BeFalse();
        consent.Source.Should().Be(FeedbackConsentSource.Default);
    }

    [Fact]
    public void Resolve_EnvOptOut_OverridesEverything()
    {
        var consent = FeedbackConsent.Resolve(commandLineOptIn: true, configEnabled: true, envOptOut: true, envOptIn: true);

        consent.IsGranted.Should().BeFalse();
        consent.Source.Should().Be(FeedbackConsentSource.EnvironmentOptOut);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_CommandLine_TakesPrecedenceOverConfigAndEnvOptIn(bool cliValue)
    {
        var consent = FeedbackConsent.Resolve(commandLineOptIn: cliValue, configEnabled: !cliValue, envOptOut: false, envOptIn: true);

        consent.IsGranted.Should().Be(cliValue);
        consent.Source.Should().Be(FeedbackConsentSource.CommandLine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_Configuration_TakesPrecedenceOverEnvOptIn(bool configValue)
    {
        var consent = FeedbackConsent.Resolve(commandLineOptIn: null, configEnabled: configValue, envOptOut: false, envOptIn: true);

        consent.IsGranted.Should().Be(configValue);
        consent.Source.Should().Be(FeedbackConsentSource.Configuration);
    }

    [Fact]
    public void Resolve_EnvOptIn_AppliesOnlyWhenHigherSourcesSilent()
    {
        var consent = FeedbackConsent.Resolve(commandLineOptIn: null, configEnabled: null, envOptOut: false, envOptIn: true);

        consent.IsGranted.Should().BeTrue();
        consent.Source.Should().Be(FeedbackConsentSource.EnvironmentOptIn);
    }
}
