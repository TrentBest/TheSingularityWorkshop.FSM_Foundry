using System.Text.Json;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

public enum GitHubActionsDispatchState
{
    Dispatching,
    Acknowledged,
    Correlated,
    Verified
}

public sealed record GitHubActionsDispatchRecord(
    int SchemaVersion,
    long Sequence,
    string RequestId,
    string TaskId,
    string Repository,
    string WorkflowFile,
    string WorkflowPath,
    string Branch,
    string CandidateSha,
    DateTimeOffset CreatedAt,
    GitHubActionsDispatchState State,
    DateTimeOffset? AcknowledgedAt,
    long? RunId,
    int? RunAttempt);

/// <summary>
/// Durable intent and correlation journal. Dispatching is flushed before the network call;
/// an interrupted Dispatching record is reconciled by request ID and is never automatically resent.
/// </summary>
public interface IGitHubActionsDispatchJournal
{
    GitHubActionsDispatchRecord Begin(
        string requestId, string taskId, string repository, string workflowFile, string workflowPath,
        string branch, string candidateSha, DateTimeOffset createdAt);
    GitHubActionsDispatchRecord Acknowledge(string requestId, DateTimeOffset acknowledgedAt);
    GitHubActionsDispatchRecord Correlate(string requestId, long runId, int runAttempt);
    GitHubActionsDispatchRecord Verify(string requestId, long runId, int runAttempt);
    GitHubActionsDispatchRecord Get(string requestId);
    IReadOnlyList<GitHubActionsDispatchRecord> GetUnresolved();
}

public sealed class JsonlGitHubActionsDispatchJournal : IGitHubActionsDispatchJournal
{
    private const int MaximumRecordCharacters = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Dictionary<string, GitHubActionsDispatchRecord> _records = new(StringComparer.Ordinal);
    private long _sequence;

    public JsonlGitHubActionsDispatchJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            Replay();
        }
    }

    public GitHubActionsDispatchRecord Begin(
        string requestId, string taskId, string repository, string workflowFile, string workflowPath,
        string branch, string candidateSha, DateTimeOffset createdAt)
    {
        ValidateIdentity(requestId, taskId, repository, workflowFile, workflowPath, branch, candidateSha, createdAt);
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            Replay();
            if (_records.Values.Any(record =>
                record.State != GitHubActionsDispatchState.Verified &&
                string.Equals(record.Repository, repository, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.WorkflowFile, workflowFile, StringComparison.Ordinal) &&
                string.Equals(record.Branch, branch, StringComparison.Ordinal) &&
                string.Equals(record.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An unresolved dispatch already exists for this workflow, branch, and candidate SHA; reconcile it before creating another dispatch.");
            if (_records.ContainsKey(requestId))
                throw new InvalidOperationException("Dispatch request ID already exists.");
            var record = new GitHubActionsDispatchRecord(1, _sequence + 1, requestId, taskId, repository,
                workflowFile, workflowPath, branch, candidateSha, createdAt,
                GitHubActionsDispatchState.Dispatching, null, null, null);
            Append(record);
            _records.Add(requestId, record);
            _sequence = record.Sequence;
            return record;
        }
    }

    public GitHubActionsDispatchRecord Acknowledge(string requestId, DateTimeOffset acknowledgedAt) =>
        Transition(requestId, current =>
        {
            if (current.State is GitHubActionsDispatchState.Correlated or GitHubActionsDispatchState.Verified)
                return current;
            if (current.State != GitHubActionsDispatchState.Dispatching || acknowledgedAt < current.CreatedAt)
                throw new InvalidOperationException("Dispatch acknowledgment does not follow a pending dispatch intent.");
            return current with { State = GitHubActionsDispatchState.Acknowledged, AcknowledgedAt = acknowledgedAt };
        });

    public GitHubActionsDispatchRecord Correlate(string requestId, long runId, int runAttempt) =>
        Transition(requestId, current =>
        {
            if (current.State is GitHubActionsDispatchState.Correlated or GitHubActionsDispatchState.Verified ||
                runId <= 0 || runAttempt <= 0)
                throw new InvalidOperationException("Dispatch correlation identity or state is invalid.");
            return current with
            {
                State = GitHubActionsDispatchState.Correlated,
                RunId = runId,
                RunAttempt = runAttempt
            };
        });

    public GitHubActionsDispatchRecord Verify(string requestId, long runId, int runAttempt) =>
        Transition(requestId, current =>
        {
            if (current.State != GitHubActionsDispatchState.Correlated ||
                current.RunId != runId || current.RunAttempt != runAttempt)
                throw new InvalidOperationException("Only the exact correlated run attempt can be verified.");
            return current with { State = GitHubActionsDispatchState.Verified };
        });

    public GitHubActionsDispatchRecord Get(string requestId)
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            Replay();
            return _records.TryGetValue(requestId, out var record)
                ? record
                : throw new KeyNotFoundException("Dispatch request ID was not found in the durable journal.");
        }
    }

    public IReadOnlyList<GitHubActionsDispatchRecord> GetUnresolved()
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            Replay();
            return _records.Values.Where(record => record.State != GitHubActionsDispatchState.Verified)
                .OrderBy(record => record.Sequence).ToArray();
        }
    }

    private GitHubActionsDispatchRecord Transition(
        string requestId, Func<GitHubActionsDispatchRecord, GitHubActionsDispatchRecord> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            Replay();
            if (!_records.TryGetValue(requestId, out var current))
                throw new KeyNotFoundException("Dispatch request ID was not found in the durable journal.");
            var proposed = update(current);
            if (ReferenceEquals(proposed, current))
                return current;
            var updated = proposed with { Sequence = _sequence + 1 };
            Append(updated);
            _records[requestId] = updated;
            _sequence = updated.Sequence;
            return updated;
        }
    }

    private void Replay()
    {
        _records.Clear();
        _sequence = 0;
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumRecordCharacters)
                throw new InvalidDataException("Dispatch journal contains an empty or oversized record.");
            GitHubActionsDispatchRecord record;
            try
            {
                record = JsonSerializer.Deserialize<GitHubActionsDispatchRecord>(line, Options)
                    ?? throw new InvalidDataException("Dispatch journal record is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Dispatch journal contains malformed JSON.", exception);
            }
            if (record.SchemaVersion != 1 || record.Sequence != _sequence + 1)
                throw new InvalidDataException("Dispatch journal schema or sequence is invalid.");
            ValidateIdentity(record.RequestId, record.TaskId, record.Repository, record.WorkflowFile,
                record.WorkflowPath, record.Branch, record.CandidateSha, record.CreatedAt);
            if (!_records.TryGetValue(record.RequestId, out var previous))
            {
                if (record.State != GitHubActionsDispatchState.Dispatching ||
                    record.AcknowledgedAt is not null || record.RunId is not null || record.RunAttempt is not null)
                    throw new InvalidDataException("Dispatch journal must begin with a Dispatching intent.");
            }
            else
            {
                if (!string.Equals(record.TaskId, previous.TaskId, StringComparison.Ordinal) ||
                    !string.Equals(record.Repository, previous.Repository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.WorkflowFile, previous.WorkflowFile, StringComparison.Ordinal) ||
                    !string.Equals(record.WorkflowPath, previous.WorkflowPath, StringComparison.Ordinal) ||
                    !string.Equals(record.Branch, previous.Branch, StringComparison.Ordinal) ||
                    !string.Equals(record.CandidateSha, previous.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                    record.CreatedAt != previous.CreatedAt)
                    throw new InvalidDataException("Dispatch journal identity changed within one request.");

                var validTransition = record.State switch
                {
                    GitHubActionsDispatchState.Acknowledged =>
                        previous.State == GitHubActionsDispatchState.Dispatching &&
                        record.AcknowledgedAt is { } acknowledged && acknowledged >= previous.CreatedAt &&
                        record.RunId is null && record.RunAttempt is null,
                    GitHubActionsDispatchState.Correlated =>
                        (previous.State is GitHubActionsDispatchState.Dispatching or GitHubActionsDispatchState.Acknowledged) &&
                        record.RunId is > 0 && record.RunAttempt is > 0 &&
                        record.AcknowledgedAt == previous.AcknowledgedAt,
                    GitHubActionsDispatchState.Verified =>
                        previous.State == GitHubActionsDispatchState.Correlated &&
                        record.RunId == previous.RunId && record.RunAttempt == previous.RunAttempt &&
                        record.AcknowledgedAt == previous.AcknowledgedAt,
                    _ => false
                };
                if (!validTransition)
                    throw new InvalidDataException("Dispatch journal state transition is invalid.");
            }
            _records[record.RequestId] = record;
            _sequence = record.Sequence;
        }
    }

    private void Append(GitHubActionsDispatchRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options);
        if (line.Length > MaximumRecordCharacters)
            throw new InvalidOperationException("Dispatch journal record exceeds its storage limit.");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void ValidateIdentity(
        string requestId, string taskId, string repository, string workflowFile, string workflowPath,
        string branch, string candidateSha, DateTimeOffset createdAt)
    {
        if (requestId is not { Length: 32 } || !requestId.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(repository) || repository.Length > 200 || repository.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(workflowFile) || workflowFile.Length > 128 ||
            string.IsNullOrWhiteSpace(workflowPath) || workflowPath.Length > 256 ||
            !workflowPath.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(branch) || branch.Length > 200 || branch.Any(char.IsControl) ||
            candidateSha is not { Length: 40 } || !candidateSha.All(Uri.IsHexDigit) ||
            createdAt == default)
            throw new ArgumentException("Dispatch journal identity is invalid.");
    }
}
