using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

public sealed record WorkTaskRepairAttempt(
    int SchemaVersion,
    string TaskId,
    string CorrelationId,
    string Repository,
    string Branch,
    string WorkflowPath,
    DateTimeOffset TaskStartedAt,
    int AttemptNumber,
    string CandidateSha,
    long CiRunId,
    int CiRunAttempt,
    string EvidencePayloadSha256,
    bool EvidenceComplete,
    bool IsSuccessful,
    bool HasActionableDiagnostics,
    string? DiagnosticsFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset ObservedAt)
{
    /// <summary>SHA-256 fingerprint of the independently host-observed nested source-artifact identity.</summary>
    public string? SourceArtifactIdentitySha256 { get; init; }

    public const int CurrentSchemaVersion = 2;

    public WorkTaskRepairAttempt Validate()
    {
        if (SchemaVersion is not (1 or CurrentSchemaVersion) ||
            string.IsNullOrWhiteSpace(TaskId) || TaskId.Length > 200 ||
            string.IsNullOrWhiteSpace(CorrelationId) || CorrelationId.Length > 200)
            throw new ArgumentException("Repair attempt schema and task identity are invalid.");
        var repositoryParts = Repository?.Split('/') ?? Array.Empty<string>();
        if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Repair attempt repository must use owner/name identity.", nameof(Repository));
        if (string.IsNullOrWhiteSpace(Branch) || Branch is "master" or "development" ||
            Branch.StartsWith('/') || Branch.Contains('\\') ||
            Branch.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("Repair attempt must target an isolated feature branch.", nameof(Branch));
        if (!IsWorkflowPath(WorkflowPath))
            throw new ArgumentException("Repair attempt workflow path is invalid.", nameof(WorkflowPath));
        if (TaskStartedAt == default || StartedAt == default || ObservedAt < StartedAt ||
            StartedAt < TaskStartedAt)
            throw new ArgumentException("Repair attempt timestamps are invalid.");
        if (AttemptNumber < 1 || CiRunId <= 0 || CiRunAttempt < 1)
            throw new ArgumentOutOfRangeException(nameof(AttemptNumber), "Attempt, run, and run-attempt identities must be positive.");
        ValidateSha(CandidateSha, nameof(CandidateSha));
        ValidateDigest(EvidencePayloadSha256, nameof(EvidencePayloadSha256));
        if (SourceArtifactIdentitySha256 is not null)
            ValidateDigest(SourceArtifactIdentitySha256, nameof(SourceArtifactIdentitySha256));
        if (SchemaVersion == CurrentSchemaVersion && EvidenceComplete && SourceArtifactIdentitySha256 is null)
            throw new ArgumentException("Complete repair evidence requires a host-observed source-artifact identity fingerprint.");
        if (IsSuccessful && (!EvidenceComplete || HasActionableDiagnostics))
            throw new ArgumentException("Successful evidence must be complete and have no actionable failure diagnostics.");
        if (HasActionableDiagnostics && (!EvidenceComplete || IsSuccessful ||
            string.IsNullOrWhiteSpace(DiagnosticsFingerprint)))
            throw new ArgumentException("Actionable diagnostics require complete unsuccessful evidence and a fingerprint.");
        if (DiagnosticsFingerprint is not null)
            ValidateDigest(DiagnosticsFingerprint, nameof(DiagnosticsFingerprint));
        return this;
    }

    private static bool IsWorkflowPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(".github/workflows/", StringComparison.Ordinal) &&
        (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal)) &&
        !path.Contains('\\') && !path.Contains('#') && !path.Contains('?') &&
        path.Split('/').Length >= 3 &&
        path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static void ValidateSha(string value, string name)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal SHA is required.", name);
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 64-character SHA-256 digest is required.", name);
    }
}

public enum WorkTaskRepairAttemptAppendOutcome
{
    Appended,
    Duplicate,
    Conflict,
    OutOfOrder
}

