namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Closed set of repository operations that a host may expose to an agent.
/// There is deliberately no shell, arbitrary tool, merge, release, deployment,
/// branch deletion, or package publication operation.
/// </summary>
public abstract record RepositoryOperation
{
    private protected RepositoryOperation(string taskId, string repository, string branch, string candidateSha)
    {
        TaskId = RequireText(taskId, nameof(taskId), 128);
        Repository = RequireText(repository, nameof(repository), 256);
        Branch = RequireText(branch, nameof(branch), 256);
        CandidateSha = RequireSha(candidateSha, nameof(candidateSha));
    }

    public string TaskId { get; }
    public string Repository { get; }
    public string Branch { get; }
    /// <summary>The exact candidate commit against which this operation is authorized.</summary>
    public string CandidateSha { get; }
    public abstract WorkTaskAction Action { get; }
    public virtual string? Path => null;

    internal static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Operation identity must be non-empty, bounded, and control-free.", parameterName);
        return value;
    }

    internal static string RequireRepositoryPath(string value, string parameterName)
    {
        var path = RequireText(value, parameterName, 512);
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('%') ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Repository paths must be normalized relative paths without encoded or traversal segments.", parameterName);
        return path;
    }

    internal static string RequireSha(string value, string parameterName)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal SHA is required.", parameterName);
        return value;
    }
}

/// <summary>Creates an isolated candidate branch at the exact authorized base commit.</summary>
public sealed record CreateRepositoryBranchOperation : RepositoryOperation
{
    public CreateRepositoryBranchOperation(string taskId, string repository, string branch,
        string baseSha) : base(taskId, repository, branch, baseSha)
    {
    }

    public override WorkTaskAction Action => WorkTaskAction.CreateBranch;
}

/// <summary>A bounded repository file read.</summary>
public sealed record ReadRepositoryFileOperation : RepositoryOperation
{
    public ReadRepositoryFileOperation(string taskId, string repository, string branch,
        string candidateSha, string path) : base(taskId, repository, branch, candidateSha)
    {
        Path = RequireRepositoryPath(path, nameof(path));
    }

    public override WorkTaskAction Action => WorkTaskAction.ReadRepository;
    public override string? Path { get; }
}

/// <summary>A bounded file write. File contents are data, never executable host instructions.</summary>
public sealed record WriteRepositoryFileOperation : RepositoryOperation
{
    public const int MaximumContentCharacters = 64 * 1024;

    public WriteRepositoryFileOperation(string taskId, string repository, string branch,
        string candidateSha, string path, string content) : base(taskId, repository, branch, candidateSha)
    {
        Path = RequireRepositoryPath(path, nameof(path));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (Content.Length > MaximumContentCharacters)
            throw new ArgumentException("File content exceeds the operation size limit.", nameof(content));
    }

    public override WorkTaskAction Action => WorkTaskAction.WriteFiles;
    public override string? Path { get; }
    public string Content { get; }
}

/// <summary>A commit request on the already isolated candidate branch.</summary>
public sealed record CreateRepositoryCommitOperation : RepositoryOperation
{
    public CreateRepositoryCommitOperation(string taskId, string repository, string branch,
        string candidateSha, string message) : base(taskId, repository, branch, candidateSha)
    {
        Message = RequireText(message, nameof(message), 256);
    }

    public override WorkTaskAction Action => WorkTaskAction.CreateCommit;
    public string Message { get; }
}

/// <summary>A request to run a host-allowlisted CI workflow against the exact candidate SHA.</summary>
public sealed record RequestRepositoryCiOperation : RepositoryOperation
{
    public RequestRepositoryCiOperation(string taskId, string repository, string branch,
        string candidateSha, string workflowKey) : base(taskId, repository, branch, candidateSha)
    {
        WorkflowKey = RequireText(workflowKey, nameof(workflowKey), 128);
    }

