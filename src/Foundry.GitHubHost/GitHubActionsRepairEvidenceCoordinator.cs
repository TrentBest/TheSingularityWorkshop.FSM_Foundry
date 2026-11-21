using System.Security.Cryptography;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

public enum GitHubActionsRepairEvidenceStatus
{
    RunNotYetCorrelated,
    RequiredJobsNotSuccessful,
    Applied,
    AlreadyApplied
}

public sealed record GitHubActionsRepairEvidenceResult(
    GitHubActionsRepairEvidenceStatus Status,
    WorkTaskSnapshot Task,
    GitHubActionsRunCorrelation? Correlation,
    GitHubActionsAttemptEvidence? AttemptEvidence,
    WorkTaskRepairAttempt? RepairAttempt);

/// <summary>
/// Host lifecycle bridge from an operation-bound GitHub Actions dispatch to durable Foundry
/// validation evidence. Only a successful exact attempt with verified candidate-checkout
/// provenance can produce a successful WorkTaskRepairAttempt and transition the task.
/// </summary>
public sealed class GitHubActionsRepairEvidenceCoordinator
{
    private const string EvidenceSchema = "foundry.github-actions-repair-evidence.v1";
    private readonly GitHubActionsWorkflowDispatcher _workflows;
    private readonly GitHubActionsArtifactProvenanceReader _provenance;
    private readonly IGitHubActionsDispatchJournal _dispatchJournal;
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly JsonlWorkTaskRepairAttemptJournal _attempts;
    private readonly string[] _requiredJobs;
    private readonly TimeSpan _maximumDuration;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubActionsRepairEvidenceCoordinator(
        GitHubActionsWorkflowDispatcher workflows,
        GitHubActionsArtifactProvenanceReader provenance,
        IGitHubActionsDispatchJournal dispatchJournal,
        PersistentWorkTaskCoordinator tasks,
        JsonlWorkTaskRepairAttemptJournal attempts,
        IReadOnlyCollection<string> requiredJobNames,
        TimeSpan maximumDuration,
        Func<DateTimeOffset>? clock = null)
    {
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _dispatchJournal = dispatchJournal ?? throw new ArgumentNullException(nameof(dispatchJournal));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        ArgumentNullException.ThrowIfNull(requiredJobNames);
        _requiredJobs = requiredJobNames.ToArray();
        if (_requiredJobs.Length == 0 ||
            _requiredJobs.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)) ||
            _requiredJobs.Distinct(StringComparer.Ordinal).Count() != _requiredJobs.Length)
            throw new ArgumentException("At least one unique, bounded required job name is required.", nameof(requiredJobNames));
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _maximumDuration = maximumDuration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reconciles the dispatch identified by its durable repository-operation ID. Missing
    /// runs and unsuccessful required jobs remain non-actionable; no task transition occurs.
    /// Successful evidence is appended before the task decision so a restart can safely retry
    /// the decision from the same exact journaled attempt.
    /// </summary>
    public async ValueTask<GitHubActionsRepairEvidenceResult> ReconcileAndApplyAsync(
        string operationId, Guid leaseId, CancellationToken cancellationToken = default)
    {
        var receipt = _workflows.GetDispatchReceiptForOperation(operationId);
        var task = _tasks.GetSnapshot(receipt.TaskId);
        var dispatch = _dispatchJournal.Get(receipt.RequestId);

        if (task.Status == WorkTaskStatus.ReviewReady)
        {
            var latest = _attempts.GetAttempts(receipt.TaskId).LastOrDefault();
            if (dispatch.State == GitHubActionsDispatchState.Verified &&
                dispatch.RunId.HasValue && dispatch.RunAttempt.HasValue &&
                latest is not null && latest.AttemptNumber == task.Attempt &&
                latest.CiRunId == dispatch.RunId.Value &&
                latest.CiRunAttempt == dispatch.RunAttempt.Value &&
                latest.CandidateSha.Equals(receipt.CandidateSha, StringComparison.OrdinalIgnoreCase))
                return new(GitHubActionsRepairEvidenceStatus.AlreadyApplied, task, null, null, latest);
            throw new InvalidOperationException("ReviewReady task does not have matching durable verified GitHub evidence.");
        }

        var initialNow = _clock();
        DemandValidatingTask(task, receipt, leaseId, initialNow);

        // Recover the append-success / task-decision-not-applied crash window before
        // consulting GitHub again. The journal record is reusable only when every
        // immutable task, dispatch, run, workflow, candidate, and success field matches.
        if (task.Events.Count == 0)
            throw new InvalidOperationException("A validating task must have durable lifecycle timestamps.");
        var priorDurableAttempt = _attempts.GetAttempts(receipt.TaskId)
            .FirstOrDefault(item => item.AttemptNumber == task.Attempt);
        if (priorDurableAttempt is not null)
        {
            if (dispatch.State != GitHubActionsDispatchState.Verified ||
                dispatch.RequestId != receipt.RequestId || dispatch.TaskId != task.Request.TaskId ||
                !string.Equals(dispatch.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
                dispatch.WorkflowPath != receipt.WorkflowPath || dispatch.Branch != receipt.Branch ||
                !string.Equals(dispatch.CandidateSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                dispatch.CorrelationId != task.Request.CorrelationId ||
                !dispatch.RunId.HasValue || !dispatch.RunAttempt.HasValue ||
                priorDurableAttempt.TaskId != task.Request.TaskId ||
                priorDurableAttempt.CorrelationId != task.Request.CorrelationId ||
                !string.Equals(priorDurableAttempt.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
                priorDurableAttempt.Branch != receipt.Branch || priorDurableAttempt.WorkflowPath != receipt.WorkflowPath ||
                priorDurableAttempt.TaskStartedAt != task.Events[0].OccurredAt ||
                !string.Equals(priorDurableAttempt.CandidateSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                priorDurableAttempt.CiRunId != dispatch.RunId.Value ||
                priorDurableAttempt.CiRunAttempt != dispatch.RunAttempt.Value ||
                priorDurableAttempt.StartedAt != receipt.DispatchedAt ||
                !priorDurableAttempt.EvidenceComplete || !priorDurableAttempt.IsSuccessful ||
                priorDurableAttempt.HasActionableDiagnostics || priorDurableAttempt.DiagnosticsFingerprint is not null)
                throw new InvalidDataException("A durable repair attempt exists but does not exactly match the verified dispatch and current task.");

            var replayExpectation = new WorkTaskRepairEvidenceExpectation(
                task.Request.TaskId, task.Request.CorrelationId, task.Request.Repository,
                receipt.Branch, receipt.WorkflowPath, priorDurableAttempt.CandidateSha,
                priorDurableAttempt.TaskStartedAt, priorDurableAttempt.CiRunId, priorDurableAttempt.CiRunAttempt);
            var replayedTask = _tasks.ApplyRepairDecision(
                task.Request.TaskId, leaseId, priorDurableAttempt, replayExpectation, _maximumDuration, initialNow);
            return new(GitHubActionsRepairEvidenceStatus.Applied, replayedTask, null, null, priorDurableAttempt);
        }

        var correlation = await _workflows.FindCorrelatedRunAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (correlation is null)
            return new(GitHubActionsRepairEvidenceStatus.RunNotYetCorrelated, task, null, null, null);

        var attemptEvidence = await _workflows.VerifyRequiredJobsAsync(
            receipt, correlation, _requiredJobs, cancellationToken).ConfigureAwait(false);
        if (!attemptEvidence.AllRequiredJobsSucceeded)
            return new(GitHubActionsRepairEvidenceStatus.RequiredJobsNotSuccessful,
                _tasks.GetSnapshot(receipt.TaskId), correlation, attemptEvidence, null);

        var provenance = await _provenance.RetrieveAndVerifyAsync(
            receipt, correlation, cancellationToken).ConfigureAwait(false);

        var now = _clock();
        task = _tasks.GetSnapshot(receipt.TaskId);
        DemandValidatingTask(task, receipt, leaseId, now);
        if (task.Events.Count == 0)
            throw new InvalidOperationException("A validating task must have durable lifecycle timestamps.");

        var taskStartedAt = task.Events[0].OccurredAt;
        if (receipt.DispatchedAt < taskStartedAt || now < receipt.DispatchedAt)
            throw new InvalidDataException("Dispatch and task timestamps are not ordered.");

        var evidenceDigest = CreateEvidenceDigest(provenance, attemptEvidence);
        var repairAttempt = new WorkTaskRepairAttempt(
            WorkTaskRepairAttempt.CurrentSchemaVersion,
            task.Request.TaskId,
            task.Request.CorrelationId,
            task.Request.Repository,
            receipt.Branch,
            receipt.WorkflowPath,
            taskStartedAt,
            task.Attempt,
            provenance.CheckedOutHead,
            provenance.RunId,
            provenance.RunAttempt,
            evidenceDigest,
            EvidenceComplete: true,
            IsSuccessful: true,
            HasActionableDiagnostics: false,
            DiagnosticsFingerprint: null,
            StartedAt: receipt.DispatchedAt,
            ObservedAt: now).Validate();

        var append = _attempts.AppendOrGetExactReplay(repairAttempt, out var durableAttempt);
        if (append is not (WorkTaskRepairAttemptAppendOutcome.Appended or
            WorkTaskRepairAttemptAppendOutcome.Replayed or WorkTaskRepairAttemptAppendOutcome.Duplicate))
            throw new InvalidOperationException($"Verified CI evidence could not be appended or replayed exactly: {append}.");

        // The journal's original record is authoritative. In the append-success / decision-
        // failure crash window, a retry has a new observation time; never replace the durable
        // timestamp or any other identity/evidence field with reconstructed values.
        repairAttempt = durableAttempt;

        var expectation = new WorkTaskRepairEvidenceExpectation(
            task.Request.TaskId,
            task.Request.CorrelationId,
            task.Request.Repository,
            receipt.Branch,
            receipt.WorkflowPath,
            provenance.CheckedOutHead,
            taskStartedAt,
            provenance.RunId,
            provenance.RunAttempt);

        var updated = _tasks.ApplyRepairDecision(
            task.Request.TaskId, leaseId, repairAttempt, expectation, _maximumDuration, now);

        return new(GitHubActionsRepairEvidenceStatus.Applied, updated, correlation, attemptEvidence, repairAttempt);
    }

    private static void DemandValidatingTask(
        WorkTaskSnapshot task, GitHubActionsDispatchReceipt receipt, Guid leaseId, DateTimeOffset now)
    {
        if (task.Status != WorkTaskStatus.Validating ||
            task.Lease is not { } lease || lease.LeaseId != leaseId || lease.ExpiresAt <= now ||
            task.Attempt < 1 ||
            task.Request.TaskId != receipt.TaskId ||
            !string.Equals(task.Request.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
            task.Request.CorrelationId.Length == 0)
            throw new UnauthorizedAccessException("GitHub evidence can be applied only to the matching task's current live validation lease.");
    }

    private static string CreateEvidenceDigest(
        VerifiedCandidateCheckoutProvenance provenance, GitHubActionsAttemptEvidence evidence)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = EvidenceSchema,
            provenance.PayloadSha256,
            provenance.RequestId,
            provenance.Repository,
            provenance.RunId,
            provenance.RunAttempt,
            provenance.CandidateRef,
            provenance.RequestedSha,
            provenance.CheckedOutHead,
            jobs = evidence.RequiredJobs.OrderBy(job => job.Name, StringComparer.Ordinal)
                .Select(job => new { job.Name, job.Status, job.Conclusion, job.Succeeded }).ToArray()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
