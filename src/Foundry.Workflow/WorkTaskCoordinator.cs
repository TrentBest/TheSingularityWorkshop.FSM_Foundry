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
    string? RecoveryIncidentId = null, long? RecoveryIncidentSequence = null);
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
    /// Applies a recovery transition after the host-owned approval gate has authorized the
    /// exact incident/task snapshot. This method is internal so host callers use the
    /// PersistentCodeRedRecoveryCoordinator approval boundary.
    /// </summary>
    internal WorkTaskSnapshot RecoverFromCodeRed(string taskId, Guid leaseId,
        CodeRedIncident incident, long expectedTaskEventSequence, DateTimeOffset now, string reason)
    {
        ArgumentNullException.ThrowIfNull(incident);
        incident.Validate();
        if (now == default)
            throw new ArgumentException("Recovery time is required.", nameof(now));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2048 || reason.Any(char.IsControl))
            throw new ArgumentException("A recovery reason must be non-empty, control-free, and at most 2048 characters.", nameof(reason));

        lock (_gate)
        {
            var state = Get(taskId);
            var lease = state.Lease;
            if (state.Status != WorkTaskStatus.CodeRed)
                throw new InvalidOperationException("Only a task currently in CODE RED can be recovered.");
            if (lease is null || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
                throw new InvalidOperationException("The task lease is missing, expired, or superseded.");

            var snapshot = state.Snapshot();
            if (snapshot.Events.Count != expectedTaskEventSequence)
                throw new UnauthorizedAccessException("The task changed after the host evaluated its recovery approval; obtain approval for the current task state.");
            if (incident.Status != CodeRedIncidentStatus.Active ||
                incident.TaskId != state.Request.TaskId ||
                !string.Equals(incident.Repository, state.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
                incident.CorrelationId != state.Request.CorrelationId ||
                incident.AgentId != lease.AgentId)
                throw new UnauthorizedAccessException("The active CODE RED incident does not match the task and lease.");
            if (snapshot.Events.Count == 0 || now < snapshot.Events[^1].OccurredAt)
                throw new ArgumentException("Recovery time must not precede the latest task event.", nameof(now));

            var previous = state.Status;
            state.Status = WorkTaskStatus.Investigating;
            state.AddEvent(previous, WorkTaskStatus.Investigating, lease.AgentId, lease.LeaseId,
                now, reason, recoveryIncidentId: incident.IncidentId,
                recoveryIncidentSequence: incident.Sequence);
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

    internal static bool HasRecoveryMetadata(WorkTaskEvent item) =>
        item.RecoveryIncidentId is not null || item.RecoveryIncidentSequence.HasValue;

    internal static bool IsValidCodeRedRecoveryEvent(WorkTaskEvent item) =>
        item.Previous == WorkTaskStatus.CodeRed &&
        item.Current == WorkTaskStatus.Investigating &&
        !string.IsNullOrWhiteSpace(item.RecoveryIncidentId) &&
        item.RecoveryIncidentId.Length <= 128 &&
        !item.RecoveryIncidentId.Any(char.IsControl) &&
        item.RecoveryIncidentSequence is > 0;

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
            string? evidencePayloadSha256 = null, string? recoveryIncidentId = null,
            long? recoveryIncidentSequence = null) =>
            _events.Add(new WorkTaskEvent(_events.Count + 1, previous, current, agent, lease, at,
                reason, ciRunId, ciRunAttempt, evidencePayloadSha256, recoveryIncidentId,
                recoveryIncidentSequence));
        public WorkTaskSnapshot Snapshot() =>
            new(Request, Status, Lease, Attempt, Array.AsReadOnly(_events.ToArray()));
    }
}
