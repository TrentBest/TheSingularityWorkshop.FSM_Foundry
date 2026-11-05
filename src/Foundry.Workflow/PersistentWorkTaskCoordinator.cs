using System.Security.Cryptography;

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
        JsonlWorkTaskRepairAttemptJournal? repairAttemptJournal = null,
        ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repairAttemptJournal = repairAttemptJournal;
        _coordinator = new WorkTaskCoordinator(recoveryReceiptVerifier);
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

    /// <summary>Verifies and durably records a host-approved CODE RED recovery.</summary>
    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, CodeRedRecoveryApprovalReceipt receipt,
        DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var snapshot = _coordinator.RecoverFromCodeRed(taskId, leaseId, incident, receipt, now, reason);
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

    /// <summary>
    /// Consumes a verified CiAi report, appends its exact attempt idempotently, and applies
    /// the existing Foundry lifecycle policy using only CiAi's verdict-derived attempt.
    /// A duplicate delivery already reflected in task history is acknowledged without
    /// applying a second transition.
    /// </summary>
    public CiAiRepairFeedbackResult ApplyCiAiReport(
        string taskId,
        Guid leaseId,
        FoundryVerifiedCiAiReportArtifact artifact,
        TimeSpan maximumDuration,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate)
        {
            EnsureHealthy();
            var journal = _repairAttemptJournal ?? throw new InvalidOperationException(
                "A durable repair-attempt journal is required before consuming CiAi repair evidence.");
            var task = _coordinator.GetSnapshot(taskId);
            if (now == default || task.Lease is not { } lease ||
                lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new UnauthorizedAccessException("A current, unexpired task lease is required to apply CiAi evidence.");
            var mapping = artifact.Request.Mapping.Validate();
            var run = artifact.Request.RunBinding
                ?? throw new InvalidOperationException("CiAi report is not bound to an exact GitHub run.");
            if (!string.Equals(mapping.TaskId, taskId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("CiAi report mapping identifies a different Foundry task.");

            var reportDigest = Convert.ToHexString(SHA256.HashData(artifact.Bytes.Span)).ToLowerInvariant();
            var prior = journal.GetAttempts(taskId).SingleOrDefault(item =>
                item.CiRunId == run.RunId && item.CiRunAttempt == run.RunAttempt);
            if (prior is not null)
            {
                if (!string.Equals(prior.EvidencePayloadSha256, reportDigest, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(prior.CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                    prior.CorrelationId != mapping.CorrelationId ||
                    !string.Equals(prior.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A previously recorded CiAi run was redelivered with conflicting report content or task provenance.");

                var alreadyApplied = task.Events.Any(item =>
                    item.CiRunId == run.RunId && item.CiRunAttempt == run.RunAttempt &&
                    string.Equals(item.EvidencePayloadSha256, reportDigest, StringComparison.OrdinalIgnoreCase));
                if (alreadyApplied)
                    return new CiAiRepairFeedbackResult(task, null, IsDuplicateDelivery: true);
            }

            var consumed = CiAiTestResultReportConsumer.Consume(artifact, task, now);
            var appendOutcome = journal.Append(consumed.Attempt);
            if (appendOutcome is WorkTaskRepairAttemptAppendOutcome.Conflict or
                WorkTaskRepairAttemptAppendOutcome.OutOfOrder)
                throw new InvalidOperationException($"CiAi repair attempt could not be appended: {appendOutcome}.");

            var expectation = new WorkTaskRepairEvidenceExpectation(
                consumed.Attempt.TaskId,
                consumed.Attempt.CorrelationId,
                consumed.Attempt.Repository,
                consumed.Attempt.Branch,
                consumed.Attempt.WorkflowPath,
                consumed.Attempt.CandidateSha,
                consumed.Attempt.TaskStartedAt,
                consumed.Attempt.CiRunId,
                consumed.Attempt.CiRunAttempt);

            var updated = ApplyRepairDecision(taskId, leaseId, consumed.Attempt,
                expectation, maximumDuration, now);
            return new CiAiRepairFeedbackResult(updated, consumed,
                IsDuplicateDelivery: appendOutcome == WorkTaskRepairAttemptAppendOutcome.Duplicate);
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
