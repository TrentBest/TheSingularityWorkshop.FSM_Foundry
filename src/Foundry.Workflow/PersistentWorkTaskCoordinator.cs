namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Couples coordinator mutations to the local journal. If a journal write fails after
/// an in-memory mutation, this adapter permanently fails closed for the process; callers
/// must restart and restore from the journal before accepting more work.
/// </summary>
public sealed class PersistentWorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly WorkTaskCoordinator _coordinator;
    private readonly JsonlWorkTaskStore _store;
    private readonly JsonlWorkTaskRepairAttemptJournal? _repairAttemptJournal;
    private Exception? _persistenceFailure;

    public PersistentWorkTaskCoordinator(JsonlWorkTaskStore store,
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _coordinator = new WorkTaskCoordinator();
        foreach (var snapshot in _store.GetAll())
            _coordinator.Restore(snapshot);
    }

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Submit(request);
            return Persist(snapshot, create: true);
        }
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Claim(taskId, agentId, leaseDuration, now);
            return Persist(snapshot);
        }
    }

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.Transition(taskId, leaseId, next, now, reason);
            return Persist(snapshot);
        }
    }

    /// <summary>Durably records a recovery already authorized by the host approval gate.</summary>
    internal WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, long expectedTaskEventSequence, DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.RecoverFromCodeRed(taskId, leaseId, incident,
                expectedTaskEventSequence, now, reason);
            return Persist(snapshot);
        }
    }

    /// <summary>
    /// Applies a repair decision using only the exact attempt history recovered from the
    /// durable append-only attempt journal. Caller-supplied history is deliberately not accepted.
    /// </summary>
    public WorkTaskSnapshot ApplyRepairDecision(string taskId, Guid leaseId,
        WorkTaskRepairAttempt currentAttempt, WorkTaskRepairEvidenceExpectation expectation,
        TimeSpan maximumDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
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
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "Task dispatch is disabled because a prior journal write failed. Restart and restore from durable state before continuing.",
                _persistenceFailure);
    }
}
