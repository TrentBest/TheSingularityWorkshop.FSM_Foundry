namespace TheSingularityWorkshop.Foundry;

public sealed record WorkTaskRequest(int SchemaVersion, string TaskId, string CorrelationId,
    string Repository, int IssueNumber, string IssueUrl, string BaseSha,
    IReadOnlyList<string> AllowedPaths, int MaximumAttempts)
{
    public const int CurrentSchemaVersion = 1;

    public WorkTaskRequest Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException($"Unsupported task schema version '{SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(TaskId) || TaskId.Length > 128 ||
            string.IsNullOrWhiteSpace(CorrelationId) || CorrelationId.Length > 128 ||
            string.IsNullOrWhiteSpace(Repository) || Repository.Length > 256 ||
            TaskId.Any(char.IsControl) || CorrelationId.Any(char.IsControl))
            throw new ArgumentException("Task and correlation identifiers must be non-empty, control-free, and at most 128 characters; repository identity is required and limited to 256 characters.");
        var repositoryParts = Repository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(part =>
            string.IsNullOrWhiteSpace(part) || part.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
            throw new ArgumentException("Repository must use canonical owner/name identity.");
        if (IssueNumber <= 0 || string.IsNullOrWhiteSpace(IssueUrl) || IssueUrl.Length > 512 ||
            !Uri.TryCreate(IssueUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != $"/{repositoryParts[0]}/{repositoryParts[1]}/issues/{IssueNumber}")
            throw new ArgumentException("Issue URL must identify this repository and issue number.");
        if (BaseSha is null || BaseSha.Length != 40 || !BaseSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Base SHA must be a full 40-character hexadecimal commit.");
        if (AllowedPaths is null || AllowedPaths.Count == 0 || AllowedPaths.Count > 128 ||
            AllowedPaths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') ||
                path.Contains('\\') || path.Contains(':') ||
                path.Split('/').Any(segment => segment.Length == 0 || segment is ".." or "." ||
                    segment.Any(char.IsControl))))
            throw new ArgumentException("At least one safe, repository-relative path is required.");
        if (MaximumAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        return this with { AllowedPaths = Array.AsReadOnly(AllowedPaths.Distinct(StringComparer.Ordinal).ToArray()) };
    }
}

public enum WorkTaskStatus
{
    Queued, Claimed, Investigating, Implementing, Validating, RepairRequired,
    Blocked, CodeRed, ReviewReady, HandedOff, Stopped, Failed
}

public sealed record WorkTaskLease(string AgentId, Guid LeaseId, DateTimeOffset ExpiresAt);
public sealed record WorkTaskEvent(long Sequence, WorkTaskStatus Previous, WorkTaskStatus Current,
    string AgentId, Guid LeaseId, DateTimeOffset OccurredAt, string Reason,
    long? CiRunId = null, int? CiRunAttempt = null, string? EvidencePayloadSha256 = null,
    string? ApprovalReceiptId = null, string? ApprovalIssuer = null, string? ApprovalPrincipal = null,
    string? ApprovalIncidentId = null, long? ApprovalIncidentSequence = null,
    long? ApprovalTaskEventSequence = null, CodeRedRecoveryApprovalReceipt? ApprovalReceipt = null);
public sealed record WorkTaskSnapshot(WorkTaskRequest Request, WorkTaskStatus Status,
    WorkTaskLease? Lease, int Attempt, IReadOnlyList<WorkTaskEvent> Events);

/// <summary>
/// Deterministic in-process task lifecycle and lease-conflict reference implementation.
/// A durable GitHub-backed store and transport are host adapters, not implied here.
/// </summary>
public sealed class WorkTaskCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskState> _tasks = new(StringComparer.Ordinal);
    private readonly ICodeRedRecoveryReceiptVerifier? _recoveryReceiptVerifier;

    public WorkTaskCoordinator(ICodeRedRecoveryReceiptVerifier? recoveryReceiptVerifier = null) =>
        _recoveryReceiptVerifier = recoveryReceiptVerifier;

    public WorkTaskSnapshot Submit(WorkTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = request.Validate();
        lock (_gate)
        {
            if (_tasks.ContainsKey(validated.TaskId))
                throw new InvalidOperationException($"Task '{validated.TaskId}' already exists.");
            var state = new TaskState(validated);
            _tasks.Add(validated.TaskId, state);
            return state.Snapshot();
        }
    }

    public WorkTaskSnapshot Claim(string taskId, string agentId, TimeSpan leaseDuration, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 128 || agentId.Any(char.IsControl))
            throw new ArgumentException("Agent identity must be non-empty, control-free, and at most 128 characters.", nameof(agentId));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (now == default)
            throw new ArgumentException("Claim time is required.", nameof(now));
        lock (_gate)
        {
            var state = Get(taskId);
            if (state.Status is WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)
                throw new InvalidOperationException($"Terminal task '{taskId}' cannot be claimed.");
            if (state.Lease is { } current && current.ExpiresAt > now)
                throw new InvalidOperationException($"Task '{taskId}' is leased by '{current.AgentId}'.");
            if (state.Status == WorkTaskStatus.CodeRed)
                throw new InvalidOperationException($"Task '{taskId}' is in CODE RED and requires approved recovery before reclaim.");
            if (state.Status != WorkTaskStatus.Queued && state.Lease is null)
                throw new InvalidOperationException($"Task '{taskId}' requires recovery before reclaim.");
            var previous = state.Status;
            var lease = new WorkTaskLease(agentId, Guid.NewGuid(), now.Add(leaseDuration));
            state.Lease = lease;
            state.Status = WorkTaskStatus.Claimed;
            state.AddEvent(previous, WorkTaskStatus.Claimed, agentId, lease.LeaseId, now, "Task claimed.");
            return state.Snapshot();
        }
    }

    public WorkTaskSnapshot Transition(string taskId, Guid leaseId, WorkTaskStatus next,
        DateTimeOffset now, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2048 || reason.Any(char.IsControl))
            throw new ArgumentException("A transition reason must be non-empty, control-free, and at most 2048 characters.", nameof(reason));
        if (now == default)
            throw new ArgumentException("Transition time is required.", nameof(now));
        lock (_gate)
        {
            var state = Get(taskId);
            var lease = state.Lease;
            if (lease is null || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new InvalidOperationException("The task lease is missing, expired, or superseded.");
            if (next is WorkTaskStatus.ReviewReady or WorkTaskStatus.RepairRequired)
                throw new InvalidOperationException("Review readiness and repair authorization must be derived from exact CI evidence through ApplyRepairDecision.");
            if (!Allowed(state.Status, next))
                throw new InvalidOperationException($"Transition '{state.Status}' -> '{next}' is not allowed.");
            if (next == WorkTaskStatus.Validating && state.Attempt >= state.Request.MaximumAttempts)
                throw new InvalidOperationException("The configured validation attempt budget is exhausted.");
            if (next == WorkTaskStatus.Validating)
                state.Attempt++;
            if (next == WorkTaskStatus.RepairRequired && state.Attempt >= state.Request.MaximumAttempts)
                next = WorkTaskStatus.Blocked;
            var previous = state.Status;
            state.Status = next;
            if (next is WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)
                state.Lease = null;
            state.AddEvent(previous, next, lease.AgentId, lease.LeaseId, now, reason);
            return state.Snapshot();
        }
    }

    /// <summary>
    /// Recovers a CODE RED task only with a trusted, signed, single-use host approval receipt.
    /// Human authentication remains in the host; receipt verification and lifecycle consumption
    /// are serialized under the task lock.
    /// </summary>
    public WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, CodeRedRecoveryApprovalReceipt receipt,
        DateTimeOffset now, string reason)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(receipt);
        incident.Validate();
        receipt.Validate();
        if (now == default)
            throw new ArgumentException("Recovery time is required.", nameof(now));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2048 || reason.Any(char.IsControl))
            throw new ArgumentException("A recovery reason must be non-empty, control-free, and at most 2048 characters.", nameof(reason));

        lock (_gate)
        {
            var state = Get(taskId);
            var lease = state.Lease;
            if (state.Status != WorkTaskStatus.CodeRed)
                throw new InvalidOperationException("Only a task currently in CODE RED can use the recovery receipt.");
            if (lease is null || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new InvalidOperationException("The task lease is missing, expired, or superseded.");

            var snapshot = state.Snapshot();
            if (incident.Status != CodeRedIncidentStatus.Active ||
                incident.TaskId != state.Request.TaskId ||
                !string.Equals(incident.Repository, state.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
                incident.CorrelationId != state.Request.CorrelationId ||
                incident.AgentId != lease.AgentId)
                throw new UnauthorizedAccessException("The active CODE RED incident does not match the task and lease.");

            if (receipt.TaskId != state.Request.TaskId ||
                receipt.IncidentId != incident.IncidentId ||
                receipt.TaskEventSequence != snapshot.Events.Count ||
                receipt.IncidentSequence != incident.Sequence ||
                receipt.Action != CodeRedRecoveryAction.ResumeInvestigation ||
                receipt.IssuedAt > now || receipt.ExpiresAt <= now ||
                receipt.IssuedAt < incident.StartedAt || receipt.IssuedAt < incident.UpdatedAt ||
                snapshot.Events.Count == 0 || receipt.IssuedAt < snapshot.Events[^1].OccurredAt ||
                now < snapshot.Events[^1].OccurredAt)
                throw new UnauthorizedAccessException("The approval receipt is expired or is not bound to the current task, incident, sequence, or recovery action.");

            if (snapshot.Events.Any(item => string.Equals(item.ApprovalReceiptId,
                    receipt.ReceiptId, StringComparison.Ordinal)))
                throw new UnauthorizedAccessException("The approval receipt has already been consumed.");

            var verifier = _recoveryReceiptVerifier;
            if (verifier is null || !verifier.IsTrustedIssuer(receipt.Issuer) ||
                !verifier.VerifySignature(receipt.Issuer, receipt.GetSigningPayload(), receipt.Signature))
                throw new UnauthorizedAccessException("The CODE RED recovery receipt issuer or signature is not trusted.");

            var previous = state.Status;
            state.Status = WorkTaskStatus.Investigating;
            state.AddEvent(previous, WorkTaskStatus.Investigating, lease.AgentId, lease.LeaseId,
                now, reason, approvalReceiptId: receipt.ReceiptId,
                approvalIssuer: receipt.Issuer, approvalPrincipal: receipt.Principal,
                approvalIncidentId: receipt.IncidentId,
                approvalIncidentSequence: receipt.IncidentSequence,
                approvalTaskEventSequence: receipt.TaskEventSequence,
                approvalReceipt: receipt);
            return state.Snapshot();
        }
    }

    /// <summary>
    /// Applies a bounded lifecycle decision derived from the exact CI attempt and its validated history.
    /// Caller-supplied evidence is re-evaluated here; a status transition alone cannot assert CI success.
    /// </summary>
    public WorkTaskSnapshot ApplyRepairDecision(string taskId, Guid leaseId,
        IReadOnlyList<WorkTaskRepairAttempt> previousAttempts, WorkTaskRepairAttempt currentAttempt,
        WorkTaskRepairEvidenceExpectation expectation, TimeSpan maximumDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previousAttempts);
        ArgumentNullException.ThrowIfNull(currentAttempt);
        ArgumentNullException.ThrowIfNull(expectation);
        if (now == default)
            throw new ArgumentException("Decision time is required.", nameof(now));
        lock (_gate)
        {
            var state = Get(taskId);
            var lease = state.Lease;
            if (lease is null || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new InvalidOperationException("The task lease is missing, expired, or superseded.");
            if (state.Status is not (WorkTaskStatus.Validating or WorkTaskStatus.CodeRed))
                throw new InvalidOperationException("CI evidence can be applied only while the task is validating or under CODE RED review.");
            if (currentAttempt.AttemptNumber != state.Attempt ||
                currentAttempt.TaskId != state.Request.TaskId ||
                currentAttempt.CorrelationId != state.Request.CorrelationId ||
                !string.Equals(currentAttempt.Repository, state.Request.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CI evidence does not identify the current task validation attempt.");
            if (expectation.TaskId != state.Request.TaskId ||
                expectation.CorrelationId != state.Request.CorrelationId ||
                !string.Equals(expectation.Repository, state.Request.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The CI evidence expectation does not identify the current task.");
            var decision = WorkTaskRepairDecisionPolicy.Evaluate(previousAttempts, currentAttempt,
                expectation, state.Request.MaximumAttempts, maximumDuration, now);
            var next = decision.Decision switch
            {
                WorkTaskRepairDecision.ReviewReady => WorkTaskStatus.ReviewReady,
                WorkTaskRepairDecision.RepairRequired => WorkTaskStatus.RepairRequired,
                WorkTaskRepairDecision.Blocked => WorkTaskStatus.Blocked,
                WorkTaskRepairDecision.CodeRed => WorkTaskStatus.CodeRed,
                _ => throw new InvalidOperationException("Unknown repair decision.")
            };
            if (!Allowed(state.Status, next))
                throw new InvalidOperationException($"Evidence decision '{state.Status}' -> '{next}' is not allowed.");
            var previous = state.Status;
            state.Status = next;
            if (next == WorkTaskStatus.Blocked)
                state.Lease = null;
            state.AddEvent(previous, next, lease.AgentId, lease.LeaseId, now,
                $"{decision.ReasonCode}: {decision.Reason}", currentAttempt.CiRunId,
                currentAttempt.CiRunAttempt, currentAttempt.EvidencePayloadSha256);
            return state.Snapshot();
        }
    }

    /// <summary>Rehydrates a task from a validated durable snapshot before dispatch starts.</summary>
    public void Restore(WorkTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        JsonlWorkTaskStore.ValidateSnapshot(snapshot);

        lock (_gate)
        {
            if (_tasks.ContainsKey(snapshot.Request.TaskId))
                throw new InvalidOperationException($"Task '{snapshot.Request.TaskId}' already exists.");
            _tasks.Add(snapshot.Request.TaskId, TaskState.Restore(snapshot));
        }
    }

    public WorkTaskSnapshot GetSnapshot(string taskId)
    {
        lock (_gate) return Get(taskId).Snapshot();
    }

    private TaskState Get(string taskId) => _tasks.TryGetValue(taskId, out var state)
        ? state : throw new KeyNotFoundException($"Unknown task '{taskId}'.");

    internal static bool IsAllowedTransition(WorkTaskStatus current, WorkTaskStatus next) => Allowed(current, next);

    internal static bool HasApprovalMetadata(WorkTaskEvent item) =>
        item.ApprovalReceiptId is not null || item.ApprovalIssuer is not null ||
        item.ApprovalPrincipal is not null || item.ApprovalIncidentId is not null ||
        item.ApprovalIncidentSequence.HasValue || item.ApprovalTaskEventSequence.HasValue ||
        item.ApprovalReceipt is not null;

    internal static bool IsValidApprovedRecoveryEvent(WorkTaskEvent item) =>
        item.Previous == WorkTaskStatus.CodeRed &&
        item.Current == WorkTaskStatus.Investigating &&
        !string.IsNullOrWhiteSpace(item.ApprovalReceiptId) && item.ApprovalReceiptId.Length <= 128 &&
        !item.ApprovalReceiptId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(item.ApprovalIssuer) && item.ApprovalIssuer.Length <= 128 &&
        !item.ApprovalIssuer.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(item.ApprovalPrincipal) && item.ApprovalPrincipal.Length <= 128 &&
        !item.ApprovalPrincipal.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(item.ApprovalIncidentId) && item.ApprovalIncidentId.Length <= 128 &&
        !item.ApprovalIncidentId.Any(char.IsControl) &&
        item.ApprovalIncidentSequence is > 0 &&
        item.ApprovalTaskEventSequence == item.Sequence - 1 &&
        item.ApprovalReceipt is { } receipt && receipt.Validate() == receipt &&
        receipt.ReceiptId == item.ApprovalReceiptId && receipt.Issuer == item.ApprovalIssuer &&
        receipt.Principal == item.ApprovalPrincipal && receipt.IncidentId == item.ApprovalIncidentId &&
        receipt.IncidentSequence == item.ApprovalIncidentSequence &&
        receipt.TaskEventSequence == item.ApprovalTaskEventSequence &&
        receipt.TaskId.Length > 0 && receipt.Action == CodeRedRecoveryAction.ResumeInvestigation;

    private static bool Allowed(WorkTaskStatus current, WorkTaskStatus next) => (current, next) switch
    {
        (WorkTaskStatus.Claimed, WorkTaskStatus.Investigating or WorkTaskStatus.Blocked or WorkTaskStatus.CodeRed or WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.Investigating, WorkTaskStatus.Implementing or WorkTaskStatus.Blocked or WorkTaskStatus.CodeRed or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.Implementing, WorkTaskStatus.Validating or WorkTaskStatus.Blocked or WorkTaskStatus.CodeRed or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.Validating, WorkTaskStatus.RepairRequired or WorkTaskStatus.ReviewReady or WorkTaskStatus.Blocked or WorkTaskStatus.CodeRed or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.RepairRequired, WorkTaskStatus.Implementing or WorkTaskStatus.Blocked or WorkTaskStatus.CodeRed or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.Blocked, WorkTaskStatus.Claimed or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.CodeRed, WorkTaskStatus.Blocked or WorkTaskStatus.Stopped or WorkTaskStatus.Failed) => true,
        (WorkTaskStatus.ReviewReady, WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped) => true,
        _ => false
    };

    private sealed class TaskState(WorkTaskRequest request)
    {
        private readonly List<WorkTaskEvent> _events = new();
        private TaskState(WorkTaskSnapshot snapshot) : this(snapshot.Request.Validate())
        {
            Request = snapshot.Request.Validate();
            Status = snapshot.Status;
            Lease = snapshot.Lease;
            Attempt = snapshot.Attempt;
            _events.AddRange(snapshot.Events);
        }
        public static TaskState Restore(WorkTaskSnapshot snapshot) => new(snapshot);
        public WorkTaskRequest Request { get; } = request;
        public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Queued;
        public WorkTaskLease? Lease { get; set; }
        public int Attempt { get; set; }
        public void AddEvent(WorkTaskStatus previous, WorkTaskStatus current, string agent, Guid lease,
            DateTimeOffset at, string reason, long? ciRunId = null, int? ciRunAttempt = null,
            string? evidencePayloadSha256 = null, string? approvalReceiptId = null,
            string? approvalIssuer = null, string? approvalPrincipal = null,
            string? approvalIncidentId = null, long? approvalIncidentSequence = null,
            long? approvalTaskEventSequence = null, CodeRedRecoveryApprovalReceipt? approvalReceipt = null) =>
            _events.Add(new WorkTaskEvent(_events.Count + 1, previous, current, agent, lease, at,
                reason, ciRunId, ciRunAttempt, evidencePayloadSha256, approvalReceiptId,
                approvalIssuer, approvalPrincipal, approvalIncidentId, approvalIncidentSequence,
                approvalTaskEventSequence, approvalReceipt));
        public WorkTaskSnapshot Snapshot() =>
            new(Request, Status, Lease, Attempt, Array.AsReadOnly(_events.ToArray()));
    }
}
