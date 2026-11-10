using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>Terminal state recorded for one host repository operation.</summary>
public enum RepositoryOperationAuditStatus
{
    Pending,
    Completed,
    Failed
}

/// <summary>
/// Non-secret durable view of one repository operation. Request bodies, commit messages,
/// credentials, and adapter detail text are never stored; payloads are represented by SHA-256.
/// </summary>
public sealed record RepositoryOperationAuditEntry(
    int SchemaVersion,
    long Sequence,
    string OperationId,
    string TaskId,
    string Repository,
    string Branch,
    string OperationType,
    string Action,
    string CandidateSha,
    string? Path,
    string? RequestPayloadSha256,
    RepositoryOperationAuditStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? OperationReference,
    string? ResultingCandidateSha,
    string? FailureType,
    string IdempotencyKey,
    string CorrelationId)
{
    public const int CurrentSchemaVersion = 2;
}

/// <summary>Storage port for durable repository-operation audit records.</summary>
public interface IRepositoryOperationAuditJournal
{
    RepositoryOperationAuditEntry Begin(RepositoryOperation operation, DateTimeOffset startedAt);
    RepositoryOperationAuditEntry Begin(
        RepositoryOperation operation, string correlationId, DateTimeOffset startedAt);
    RepositoryOperationAuditEntry Complete(string operationId, RepositoryOperationResult result, DateTimeOffset completedAt);
    RepositoryOperationAuditEntry Fail(string operationId, Exception exception, DateTimeOffset failedAt);
    IReadOnlyList<RepositoryOperationAuditEntry> GetAll();
}

/// <summary>
/// Append-only JSONL audit for host repository operations. A Pending record is flushed before
/// transport invocation; a terminal record follows a validated result or a caught failure.
/// A pending record after process loss is indeterminate and must be reconciled, never replayed
/// automatically. File locking coordinates cooperating local processes, not distributed hosts.
/// </summary>
public sealed class JsonlRepositoryOperationJournal : IRepositoryOperationAuditJournal
{
    private const int MaximumRecordCharacters = 64 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Dictionary<string, RepositoryOperationAuditEntry> _entries = new(StringComparer.Ordinal);
    private long _sequence;

    public JsonlRepositoryOperationJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
        }
    }

    /// <summary>Persists a non-secret Pending record before any external side effect is invoked.</summary>
    public RepositoryOperationAuditEntry Begin(RepositoryOperation operation, DateTimeOffset startedAt) =>
        Begin(operation, operation?.TaskId ?? throw new ArgumentNullException(nameof(operation)), startedAt);

    public RepositoryOperationAuditEntry Begin(
        RepositoryOperation operation, string correlationId, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RepositoryOperation.RequireText(correlationId, nameof(correlationId), 128);
        if (startedAt == default)
            throw new ArgumentException("An operation start timestamp is required.", nameof(startedAt));

        var operationId = Guid.NewGuid().ToString("N");
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var entry = new RepositoryOperationAuditEntry(
            RepositoryOperationAuditEntry.CurrentSchemaVersion,
            0,
            operationId,
            operation.TaskId,
            operation.Repository,
            operation.Branch,
            GetOperationType(operation),
            operation.Action.ToString(),
            operation.CandidateSha,
            operation.Path,
            GetPayloadSha256(operation),
            RepositoryOperationAuditStatus.Pending,
            startedAt,
            null,
            null,
            null,
            null,
            idempotencyKey,
            correlationId);

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            var stored = entry with { Sequence = _sequence + 1 };
            Append(stored);
            _entries.Add(operationId, stored);
            _sequence = stored.Sequence;
            return stored;
        }
    }

    /// <summary>Completes only an existing Pending operation with validated result provenance.</summary>
    public RepositoryOperationAuditEntry Complete(
        string operationId, RepositoryOperationResult result, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (completedAt == default)
            throw new ArgumentException("An operation completion timestamp is required.", nameof(completedAt));
        RepositoryOperation.RequireText(operationId, nameof(operationId), 32);
        if (operationId.Length != 32 || !Guid.TryParseExact(operationId, "N", out _))
            throw new ArgumentException("A 32-character operation identifier is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(result.OperationReference) || result.OperationReference.Length > 512 ||
            result.OperationReference.Any(char.IsControl) ||
            result.ResultingCandidateSha is not { Length: 40 } ||
            !result.ResultingCandidateSha.All(Uri.IsHexDigit))
            throw new ArgumentException("The operation result is not safe to audit.", nameof(result));

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            var pending = GetPending(operationId);
            if (!string.Equals(result.RequestedCandidateSha, pending.CandidateSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The audited result does not match the authorized candidate SHA.");
            if (completedAt < pending.StartedAt)
                throw new ArgumentException("An operation cannot complete before it starts.", nameof(completedAt));

            var completed = pending with
            {
                Sequence = _sequence + 1,
                Status = RepositoryOperationAuditStatus.Completed,
                CompletedAt = completedAt,
                OperationReference = result.OperationReference,
                ResultingCandidateSha = result.ResultingCandidateSha,
                FailureType = null
            };
            Append(completed);
            _entries[operationId] = completed;
            _sequence = completed.Sequence;
            return completed;
        }
    }

    /// <summary>Records only the exception type; exception messages may contain sensitive data.</summary>
    public RepositoryOperationAuditEntry Fail(string operationId, Exception exception, DateTimeOffset failedAt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (failedAt == default)
            throw new ArgumentException("An operation failure timestamp is required.", nameof(failedAt));
        RepositoryOperation.RequireText(operationId, nameof(operationId), 32);
        if (operationId.Length != 32 || !Guid.TryParseExact(operationId, "N", out _))
            throw new ArgumentException("A 32-character operation identifier is required.", nameof(operationId));
        var failureType = exception.GetType().FullName ?? exception.GetType().Name;
        if (failureType.Length > 256 || failureType.Any(char.IsControl))
            failureType = "System.Exception";

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            var pending = GetPending(operationId);
            if (failedAt < pending.StartedAt)
                throw new ArgumentException("An operation cannot fail before it starts.", nameof(failedAt));

            var failed = pending with
            {
                Sequence = _sequence + 1,
                Status = RepositoryOperationAuditStatus.Failed,
                CompletedAt = failedAt,
                FailureType = failureType
            };
            Append(failed);
            _entries[operationId] = failed;
            _sequence = failed.Sequence;
            return failed;
        }
    }

    /// <summary>Returns the latest immutable state for each operation, ordered by start time.</summary>
    public IReadOnlyList<RepositoryOperationAuditEntry> GetAll()
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            return Array.AsReadOnly(_entries.Values
                .OrderBy(entry => entry.StartedAt)
                .ThenBy(entry => entry.OperationId, StringComparer.Ordinal)
                .ToArray());
        }
    }

    private RepositoryOperationAuditEntry GetPending(string operationId)
    {
        if (!_entries.TryGetValue(operationId, out var entry))
            throw new KeyNotFoundException("The repository operation audit entry does not exist.");
        if (entry.Status != RepositoryOperationAuditStatus.Pending)
            throw new InvalidOperationException("Only a Pending repository operation can be completed or failed.");
        return entry;
    }

    private FileStream AcquireFileLock()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < LockTimeout)
            {
                Thread.Sleep(25);
            }
            catch (IOException exception)
            {
                throw new TimeoutException("Timed out waiting for the repository operation journal lock.", exception);
            }
        }
    }

    private void Replay()
    {
        _entries.Clear();
        _sequence = 0;
        if (!File.Exists(_path))
            return;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10)
                throw new InvalidDataException("Repository operation journal has an incomplete final record; refusing to discard audit history.");
            stream.Position = 0;
        }

        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumRecordCharacters)
                throw new InvalidDataException($"Repository operation journal record {lineNumber} is empty or oversized.");
            RepositoryOperationAuditEntry record;
            try
            {
                record = JsonSerializer.Deserialize<RepositoryOperationAuditEntry>(line, JsonOptions)
                    ?? throw new JsonException("Null audit record.");
                ValidateRecord(record);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"Repository operation journal record {lineNumber} is invalid.", exception);
            }

            if (record.Sequence != _sequence + 1)
                throw new InvalidDataException($"Repository operation journal sequence is invalid at record {lineNumber}.");

            if (!_entries.TryGetValue(record.OperationId, out var previous))
            {
                if (record.Status != RepositoryOperationAuditStatus.Pending ||
                    record.CompletedAt.HasValue || record.OperationReference is not null ||
                    record.ResultingCandidateSha is not null || record.FailureType is not null)
                    throw new InvalidDataException($"Repository operation '{record.OperationId}' does not begin Pending.");
            }
            else
            {
                if (previous.Status != RepositoryOperationAuditStatus.Pending ||
                    record.Status == RepositoryOperationAuditStatus.Pending ||
                    !SameIdentity(previous, record) ||
                    !record.CompletedAt.HasValue || record.CompletedAt.Value < previous.StartedAt)
                    throw new InvalidDataException($"Repository operation '{record.OperationId}' has an invalid state transition.");
                if (record.Status == RepositoryOperationAuditStatus.Completed &&
                    (string.IsNullOrWhiteSpace(record.OperationReference) || record.ResultingCandidateSha is null ||
                     record.FailureType is not null))
                    throw new InvalidDataException($"Repository operation '{record.OperationId}' has an invalid completion.");
                if (record.Status == RepositoryOperationAuditStatus.Failed &&
                    (string.IsNullOrWhiteSpace(record.FailureType) || record.OperationReference is not null ||
                     record.ResultingCandidateSha is not null))
                    throw new InvalidDataException($"Repository operation '{record.OperationId}' has an invalid failure.");
            }

            _entries[record.OperationId] = record;
            _sequence = record.Sequence;
        }
    }

    private void Append(RepositoryOperationAuditEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        if (json.Length > MaximumRecordCharacters)
            throw new InvalidOperationException("Repository operation audit record exceeds its size limit.");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateRecord(RepositoryOperationAuditEntry entry)
    {
        if (entry.SchemaVersion is not (1 or RepositoryOperationAuditEntry.CurrentSchemaVersion) ||
            entry.Sequence < 1 || string.IsNullOrEmpty(entry.OperationId) || entry.OperationId.Length != 32 ||
            !Guid.TryParseExact(entry.OperationId, "N", out _) ||
            entry.StartedAt == default ||
            !Enum.TryParse<WorkTaskAction>(entry.Action, out var action) ||
            !Enum.IsDefined(action) || !Enum.IsDefined(entry.Status))
            throw new ArgumentException("Repository operation audit schema, identity, or state is invalid.");
        RepositoryOperation.RequireText(entry.TaskId, nameof(entry.TaskId), 128);
        if (entry.SchemaVersion == RepositoryOperationAuditEntry.CurrentSchemaVersion)
        {
            if (entry.IdempotencyKey is not { Length: 32 } ||
                !Guid.TryParseExact(entry.IdempotencyKey, "N", out _))
                throw new ArgumentException("Repository operation idempotency identity is invalid.");
            RepositoryOperation.RequireText(entry.CorrelationId, nameof(entry.CorrelationId), 128);
        }
        RepositoryOperation.RequireText(entry.Repository, nameof(entry.Repository), 256);
        RepositoryOperation.RequireText(entry.Branch, nameof(entry.Branch), 256);
        RepositoryOperation.RequireSha(entry.CandidateSha, nameof(entry.CandidateSha));
        RepositoryOperation.RequireText(entry.OperationType, nameof(entry.OperationType), 64);
        if (entry.Path is not null)
            RepositoryOperation.RequireRepositoryPath(entry.Path, nameof(entry.Path));
        if (entry.RequestPayloadSha256 is not null &&
            (entry.RequestPayloadSha256.Length != 64 || !entry.RequestPayloadSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("Repository operation payload digest is invalid.");
        if (entry.CompletedAt.HasValue && entry.CompletedAt.Value == default)
            throw new ArgumentException("Repository operation completion timestamp is invalid.");
        if (entry.Status == RepositoryOperationAuditStatus.Pending &&
            (entry.CompletedAt.HasValue || entry.OperationReference is not null ||
             entry.ResultingCandidateSha is not null || entry.FailureType is not null))
            throw new ArgumentException("Pending repository operation audit records cannot contain terminal fields.");
        if (entry.Status == RepositoryOperationAuditStatus.Completed &&
            (!entry.CompletedAt.HasValue || string.IsNullOrWhiteSpace(entry.OperationReference) ||
             entry.OperationReference.Length > 512 || entry.OperationReference.Any(char.IsControl) ||
             entry.ResultingCandidateSha is not { Length: 40 } ||
             !entry.ResultingCandidateSha.All(Uri.IsHexDigit) || entry.FailureType is not null))
            throw new ArgumentException("Completed repository operation audit records require valid result provenance.");
        if (entry.Status == RepositoryOperationAuditStatus.Failed &&
            (!entry.CompletedAt.HasValue || string.IsNullOrWhiteSpace(entry.FailureType) ||
             entry.FailureType.Length > 256 || entry.FailureType.Any(char.IsControl) ||
             entry.OperationReference is not null || entry.ResultingCandidateSha is not null))
            throw new ArgumentException("Failed repository operation audit records require a bounded failure type.");
    }

    private static bool SameIdentity(RepositoryOperationAuditEntry left, RepositoryOperationAuditEntry right) =>
        left.OperationId == right.OperationId && left.TaskId == right.TaskId &&
        left.Repository == right.Repository && left.Branch == right.Branch &&
        left.OperationType == right.OperationType && left.Action == right.Action &&
        left.CandidateSha == right.CandidateSha && left.Path == right.Path &&
        left.RequestPayloadSha256 == right.RequestPayloadSha256 && left.StartedAt == right.StartedAt &&
        left.IdempotencyKey == right.IdempotencyKey && left.CorrelationId == right.CorrelationId;

    private static string GetOperationType(RepositoryOperation operation) => operation switch
    {
        CreateRepositoryBranchOperation => nameof(CreateRepositoryBranchOperation),
        ReadRepositoryFileOperation => nameof(ReadRepositoryFileOperation),
        WriteRepositoryFileOperation => nameof(WriteRepositoryFileOperation),
        CreateRepositoryCommitOperation => nameof(CreateRepositoryCommitOperation),
        RequestRepositoryCiOperation => nameof(RequestRepositoryCiOperation),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), "Unsupported repository operation type.")
    };

    private static string? GetPayloadSha256(RepositoryOperation operation)
    {
        var payload = operation switch
        {
            WriteRepositoryFileOperation write => write.Content,
            CreateRepositoryCommitOperation commit => commit.Message,
            RequestRepositoryCiOperation ci => ci.WorkflowKey,
            _ => null
        };
        return payload is null ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
