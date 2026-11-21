using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class AgentOnboardingAdmissionEvidenceTests
{
    private const string Repository = "TrentBest/Sample";
    private static readonly string TargetSha = new('a', 40);

    [Fact]
    public void EligibleEvidenceMustBindExactRepositoryCommitContractAndDigest()
    {
        var evidence = EligibleEvidence();

        Assert.Same(evidence, evidence.ValidateFor(Repository, TargetSha));
    }

    [Fact]
    public void RejectsEvidenceForDifferentRepositoryOrTargetSha()
    {
        var evidence = EligibleEvidence();

        Assert.Throws<UnauthorizedAccessException>(() =>
            evidence.ValidateFor("TrentBest/Other", TargetSha));
        Assert.Throws<UnauthorizedAccessException>(() =>
            evidence.ValidateFor(Repository, new string('b', 40)));
    }

    [Fact]
    public void RejectsAlternatePathOrContractVersion()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            (EligibleEvidence() with { Path = "README.md" }).ValidateFor(Repository, TargetSha));
        Assert.Throws<UnauthorizedAccessException>(() =>
            (EligibleEvidence() with { ContractVersion = "ciai.agent-onboarding.v2" })
                .ValidateFor(Repository, TargetSha));
    }

    [Fact]
    public void EligibleEvidenceRequiresSuccessfulOutcomeAndSha256Digest()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            (EligibleEvidence() with { Outcome = "NOT_ELIGIBLE" })
                .ValidateFor(Repository, TargetSha));
        Assert.Throws<UnauthorizedAccessException>(() =>
            (EligibleEvidence() with { ContentSha256 = null })
                .ValidateFor(Repository, TargetSha));
        Assert.Throws<UnauthorizedAccessException>(() =>
            (EligibleEvidence() with { ContentSha256 = "not-a-digest" })
                .ValidateFor(Repository, TargetSha));
    }

    [Fact]
    public void RejectedEvidenceRetainsBoundedFailureMetadata()
    {
        var evidence = EligibleEvidence() with
        {
            IsEligible = false,
            Outcome = "NOT_ELIGIBLE",
            FailureCode = "onboarding.missing",
            Reason = "The exact target commit does not contain AgentOnboarding.md.",
            ContentSha256 = null
        };

        Assert.Same(evidence, evidence.ValidateFor(Repository, TargetSha));
        Assert.Throws<ArgumentException>(() =>
            (evidence with { FailureCode = null }).ValidateFor(Repository, TargetSha));
    }

    private static AgentOnboardingAdmissionEvidence EligibleEvidence() => new(
        Repository, TargetSha, AgentOnboardingAdmissionEvidence.RequiredPath,
        AgentOnboardingAdmissionEvidence.RequiredContractVersion, true, "ELIGIBLE",
        null, "CiAi v1 contract passed at the exact requested commit.", new string('f', 64));
}
