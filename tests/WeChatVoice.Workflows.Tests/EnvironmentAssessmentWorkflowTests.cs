using WeChatVoice.Workflows.Broker;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

public sealed class EnvironmentAssessmentWorkflowTests
{
    [Fact]
    public void Trust_failure_before_acl_probe_is_not_evaluated()
    {
        var result = EnvironmentAssessmentWorkflow.AssessInstallDirectorySecurity(
            new ReleaseBrokerTrustPolicy(),
            BrokerTrustResult.Deny("broker-publisher-mismatch"));

        Assert.Equal(InstallSecurityState.NotEvaluated, result.SecurityState);
        Assert.Equal(UserWriteability.Indeterminate, result.Writeability);
        Assert.False(result.Protected);
        Assert.False(result.UserWritable);
    }

    [Fact]
    public void Explicit_acl_probe_results_are_preserved()
    {
        var writable = EnvironmentAssessmentWorkflow.AssessInstallDirectorySecurity(
            new ReleaseBrokerTrustPolicy(),
            BrokerTrustResult.Deny("install-directory-user-writable"));
        var indeterminate = EnvironmentAssessmentWorkflow.AssessInstallDirectorySecurity(
            new ReleaseBrokerTrustPolicy(),
            BrokerTrustResult.Deny("install-directory-writeability-indeterminate"));
        var protectedResult = EnvironmentAssessmentWorkflow.AssessInstallDirectorySecurity(
            new ReleaseBrokerTrustPolicy(),
            BrokerTrustResult.Ok());

        Assert.Equal(InstallSecurityState.UserWritable, writable.SecurityState);
        Assert.True(writable.UserWritable);
        Assert.Equal(InstallSecurityState.Indeterminate, indeterminate.SecurityState);
        Assert.Equal(InstallSecurityState.VerifiedProtected, protectedResult.SecurityState);
        Assert.True(protectedResult.Protected);
    }

    [Fact]
    public void Development_mode_does_not_claim_release_directory_protection()
    {
        var result = EnvironmentAssessmentWorkflow.AssessInstallDirectorySecurity(
            new DevelopmentBrokerTrustPolicy(),
            BrokerTrustResult.Ok());

        Assert.Equal(InstallSecurityState.DevelopmentModeNotApplicable, result.SecurityState);
        Assert.False(result.Protected);
        Assert.False(result.UserWritable);
    }
}
