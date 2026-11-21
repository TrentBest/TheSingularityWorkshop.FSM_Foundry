using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Owns the production composition of the GitHub repository adapter, Actions dispatcher,
/// exact-run candidate-checkout provenance verifier, and durable repair-evidence lifecycle bridge.
/// The host supplies durable stores, token provisioning, allowlists, and redirect-disabled clients.
/// </summary>
public sealed class GitHubRepositoryOperationHost
{
    private GitHubRepositoryOperationHost(
        GitHubRepositoryOperationAdapter repositoryOperations,
        GitHubActionsWorkflowDispatcher actionsDispatcher,
        GitHubActionsArtifactProvenanceReader provenanceReader,
        GitHubActionsRepairEvidenceCoordinator repairEvidence,
        GitHubAgentOnboardingAdmissionVerifier onboardingAdmissionVerifier,
        PersistentWorkTaskCoordinator tasks)
    {
        RepositoryOperations = repositoryOperations;
        ActionsDispatcher = actionsDispatcher;
        ProvenanceReader = provenanceReader;
        RepairEvidence = repairEvidence;
        OnboardingAdmissionVerifier = onboardingAdmissionVerifier;
        _tasks = tasks;
    }

    private readonly PersistentWorkTaskCoordinator _tasks;

    /// <summary>Capability-gated repository-operation adapter to register with Foundry's host dispatcher.</summary>
    public GitHubRepositoryOperationAdapter RepositoryOperations { get; }

    /// <summary>Host-owned dispatcher used by repository CI operations.</summary>
    public GitHubActionsWorkflowDispatcher ActionsDispatcher { get; }

    /// <summary>Verifier for the provenance artifact of an already-correlated exact run attempt.</summary>
    public GitHubActionsArtifactProvenanceReader ProvenanceReader { get; }

    /// <summary>Lease-bound bridge from verified CI evidence to durable task repair decisions.</summary>
    public GitHubActionsRepairEvidenceCoordinator RepairEvidence { get; }

    /// <summary>Exact-development-SHA verifier backed by CiAi's pinned authoritative validator.</summary>
    public GitHubAgentOnboardingAdmissionVerifier OnboardingAdmissionVerifier { get; }

    /// <summary>Revalidates restored governed tasks before a host accepts lifecycle work.</summary>
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _tasks.InitializeAsync(OnboardingAdmissionVerifier, cancellationToken);

    /// <summary>Host-owned task admission; governed targets are checked before durable creation.</summary>
    public ValueTask<WorkTaskSnapshot> SubmitTaskAsync(WorkTaskRequest request,
        CancellationToken cancellationToken = default) =>
        _tasks.SubmitAsync(request, OnboardingAdmissionVerifier, cancellationToken);

    /// <summary>
    /// Creates the last-mile repository dispatcher with the host's trusted onboarding verifier
    /// already wired. Callers cannot accidentally omit governed-target revalidation.
    /// </summary>
    public AuthorizedRepositoryOperationDispatcher CreateAuthorizedRepositoryOperationDispatcher(
        WorkTaskCapabilityRegistry capabilities,
        IRepositoryOperationAuditJournal auditJournal,
        Func<DateTimeOffset>? clock = null) =>
        new(_tasks, capabilities, RepositoryOperations, auditJournal, clock, OnboardingAdmissionVerifier);

    /// <summary>Creates a HeadlessAi turn coordinator with the host verifier already wired.</summary>
    public FoundryHeadlessAiTurnCoordinator CreateHeadlessAiTurnCoordinator(
        IHeadlessAiAgentTurnClient client,
        JsonlHeadlessAiTurnJournal journal,
        TimeProvider? timeProvider = null) =>
        new(_tasks, client, journal, timeProvider, OnboardingAdmissionVerifier);

    /// <summary>
    /// Creates the complete GitHub host composition. Both clients must have automatic redirects
    /// disabled. The API client must be rooted at https://api.github.com/ and have no default
    /// Authorization header; credentials are supplied per request by the host token provider.
    /// The artifact client must have no default Authorization header and must not follow redirects.
    /// The task coordinator and repair-attempt journal must share the same journal instance.
    /// Required job names are host policy, not values supplied by a workflow or candidate.
    /// </summary>
    public static GitHubRepositoryOperationHost Create(
        HttpClient apiClient,
        HttpClient artifactDownloadClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy repositoryPolicy,
        GitHubActionsWorkflowAllowlist workflows,
        IGitHubActionsDispatchJournal dispatchJournal,
        IGitHubRepositoryStagingStore stagingStore,
        PersistentWorkTaskCoordinator tasks,
        JsonlWorkTaskRepairAttemptJournal repairAttempts,
        IReadOnlyCollection<string> requiredJobNames,
        TimeSpan maximumRepairDuration,
        string trustedWorkflowSha,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(artifactDownloadClient);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(repositoryPolicy);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(dispatchJournal);
        ArgumentNullException.ThrowIfNull(stagingStore);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(repairAttempts);
        ArgumentNullException.ThrowIfNull(requiredJobNames);

        if (!ReferenceEquals(tasks.RepairAttemptJournal, repairAttempts))
            throw new ArgumentException(
                "The task coordinator and repair-evidence bridge must share the exact durable repair-attempt journal instance.",
                nameof(repairAttempts));

        if (apiClient.DefaultRequestHeaders.Authorization is not null ||
            artifactDownloadClient.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("GitHub host clients must not carry ambient Authorization headers.");

        var dispatcher = new GitHubActionsWorkflowDispatcher(
            apiClient, tokens, repositoryPolicy, workflows, dispatchJournal, trustedWorkflowSha, clock);
        var provenanceReader = new GitHubActionsArtifactProvenanceReader(
            apiClient, artifactDownloadClient, tokens, repositoryPolicy, dispatchJournal, clock);
        var repositoryAdapter = new GitHubRepositoryOperationAdapter(
            apiClient, tokens, repositoryPolicy, stagingStore, clock, dispatcher);
        var repairEvidence = new GitHubActionsRepairEvidenceCoordinator(
            dispatcher, provenanceReader, dispatchJournal, tasks, repairAttempts,
            requiredJobNames, maximumRepairDuration, clock);
        var onboardingAdmissionVerifier = new GitHubAgentOnboardingAdmissionVerifier(
            apiClient, tokens, repositoryPolicy);

        return new GitHubRepositoryOperationHost(
            repositoryAdapter, dispatcher, provenanceReader, repairEvidence,
            onboardingAdmissionVerifier, tasks);
    }
}
