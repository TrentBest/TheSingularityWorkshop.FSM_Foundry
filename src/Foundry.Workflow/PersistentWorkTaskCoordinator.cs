namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Couples coordinator mutations to the local journal. If a journal write fails after
/// an in-memory mutation, this adapter permanently fails closed for the process; callers
/// must restart and restore from the journal before accepting more work.
/// </summary>
public sealed class PersistentWorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _taskGates = new(StringComparer.Ordinal);
    private readonly WorkTaskCoordinator _coordinator;
    private readonly JsonlWorkTaskStore _store;
    private readonly JsonlWorkTaskRepairAttemptJournal? _repairAttemptJournal;
    private Exception? _persistenceFailure;
    private bool _admissionReady = true;

    public PersistentWorkTaskCoordinator(JsonlWorkTaskStore store,
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null,
        ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _coordinator = new WorkTaskCoordinator(recoveryReceiptVerifier);
        var restored = _store.GetAll();
        foreach (var snapshot in restored)
            _coordinator.Restore(snapshot);
        _admissionReady = !restored.Any(snapshot =>
            AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository));
    }

    /// <summary>The exact durable repair-attempt journal used by evidence decisions, if configured.</summary>
    public JsonlWorkTaskRepairAttemptJournal? RepairAttemptJournal => _repairAttemptJournal;

    /// <summary>
    /// Revalidates every restored governed task against the trusted host before the coordinator
    /// exposes any task lifecycle operation after process restart.
    /// </summary>
    public async ValueTask InitializeAsync(IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        var restored = _store.GetAll();
        foreach (var snapshot in restored)
        {
            if (!AgentOnboardingTargetPolicy.RequiresAdmission(snapshot.Request.Repository))
                continue;
            var stored = snapshot.Request.AdmissionEvidence ?? throw new UnauthorizedAccessException(
                "A restored governed task is missing immutable AgentOnboarding admission provenance.");
            var current = await admissionVerifier.VerifyAsync(snapshot.Request.Repository,
                snapshot.Request.BaseSha, cancellationToken).ConfigureAwait(false);
            current.ValidateFor(snapshot.Request.Repository, snapshot.Request.BaseSha);
            if (!current.IsEligible || current != stored)
                throw new UnauthorizedAccessException(
                    $"Restored task '{snapshot.Request.TaskId}' no longer has matching exact-SHA AgentOnboarding admission.");
        }

        lock (_gate)
        {
            EnsurePersistenceHealthy();
            _admissionReady = true;
        }
    }

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithTaskLock(request.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.Submit(request);
                return Persist(snapshot, create: true);
            }
        });
    }

    /// <summary>Verifies governed onboarding before either in-memory mutation or durable creation.</summary>
    public async ValueTask<WorkTaskSnapshot> SubmitAsync(WorkTaskRequest request,
        IAgentOnboardingAdmissionVerifier admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admissionVerifier);
        var validated = request.Validate(requireAdmissionEvidence: false);
        AgentOnboardingAdmissionEvidence? evidence = null;
        if (AgentOnboardingTargetPolicy.RequiresAdmission(validated.Repository))
        {
            evidence = await admissionVerifier.VerifyAsync(validated.Repository, validated.BaseSha,
                cancellationToken).ConfigureAwait(false);
            evidence.ValidateFor(validated.Repository, validated.BaseSha);
            if (!evidence.IsEligible)
                throw new UnauthorizedAccessException("The repository's AgentOnboarding contract is not eligible for autonomous task creation.");
        }

        return WithTaskLock(validated.TaskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = evidence is null
                    ? _coordinator.Submit(validated with { AdmissionEvidence = null })
                    : _coordinator.SubmitAdmitted(validated, evidence);
                return Persist(snapshot, create: true);
            }
        });
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.Claim(taskId, agentId, leaseDuration, now);
                return Persist(snapshot);
            }
        });

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.Transition(taskId, leaseId, next, now, reason);
                return Persist(snapshot);
            }
        });

    /// <summary>Verifies and durably records a host-approved CODE RED recovery.</summary>
    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, CodeRedRecoveryApprovalReceipt receipt,
        DateTimeOffset now, string reason) =>
        WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var snapshot = _coordinator.RecoverFromCodeRed(taskId, leaseId, incident, receipt, now, reason);
                return Persist(snapshot);
            }
        });

    /// <summary>
    /// Applies a repair decision using only the exact attempt history recovered from the
    /// durable append-only attempt journal. Caller-supplied history is deliberately not accepted.
    /// </summary>
    public WorkTaskSnapshot ApplyRepairDecision(string taskId, Guid leaseId,
        WorkTaskRepairAttempt currentAttempt, WorkTaskRepairEvidenceExpectation expectation,
        TimeSpan maximumDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
        return WithTaskLock(taskId, () =>
        {
            lock (_gate)
            {
                EnsureHealthy();
                var journal = _repairAttemptJournal ?? throw new InvalidOperationException(
                    "A durable repair-attempt journal is required before applying a persistent repair decision.");
                return journal.ExecuteWithAttempts(taskId, history =>
                {
                    if (history.Count == 0 || history[^1] != currentAttempt ||
                        currentAttempt.TaskId != taskId)
                        throw new InvalidOperationException(
                            "The current CI attempt must exactly match the latest durable attempt-journal record for this task.");
                    var previousAttempts = history.Take(history.Count - 1).ToArray();
                    var snapshot = _coordinator.ApplyRepairDecision(taskId, leaseId, previousAttempts,
                        currentAttempt, expectation, maximumDuration, now);
                    return Persist(snapshot);
                });
            }
        });
    }

    /// <summary>
    /// Serializes a repository dispatch against lifecycle mutations for this task only.
    /// The reservation remains held until the asynchronous host operation and its audit
    /// completion have finished. Cancellation while waiting does not acquire the reservation.
    /// </summary>
    public async ValueTask<T> ExecuteSerializedAsync<T>(string taskId,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var taskGate = GetTaskGate(taskId);
        await taskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            taskGate.Release();
        }
    }

    private T WithTaskLock<T>(string taskId, Func<T> operation)
    {
        var taskGate = GetTaskGate(taskId);
        taskGate.Wait();
        try
        {
            return operation();
        }
        finally
        {
            taskGate.Release();
        }
    }

    private SemaphoreSlim GetTaskGate(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw new ArgumentException("Task identity must be non-empty, bounded, and control-free.", nameof(taskId));
        return _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
    }

    public WorkTaskSnapshot GetSnapshot(string taskId)
    {
        lock (_gate)
        {
            EnsureHealthy();
            return _coordinator.GetSnapshot(taskId);
        }
    }

    private WorkTaskSnapshot Persist(WorkTaskSnapshot snapshot, bool create = false)
    {
        try
        {
            return create ? _store.Create(snapshot) : _store.Append(snapshot);
        }
        catch (Exception exception)
        {
            _persistenceFailure = exception;
            throw new InvalidOperationException(
                "Task state changed in memory but could not be durably journaled. This coordinator is now fail-closed; restart and restore before dispatch.",
                exception);
        }
    }

    private void EnsureHealthy()
    {
        EnsurePersistenceHealthy();
        if (!_admissionReady)
            throw new InvalidOperationException(
                "Restored governed tasks are unavailable until InitializeAsync revalidates their exact-SHA AgentOnboarding evidence.");
    }

    private void EnsurePersistenceHealthy()
    {
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "Task dispatch is disabled because a prior journal write failed. Restart and restore from durable state before continuing.",
                _persistenceFailure);
    }
}