/// <summary>Durable append-only history of exact CI attempts for each task.</summary>
public sealed class JsonlWorkTaskRepairAttemptJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, List<WorkTaskRepairAttempt>> _attempts = new(StringComparer.Ordinal);

    public JsonlWorkTaskRepairAttemptJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            Replay();
    }

    public WorkTaskRepairAttemptAppendOutcome Append(WorkTaskRepairAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Validate();
        if (attempt.SchemaVersion != WorkTaskRepairAttempt.CurrentSchemaVersion)
            throw new ArgumentException("New repair-attempt journal entries must use the current provenance schema.", nameof(attempt));
        lock (_gate)
        {
            if (!_attempts.TryGetValue(attempt.TaskId, out var history))
                history = new List<WorkTaskRepairAttempt>();

            var existing = history.FirstOrDefault(item => item.AttemptNumber == attempt.AttemptNumber);
            if (existing is not null)
                return existing == attempt
                    ? WorkTaskRepairAttemptAppendOutcome.Duplicate
                    : WorkTaskRepairAttemptAppendOutcome.Conflict;

            if (attempt.AttemptNumber != history.Count + 1)
                return WorkTaskRepairAttemptAppendOutcome.OutOfOrder;
            if (history.Any(item => item.CiRunId == attempt.CiRunId &&
                item.CiRunAttempt == attempt.CiRunAttempt))
                return WorkTaskRepairAttemptAppendOutcome.Conflict;
            if (_attempts.Values.SelectMany(items => items).Any(item =>
                string.Equals(item.Repository, attempt.Repository, StringComparison.OrdinalIgnoreCase) &&
                item.CiRunId == attempt.CiRunId && item.CiRunAttempt == attempt.CiRunAttempt))
                return WorkTaskRepairAttemptAppendOutcome.Conflict;
            if (history.Count > 0)
            {
                var previous = history[^1];
                if (!SameTask(previous, attempt) || previous.IsSuccessful ||
                    attempt.StartedAt < previous.ObservedAt)
                    return WorkTaskRepairAttemptAppendOutcome.Conflict;
            }

            WriteLine(attempt);
            if (!_attempts.TryGetValue(attempt.TaskId, out history))
                _attempts.Add(attempt.TaskId, history = new List<WorkTaskRepairAttempt>());
            history.Add(attempt);
            return WorkTaskRepairAttemptAppendOutcome.Appended;
        }
    }

    /// <summary>
    /// Executes a decision callback while holding the journal lock, preventing an append
    /// from changing the attempt history between validation and the caller's durable decision.
    /// The callback receives an immutable snapshot and must not call back into this journal.
    /// </summary>
    public TResult ExecuteWithAttempts<TResult>(string taskId,
        Func<IReadOnlyList<WorkTaskRepairAttempt>, TResult> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            IReadOnlyList<WorkTaskRepairAttempt> history = _attempts.TryGetValue(taskId, out var attempts)
                ? Array.AsReadOnly(attempts.ToArray())
                : Array.Empty<WorkTaskRepairAttempt>();
            return action(history);
        }
    }

    public IReadOnlyList<WorkTaskRepairAttempt> GetAttempts(string taskId) =>
        ExecuteWithAttempts(taskId, history => history);

    public IReadOnlyList<WorkTaskRepairAttempt> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_attempts.Values.SelectMany(history => history)
                .OrderBy(item => item.TaskId, StringComparer.Ordinal)
                .ThenBy(item => item.AttemptNumber).ToArray());
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10)
                throw new InvalidDataException("Repair-attempt journal has an incomplete final record; refusing to discard evidence.");
            stream.Position = 0;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException($"Repair-attempt journal contains an empty record at line {lineNumber}.");
            WorkTaskRepairAttempt attempt;
            try
            {
                attempt = JsonSerializer.Deserialize<WorkTaskRepairAttempt>(line, JsonOptions)
                    ?? throw new InvalidDataException($"Repair-attempt record {lineNumber} is null.");
                attempt.Validate();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"Repair-attempt record {lineNumber} is invalid.", exception);
            }

            if (!_attempts.TryGetValue(attempt.TaskId, out var history))
                _attempts.Add(attempt.TaskId, history = new List<WorkTaskRepairAttempt>());
            var reusedRunAttempt = _attempts.Values.SelectMany(items => items).Any(item =>
                string.Equals(item.Repository, attempt.Repository, StringComparison.OrdinalIgnoreCase) &&
                item.CiRunId == attempt.CiRunId && item.CiRunAttempt == attempt.CiRunAttempt);
            if (attempt.AttemptNumber != history.Count + 1 || reusedRunAttempt ||
                (history.Count > 0 && (!SameTask(history[^1], attempt) || history[^1].IsSuccessful ||
                    attempt.StartedAt < history[^1].ObservedAt)))
                throw new InvalidDataException($"Repair-attempt history for '{attempt.TaskId}' is discontinuous or reuses CI evidence.");
            history.Add(attempt);
        }
    }

    private void WriteLine(WorkTaskRepairAttempt attempt)
    {
        var json = JsonSerializer.Serialize(attempt, JsonOptions);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool SameTask(WorkTaskRepairAttempt left, WorkTaskRepairAttempt right) =>
        left.SchemaVersion == right.SchemaVersion && left.TaskId == right.TaskId &&
        left.CorrelationId == right.CorrelationId &&
        string.Equals(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase) &&
        left.Branch == right.Branch && left.WorkflowPath == right.WorkflowPath &&
        left.TaskStartedAt == right.TaskStartedAt;
}
