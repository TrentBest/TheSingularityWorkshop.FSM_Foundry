namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// A versioned, read-only view of one authoritative work-task snapshot.
/// This projection carries display/provenance facts only; it cannot issue commands,
/// grant capabilities, advance lifecycle state, or authorize repository operations.
/// </summary>
public sealed record AutonomousDevelopmentStatusProjection(
    int SchemaVersion,
    string TaskId,
    string CorrelationId,
    string Repository,
    int IssueNumber,
    string IssueUrl,
    string BaseSha,
    WorkTaskStatus Status,
    int Attempt,
    int MaximumAttempts,
    string? AgentId,
    DateTimeOffset? LeaseExpiresAt,
    bool LeaseActive,
    long EventSequence,
    DateTimeOffset? LastTransitionAt,
    string? LastTransitionReason,
    long? CiRunId,
    int? CiRunAttempt,
    string? EvidencePayloadSha256,
    AutonomousDevelopmentApprovalProjection? LatestApproval)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Builds a display projection from one validated authoritative snapshot.</summary>
    public static AutonomousDevelopmentStatusProjection Create(
        WorkTaskSnapshot snapshot, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Request);
        ArgumentNullException.ThrowIfNull(snapshot.Events);

        if (observedAt == default)
            throw new ArgumentException("Projection observation time is required.", nameof(observedAt));

        var request = snapshot.Request.Validate();
        if (!Enum.IsDefined(snapshot.Status) || snapshot.Attempt < 0 ||
            snapshot.Attempt > request.MaximumAttempts)
            throw new ArgumentException("Task snapshot status or attempt count is invalid.", nameof(snapshot));

        var previousStatus = WorkTaskStatus.Queued;
        DateTimeOffset? previousTime = null;
        var validationAttempts = 0;
        WorkTaskEvent? latest = null;
        WorkTaskEvent? latestApprovalEvent = null;

        for (var index = 0; index < snapshot.Events.Count; index++)
        {
            var item = snapshot.Events[index];
            if (item is null || item.Sequence != index + 1 ||
                item.Previous != previousStatus || item.Current == previousStatus ||
                !Enum.IsDefined(item.Previous) || !Enum.IsDefined(item.Current) ||
                item.OccurredAt == default ||
                (previousTime.HasValue && item.OccurredAt < previousTime.Value) ||
                string.IsNullOrWhiteSpace(item.AgentId) || item.LeaseId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Reason) ||
                (item.CiRunId.HasValue != item.CiRunAttempt.HasValue) ||
                (item.CiRunId.HasValue != (item.EvidencePayloadSha256 is not null)))
                throw new ArgumentException("Task event history is discontinuous or invalid.", nameof(snapshot));

            if (item.Current == WorkTaskStatus.Validating)
                validationAttempts++;
            if (WorkTaskCoordinator.HasApprovalMetadata(item))
            {
                if (!WorkTaskCoordinator.IsValidApprovedRecoveryEvent(item) ||
                    item.ApprovalReceipt?.TaskId != request.TaskId)
                    throw new ArgumentException("Task approval provenance is invalid.", nameof(snapshot));
                latestApprovalEvent = item;
            }

            previousStatus = item.Current;
            previousTime = item.OccurredAt;
            latest = item;
        }

        if (snapshot.Events.Count == 0)
        {
            if (snapshot.Status != WorkTaskStatus.Queued || snapshot.Lease is not null ||
                snapshot.Attempt != 0)
                throw new ArgumentException("An event-free snapshot must be queued and untouched.", nameof(snapshot));
        }
        else if (snapshot.Status != previousStatus || snapshot.Attempt != validationAttempts)
        {
            throw new ArgumentException("Snapshot state does not match its event history.", nameof(snapshot));
        }

        if (snapshot.Lease is { } lease)
        {
            if (string.IsNullOrWhiteSpace(lease.AgentId) || lease.LeaseId == Guid.Empty ||
                lease.ExpiresAt == default || latest is null ||
                lease.AgentId != latest.AgentId || lease.LeaseId != latest.LeaseId ||
                lease.ExpiresAt <= latest.OccurredAt ||
                snapshot.Status is WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)
                throw new ArgumentException("Active lease does not match the authoritative task history.", nameof(snapshot));
        }
        else if (snapshot.Events.Count > 0 &&
                 snapshot.Status is not (WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed or WorkTaskStatus.Blocked))
        {
            throw new ArgumentException("A non-terminal task must retain its lease.", nameof(snapshot));
        }

        var approval = latestApprovalEvent is null ? null :
            new AutonomousDevelopmentApprovalProjection(
                latestApprovalEvent.ApprovalReceiptId!,
                latestApprovalEvent.ApprovalIssuer!,
                latestApprovalEvent.ApprovalPrincipal!,
                latestApprovalEvent.ApprovalIncidentId!,
                latestApprovalEvent.ApprovalIncidentSequence!.Value,
                latestApprovalEvent.ApprovalTaskEventSequence!.Value,
                latestApprovalEvent.OccurredAt);

        return new AutonomousDevelopmentStatusProjection(
            CurrentSchemaVersion, request.TaskId, request.CorrelationId, request.Repository,
            request.IssueNumber, request.IssueUrl, request.BaseSha, snapshot.Status,
            snapshot.Attempt, request.MaximumAttempts, snapshot.Lease?.AgentId,
            snapshot.Lease?.ExpiresAt, snapshot.Lease is { } active && active.ExpiresAt > observedAt,
            latest?.Sequence ?? 0, latest?.OccurredAt, latest?.Reason,
            latest?.CiRunId, latest?.CiRunAttempt, latest?.EvidencePayloadSha256, approval);
    }
}

/// <summary>Non-secret summary of the latest consumed human-approval receipt.</summary>
public sealed record AutonomousDevelopmentApprovalProjection(
    string ReceiptId,
    string Issuer,
    string Principal,
    string IncidentId,
    long IncidentSequence,
    long TaskEventSequence,
    DateTimeOffset ConsumedAt);
