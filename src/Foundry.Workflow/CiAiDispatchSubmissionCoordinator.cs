namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host boundary for submitting an already-persisted CI workflow intent.
/// Implementations own GitHub credentials and transport; they must not infer
/// workflow trust from the candidate ref or from caller-supplied run metadata.
/// </summary>
public interface ICiAiWorkflowDispatcher
{
    /// <summary>
    /// Submits the registered workflow using the exact trusted workflow and
    /// candidate provenance in the mapping. GitHub dispatch normally does not
    /// return the run ID; callers must reconcile the resulting run separately.
    /// </summary>
    Task DispatchAsync(CiAiRequestTaskMapping mapping, CancellationToken cancellationToken);
}

public enum CiAiDispatchSubmissionOutcome
{
    SubmittedAwaitingReconciliation,
    ExistingIntentRequiresReconciliation,
    Conflict
}

public sealed record CiAiDispatchSubmissionResult(
    CiAiDispatchSubmissionOutcome Outcome,
    string RequestId);

/// <summary>
/// Persists a dispatch intent before crossing the host/network boundary.
/// It never resubmits an existing intent: after restart or an ambiguous transport
/// failure, the host must reconcile the exact run marker before considering a
/// separately authorized new dispatch.
/// </summary>
public static class CiAiDispatchSubmissionCoordinator
{
    public static async Task<CiAiDispatchSubmissionResult> SubmitAsync(
        JsonlCiAiRequestTaskMappingJournal journal,
        CiAiRequestTaskMapping mapping,
        ICiAiWorkflowDispatcher dispatcher,
        IAgentOnboardingAdmissionVerifier? admissionVerifier = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(dispatcher);
        // Validate ordinary mapping fields before contacting the host. For governed
        // repositories, admission is fetched and evaluated asynchronously before any
        // durable intent or dispatcher side effect can occur.
        mapping.Validate(requireAdmissionEvidence: false);
        if (AgentOnboardingTargetPolicy.RequiresAdmission(mapping.Repository))
        {
            if (admissionVerifier is null)
                throw new UnauthorizedAccessException("A trusted exact-SHA onboarding verifier is required for this repository.");
            var evidence = await admissionVerifier.VerifyAsync(
                mapping.Repository, mapping.CandidateSha, cancellationToken).ConfigureAwait(false);
            evidence.ValidateFor(mapping.Repository, mapping.CandidateSha);
            if (!evidence.IsEligible)
                throw new UnauthorizedAccessException("CiAi AgentOnboarding validation rejected this target.");
            mapping = mapping with { AdmissionEvidence = evidence };
        }
        mapping.Validate();

        var intent = AgentOnboardingTargetPolicy.RequiresAdmission(mapping.Repository)
            ? journal.CreateDispatchIntentAfterAdmission(mapping)
            : journal.CreateDispatchIntent(mapping);
        if (intent == CiAiRequestMappingAppendOutcome.Conflict)
            return new(CiAiDispatchSubmissionOutcome.Conflict, mapping.RequestId);
        if (intent == CiAiRequestMappingAppendOutcome.Duplicate)
            return new(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, mapping.RequestId);

        // Revalidate at the last possible boundary before crossing the dispatcher/network
        // side effect. If the target head moved after intent creation, leave the intent durable
        // but do not dispatch a stale task; reconciliation must resolve that pending intent.
        if (AgentOnboardingTargetPolicy.RequiresAdmission(mapping.Repository))
        {
            var finalEvidence = await admissionVerifier!.VerifyAsync(
                mapping.Repository, mapping.CandidateSha, cancellationToken).ConfigureAwait(false);
            finalEvidence.ValidateFor(mapping.Repository, mapping.CandidateSha);
            if (!finalEvidence.IsEligible || finalEvidence != mapping.AdmissionEvidence)
                throw new UnauthorizedAccessException(
                    "The exact-SHA AgentOnboarding result changed before CI dispatch; the durable intent was not dispatched.");
        }

        // The durable intent exists before any external side effect. If this call
        // throws or is cancelled, its outcome may be ambiguous; do not resubmit it.
        await dispatcher.DispatchAsync(mapping, cancellationToken).ConfigureAwait(false);
        return new(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, mapping.RequestId);
    }
}
