using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Durable, append-only invocation ledger for Foundry's HeadlessAi bridge.
/// A pending entry is never automatically replayed after a crash because the provider may
/// already have received the request. Cooperating instances coordinate through an exclusive
/// sidecar file handle and reload the journal while holding that lock. This is intended for
/// a local filesystem; it is not a distributed ledger or a guarantee for network filesystems.
/// </summary>
public sealed class JsonlHeadlessAiTurnJournal
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecordCharacters = 2_000_000;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Dictionary<string, EntryState> _entries = new(StringComparer.Ordinal);
    private long _sequence;
    private Exception? _persistenceFailure;

    public JsonlHeadlessAiTurnJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A turn journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var fileLock = AcquireFileLock();
        Replay();
    }

    /// <summary>
    /// Durably reserves a turn before provider invocation. Returns a previously completed
    /// result for an exact duplicate. A pending duplicate is indeterminate and is never
    /// invoked again automatically.
    /// </summary>
    public FoundryHeadlessAiTurnResult? BeginOrGet(string taskId, string idempotencyKey,
        string invocationFingerprint)
    {
        ValidateIdentity(taskId, idempotencyKey, invocationFingerprint);
        var key = GetKey(taskId, idempotencyKey);
        lock (_gate)
        {
            EnsureHealthy();
            using var fileLock = AcquireFileLock();
            Replay();
            if (_entries.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.InvocationFingerprint, invocationFingerprint,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The HeadlessAi idempotency key was reused with different canonical invocation data.");
                if (existing.Result is not null)
                    return Clone(existing.Result);
                throw new InvalidOperationException(
                    "This HeadlessAi invocation has a durable pending record but no result. The provider outcome is indeterminate; reconcile or escalate instead of invoking it again.");
            }

            Append(new JournalRecord(CurrentSchemaVersion, _sequence + 1, taskId,
                idempotencyKey, invocationFingerprint, JournalState.Pending, null));
            _entries.Add(key, new EntryState(invocationFingerprint, null));
            return null;
        }
    }

    /// <summary>Durably completes the exact pending invocation with its validated normalized result.</summary>
    public void Complete(string taskId, string idempotencyKey, string invocationFingerprint,
        FoundryHeadlessAiTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateIdentity(taskId, idempotencyKey, invocationFingerprint);
        if (result.TaskId != taskId || result.IdempotencyKey != idempotencyKey)
            throw new ArgumentException("The HeadlessAi result does not match the journal task and idempotency identity.", nameof(result));
        var key = GetKey(taskId, idempotencyKey);
        lock (_gate)
        {
            EnsureHealthy();
            using var fileLock = AcquireFileLock();
            Replay();
            if (!_entries.TryGetValue(key, out var existing) ||
                !string.Equals(existing.InvocationFingerprint, invocationFingerprint, StringComparison.Ordinal) ||
                existing.Result is not null)
                throw new InvalidOperationException("Only the exact pending HeadlessAi invocation can be completed.");

            var stored = Clone(result);
            Append(new JournalRecord(CurrentSchemaVersion, _sequence + 1, taskId,
                idempotencyKey, invocationFingerprint, JournalState.Completed, stored));
            _entries[key] = new EntryState(invocationFingerprint, stored);
        }
    }

    /// <summary>Returns a completed turn only when it belongs to the exact task and turn ID.</summary>
    public FoundryHeadlessAiTurnResult? GetCompletedTurn(string taskId, string turnId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 ||
            string.IsNullOrWhiteSpace(turnId) || turnId.Length > 128)
            throw new ArgumentException("Task and turn identities must be non-empty and bounded.");
        lock (_gate)
        {
            EnsureHealthy();
            using var fileLock = AcquireFileLock();
            Replay();
            var matches = _entries.Values
                .Select(entry => entry.Result)
                .Where(result => result is not null && result.TaskId == taskId && result.TurnId == turnId)
                .Cast<FoundryHeadlessAiTurnResult>()
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException("A HeadlessAi turn ID is duplicated within the task journal.");
            return matches.Length == 0 ? null : Clone(matches[0]);
        }
    }

    private FileStream AcquireFileLock()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // The OS releases this exclusive handle when the owning process exits.
                // Keep the sidecar file itself: deleting it can split lock ownership.
                return new FileStream(_lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < LockTimeout)
            {
                Thread.Sleep(25);
            }
            catch (IOException exception)
            {
                throw new TimeoutException(
                    "Timed out waiting for the HeadlessAi turn journal file lock.", exception);
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
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line.Length > MaximumRecordCharacters)
                throw new InvalidDataException("The HeadlessAi turn journal contains an empty or oversized record.");
            JournalRecord record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions)
                    ?? throw new JsonException("Empty turn journal record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The HeadlessAi turn journal contains malformed JSON.", exception);
            }

            if (record.SchemaVersion != CurrentSchemaVersion || record.Sequence != _sequence + 1)
                throw new InvalidDataException("The HeadlessAi turn journal schema or sequence is invalid.");
            ValidateIdentity(record.TaskId, record.IdempotencyKey, record.InvocationFingerprint);
            var key = GetKey(record.TaskId, record.IdempotencyKey);
            switch (record.State)
            {
                case JournalState.Pending:
                    if (record.Result is not null || _entries.ContainsKey(key))
                        throw new InvalidDataException("The HeadlessAi turn journal contains a duplicate or malformed pending record.");
                    _entries.Add(key, new EntryState(record.InvocationFingerprint, null));
                    break;
                case JournalState.Completed:
                    if (record.Result is null || !_entries.TryGetValue(key, out var pending) ||
                        pending.Result is not null ||
                        !string.Equals(pending.InvocationFingerprint, record.InvocationFingerprint, StringComparison.Ordinal) ||
                        record.Result.TaskId != record.TaskId ||
                        record.Result.IdempotencyKey != record.IdempotencyKey)
                        throw new InvalidDataException("The HeadlessAi turn journal contains an invalid completion record.");
                    _entries[key] = new EntryState(record.InvocationFingerprint, Clone(record.Result));
                    break;
                default:
                    throw new InvalidDataException("The HeadlessAi turn journal contains an unknown state.");
            }
            _sequence = record.Sequence;
        }
    }

    private void Append(JournalRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        if (json.Length > MaximumRecordCharacters)
            throw new InvalidOperationException("The HeadlessAi turn journal record exceeds its size limit.");
        try
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            _sequence = record.Sequence;
        }
        catch (Exception exception)
        {
            _persistenceFailure = exception;
            throw new InvalidOperationException(
                "The HeadlessAi turn journal could not be durably written; further invocations are disabled for this journal instance.",
                exception);
        }
    }

    private void EnsureHealthy()
    {
        if (_persistenceFailure is not null)
            throw new InvalidOperationException(
                "HeadlessAi invocation is disabled because a prior turn-journal write failed.",
                _persistenceFailure);
    }

    private static FoundryHeadlessAiTurnResult Clone(FoundryHeadlessAiTurnResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Deserialize<FoundryHeadlessAiTurnResult>(json, JsonOptions)
            ?? throw new InvalidDataException("A stored HeadlessAi turn result could not be cloned.");
    }

    private static void ValidateIdentity(string taskId, string idempotencyKey, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64 ||
            !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Task, idempotency, and SHA-256 invocation identities must be valid and bounded.");
    }

    private static string GetKey(string taskId, string idempotencyKey) => taskId + "\n" + idempotencyKey;

    private sealed record EntryState(string InvocationFingerprint, FoundryHeadlessAiTurnResult? Result);
    private enum JournalState { Pending, Completed }
    private sealed record JournalRecord(int SchemaVersion, long Sequence, string TaskId,
        string IdempotencyKey, string InvocationFingerprint, JournalState State,
        FoundryHeadlessAiTurnResult? Result);
}
