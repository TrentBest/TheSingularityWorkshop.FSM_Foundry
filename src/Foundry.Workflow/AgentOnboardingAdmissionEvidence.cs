namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Bounded result of the CiAi AgentOnboarding contract evaluated by a trusted host adapter.
/// This value is audit evidence only; it never grants repository capabilities or human approval.
/// </summary>
public sealed record AgentOnboardingAdmissionEvidence(
    string Repository,
    string TargetSha,
    string Path,
    string ContractVersion,
    bool IsEligible,
    string Outcome,
    string? FailureCode,
    string Reason,
    string? ContentSha256)
{
    public const string RequiredPath = "AgentOnboarding.md";
    public const string RequiredContractVersion = "ciai.agent-onboarding.v1";

    public AgentOnboardingAdmissionEvidence ValidateFor(string repository, string targetSha)
    {
        if (!IsCanonicalRepository(Repository) ||
            !string.Equals(Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Onboarding evidence is not bound to the requested repository.");

        if (!IsFullSha(TargetSha) ||
            !string.Equals(TargetSha, targetSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Onboarding evidence is not bound to the exact requested target SHA.");

        if (!string.Equals(Path, RequiredPath, StringComparison.Ordinal) ||
            !string.Equals(ContractVersion, RequiredContractVersion, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Onboarding evidence does not identify the required root-level CiAi contract.");

        if (string.IsNullOrWhiteSpace(Outcome) || Outcome.Length > 64 || Outcome.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Reason) || Reason.Length > 2048 || Reason.Any(char.IsControl) ||
            (FailureCode is not null && (FailureCode.Length > 128 || FailureCode.Any(char.IsControl))))
            throw new ArgumentException("Onboarding evidence contains invalid bounded result metadata.");

        if (IsEligible)
        {
            if (!string.Equals(Outcome, "ELIGIBLE", StringComparison.Ordinal) ||
                FailureCode is not null || !IsSha256(ContentSha256))
                throw new UnauthorizedAccessException("Eligible onboarding evidence must contain the exact successful CiAi result and content digest.");
        }
        else if (!string.Equals(Outcome, "NOT_ELIGIBLE", StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(FailureCode))
        {
            throw new ArgumentException("Rejected onboarding evidence must retain its failure outcome and code.");
        }

        return this;
    }

    private static bool IsCanonicalRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            return false;
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(part =>
            part.Length is > 0 and <= 100 &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Trusted host port. Implementations must first resolve the repository's current
/// refs/heads/development head and require it to equal targetSha, then read AgentOnboarding.md
/// from that exact immutable commit, invoke CiAi's AgentOnboardingValidator, and return its
/// complete result. A caller-created evidence record is not proof; Foundry calls this verifier
/// before each governed task, model, and dispatch boundary, including after restart.
/// </summary>
public interface IAgentOnboardingAdmissionVerifier
{
    ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
        string repository, string targetSha, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owner-designated repositories whose autonomous task creation and CI dispatch require
/// exact-SHA onboarding admission. This allowlist is deliberately closed and narrow.
/// </summary>
public static class AgentOnboardingTargetPolicy
{
    private static readonly HashSet<string> Repositories = new(StringComparer.OrdinalIgnoreCase)
    {
        "TrentBest/TheSingularityWorkshop.SingularityBot",
        "TrentBest/TheSingularityWorkshop.ServerSC2",
        "TrentBest/WebPage"
    };

    public static bool RequiresAdmission(string repository) =>
        Repositories.Contains(repository);
}