    public override WorkTaskAction Action => WorkTaskAction.RequestCiRun;
    public string WorkflowKey { get; }
}

/// <summary>
/// Host-owned repository transport. Implementations hold credentials and must independently
/// enforce their configured repository, branch, workflow, and network policy. For mutations,
/// the adapter must verify that the resulting commit is based on the authorized candidate SHA.
/// </summary>
public interface IRepositoryOperationAdapter
{
    ValueTask<RepositoryOperationResult> ExecuteAsync(
        RepositoryOperation operation, CancellationToken cancellationToken);
}

/// <summary>
/// Non-secret adapter result. RequestedCandidateSha identifies the authorized input commit;
/// ResultingCandidateSha identifies the commit after the operation (which may differ for writes).
/// </summary>
public sealed record RepositoryOperationResult(
    string OperationReference,
    string RequestedCandidateSha,
    string ResultingCandidateSha,
    string? Detail = null);

/// <summary>
/// Last-mile authorization boundary between workflow/agent proposals and a host repository adapter.
/// Every call re-reads the durable task snapshot and demands the exact host-issued capability.
/// </summary>
public sealed class AuthorizedRepositoryOperationDispatcher
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly WorkTaskCapabilityRegistry _capabilities;
    private readonly IRepositoryOperationAdapter _adapter;
    private readonly IRepositoryOperationAuditJournal _journal;
    private readonly Func<DateTimeOffset> _clock;

    public AuthorizedRepositoryOperationDispatcher(
        PersistentWorkTaskCoordinator tasks,
        WorkTaskCapabilityRegistry capabilities,
        IRepositoryOperationAdapter adapter,
        IRepositoryOperationAuditJournal journal,
        Func<DateTimeOffset>? clock = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<RepositoryOperationResult> ExecuteAsync(
        Guid capabilityId, RepositoryOperation operation, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (now == default)
            throw new ArgumentException("Operation time is required.", nameof(now));

        var task = _tasks.GetSnapshot(operation.TaskId);
        if (!string.Equals(task.Request.Repository, operation.Repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Operation repository does not match the durable task.");

        _capabilities.Demand(capabilityId, task, operation.Branch, operation.CandidateSha,
            operation.Action, operation.Path, now);

        cancellationToken.ThrowIfCancellationRequested();

        // Persist intent before invoking the host: a crash after the external side effect leaves
        // a Pending record that must be reconciled, not replayed automatically.
        var audit = _journal.Begin(operation, now);
        RepositoryOperationResult result;
        try
        {
            // Recheck after the durable write to close the journal-I/O time-of-check window.
            var latestTask = _tasks.GetSnapshot(operation.TaskId);
            if (!string.Equals(latestTask.Request.Repository, operation.Repository, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Operation repository no longer matches the durable task.");
            _capabilities.Demand(capabilityId, latestTask, operation.Branch, operation.CandidateSha,
                operation.Action, operation.Path, _clock());
            cancellationToken.ThrowIfCancellationRequested();

            result = await _adapter.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(result);
            if (string.IsNullOrWhiteSpace(result.OperationReference) ||
                result.OperationReference.Length > 512 || result.OperationReference.Any(char.IsControl) ||
                !string.Equals(result.RequestedCandidateSha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                result.ResultingCandidateSha is not { Length: 40 } ||
                !result.ResultingCandidateSha.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Repository adapter returned invalid operation provenance.");

            if ((operation is ReadRepositoryFileOperation or RequestRepositoryCiOperation or CreateRepositoryBranchOperation) &&
                !string.Equals(result.ResultingCandidateSha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Read, CI, and branch-creation operations must retain the exact authorized candidate SHA.");
        }
        catch (Exception exception)
        {
            _journal.Fail(audit.OperationId, exception, _clock());
            throw;
        }

        // Do not persist adapter Detail: it is host-controlled text and may contain secrets.
        _journal.Complete(audit.OperationId, result, _clock());
        return result;
    }
}
