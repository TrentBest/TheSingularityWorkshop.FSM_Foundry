namespace TheSingularityWorkshop.Foundry;

/// <summary>Explicit host-approved operations. Privileged release and merge operations are intentionally absent.</summary>
public enum WorkTaskAction
{
    ReadRepository = 0,
    WriteFiles = 1,
    CreateBranch = 2,
    CreateCommit = 3,
    RequestCiRun = 4,
    CreateDraftPullRequest = 5
}

/// <summary>Opaque, short-lived authorization grant bound to one active agent lease and candidate scope.</summary>
public sealed record WorkTaskCapability(
    Guid CapabilityId,
    string TaskId,
    string Repository,
    string AgentId,
    Guid LeaseId,
    string Branch,
    string BaseSha,
    string CandidateSha,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<WorkTaskAction> AllowedActions,
    long TaskEventSequence,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Host-owned in-memory capability registry. Keep this object and capability IDs on the host side;
/// model output and task envelopes cannot issue or expand capabilities.
/// </summary>
public sealed class WorkTaskCapabilityRegistry
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorkTaskCapability> _capabilities = new();

    /// <summary>Issues a capability only for a currently leased task and a host-approved subset of its scope.</summary>
    public WorkTaskCapability Grant(WorkTaskSnapshot task, string branch, string candidateSha,
        IReadOnlyList<string> allowedPaths, IReadOnlyCollection<WorkTaskAction> allowedActions,
        DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(task.Request);
        ArgumentNullException.ThrowIfNull(task.Events);
        var request = task.Request.Validate();
        if (task.Lease is not { } lease || lease.ExpiresAt <= now ||
            task.Status is WorkTaskStatus.Queued or WorkTaskStatus.Blocked or WorkTaskStatus.ReviewReady or
                WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)
            throw new UnauthorizedAccessException("A live agent lease on an active task is required to issue capabilities.");
        if (string.IsNullOrWhiteSpace(lease.AgentId) || lease.LeaseId == Guid.Empty)
            throw new ArgumentException("Task lease identity is invalid.", nameof(task));
        ValidateBranch(branch);
        ValidateSha(candidateSha, nameof(candidateSha));
        ArgumentNullException.ThrowIfNull(allowedPaths);
        ArgumentNullException.ThrowIfNull(allowedActions);
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Capability lifetime must be positive and no longer than one hour.");
        if (now == default)
            throw new ArgumentException("Capability issue time is required.", nameof(now));
        if (allowedPaths.Count == 0 || allowedPaths.Any(path => !IsSafePath(path)))
            throw new ArgumentException("At least one safe repository-relative path is required.", nameof(allowedPaths));
        var normalizedPaths = allowedPaths.Distinct(StringComparer.Ordinal).ToArray();
        if (normalizedPaths.Any(path => !request.AllowedPaths.Any(scope => IsWithinScope(path, scope))))
            throw new ArgumentException("Capability paths must be contained within the task's authorized path scope.", nameof(allowedPaths));
        if (allowedActions.Count == 0 || allowedActions.Any(action => !Enum.IsDefined(action)))
            throw new ArgumentException("At least one recognized action is required.", nameof(allowedActions));
        var normalizedActions = allowedActions.Distinct().Order().ToArray();
        if (task.Status == WorkTaskStatus.CodeRed &&
            normalizedActions.Any(action => action != WorkTaskAction.ReadRepository))
            throw new UnauthorizedAccessException("CODE RED permits read-only repository access; mutating capabilities are suspended.");

        var capability = new WorkTaskCapability(Guid.NewGuid(), request.TaskId, request.Repository,
            lease.AgentId, lease.LeaseId, branch, request.BaseSha, candidateSha,
            Array.AsReadOnly(normalizedPaths), Array.AsReadOnly(normalizedActions),
            task.Events.Count == 0 ? 0 : task.Events[^1].Sequence,
            now, now.Add(lifetime) < lease.ExpiresAt ? now.Add(lifetime) : lease.ExpiresAt);
        lock (_gate)
            _capabilities.Add(capability.CapabilityId, capability);
        return capability;
    }

    /// <summary>Throws unless the exact operation is within a live host-issued capability and agent lease.</summary>
    public void Demand(Guid capabilityId, WorkTaskSnapshot currentTask, string branch,
        string candidateSha, WorkTaskAction action, string? path, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        ArgumentNullException.ThrowIfNull(currentTask.Events);
        lock (_gate)
        {
            if (!_capabilities.TryGetValue(capabilityId, out var capability))
                throw new UnauthorizedAccessException("Capability is unknown or has been revoked.");
            if (now < capability.IssuedAt || now >= capability.ExpiresAt)
                throw new UnauthorizedAccessException("Capability is not currently valid.");
            if (currentTask.Request is null || currentTask.Lease is not { } currentLease ||
                currentLease.ExpiresAt <= now ||
                currentTask.Status is WorkTaskStatus.Queued or WorkTaskStatus.Blocked or WorkTaskStatus.ReviewReady or
                    WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)
                throw new UnauthorizedAccessException("The task no longer has an active agent lease.");
            if (currentTask.Events.Any(item => item.Sequence > capability.TaskEventSequence &&
                item.Current == WorkTaskStatus.CodeRed))
                throw new UnauthorizedAccessException("Capability was invalidated by a CODE RED transition; host-approved recovery requires a fresh grant.");
            if (currentTask.Events.Any(item => item.Sequence > capability.TaskEventSequence))
                throw new UnauthorizedAccessException("Capability was invalidated by a task lifecycle transition; a fresh grant is required for the new phase.");
            if (currentTask.Status == WorkTaskStatus.CodeRed && action != WorkTaskAction.ReadRepository)
                throw new UnauthorizedAccessException("CODE RED permits read-only repository access; mutating operations are suspended.");
            if (!string.Equals(currentTask.Request.TaskId, capability.TaskId, StringComparison.Ordinal) ||
                !string.Equals(currentTask.Request.Repository, capability.Repository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentLease.AgentId, capability.AgentId, StringComparison.Ordinal) ||
                currentLease.LeaseId != capability.LeaseId ||
                !string.Equals(currentTask.Request.BaseSha, capability.BaseSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(branch, capability.Branch, StringComparison.Ordinal) ||
                !string.Equals(candidateSha, capability.CandidateSha, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Operation provenance or active lease does not match the capability.");
            if (!Enum.IsDefined(action) || !capability.AllowedActions.Contains(action))
                throw new UnauthorizedAccessException("Operation is not authorized by this capability.");

            var pathScoped = action is WorkTaskAction.ReadRepository or WorkTaskAction.WriteFiles;
            if (pathScoped)
            {
                if (!IsSafePath(path) || !capability.AllowedPaths.Any(scope => IsWithinScope(path!, scope)))
                    throw new UnauthorizedAccessException("Operation path is outside the capability scope.");
            }
            else if (path is not null)
            {
                throw new UnauthorizedAccessException("A repository path is not valid for this operation.");
            }
        }
    }

    /// <summary>Revokes a capability immediately. Repeated revocation is harmless.</summary>
    public void Revoke(Guid capabilityId)
    {
        lock (_gate)
            _capabilities.Remove(capabilityId);
    }

    private static bool IsWithinScope(string path, string scope) =>
        string.Equals(path, scope, StringComparison.Ordinal) ||
        path.StartsWith(scope.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.Any(char.IsControl));

    private static void ValidateBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            string.Equals(branch, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(branch, "development", StringComparison.OrdinalIgnoreCase) || branch == "@" ||
            branch.StartsWith('/') || branch.EndsWith('/') ||
            branch.Contains('\\') || branch.Contains("..", StringComparison.Ordinal) ||
            branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                character is '~' or '^' or ':' or '?' or '*' or '[' or ']') ||
            branch.Split('/').Any(segment => segment.Length == 0 ||
                segment.StartsWith('.') || segment is "." or ".." || segment.EndsWith('.') ||
                segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Capability must target a Git-valid isolated feature branch.", nameof(branch));
    }

    private static void ValidateSha(string sha, string parameterName)
    {
        if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal commit SHA is required.", parameterName);
    }
}
