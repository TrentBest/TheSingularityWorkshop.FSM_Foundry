using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Append-only, single-process durable journal for task snapshots. Each accepted
/// snapshot is a complete recovery point; earlier snapshots are never overwritten.
/// </summary>
public sealed class JsonlWorkTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, WorkTaskSnapshot> _latest = new(StringComparer.Ordinal);

    public JsonlWorkTaskStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(_path))
            Replay();
    }

    public WorkTaskSnapshot Create(WorkTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        lock (_gate)
        {
            var id = snapshot.Request.TaskId;
            if (_latest.ContainsKey(id))
                throw new InvalidOperationException($"Task '{id}' already exists in the journal.");
            if (snapshot.Events.Count != 0 || snapshot.Status != WorkTaskStatus.Queued ||
                snapshot.Lease is not null || snapshot.Attempt != 0)
                throw new ArgumentException("A new journal task must be an untouched queued snapshot.", nameof(snapshot));
            WriteLine(snapshot);
            _latest.Add(id, Clone(snapshot));
            return Clone(snapshot);
        }
    }

    public WorkTaskSnapshot Append(WorkTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        lock (_gate)
        {
            if (!_latest.TryGetValue(snapshot.Request.TaskId, out var previous))
                throw new KeyNotFoundException($"Task '{snapshot.Request.TaskId}' is not in the journal.");
            if (snapshot.Events.Count <= previous.Events.Count ||
                snapshot.Events.Count != previous.Events.Count + 1 ||
                !snapshot.Events.Take(previous.Events.Count).SequenceEqual(previous.Events))
                throw new InvalidOperationException("A journal append must add exactly one event to the existing history.");
            if (!SameRequest(snapshot.Request, previous.Request))
                throw new InvalidOperationException("Task provenance cannot change after creation.");
            WriteLine(snapshot);
            _latest[snapshot.Request.TaskId] = Clone(snapshot);
            return Clone(snapshot);
        }
    }

    public WorkTaskSnapshot Get(string taskId)
    {
        lock (_gate)
            return _latest.TryGetValue(taskId, out var snapshot)
                ? Clone(snapshot)
                : throw new KeyNotFoundException($"Unknown task '{taskId}'.");
    }

    public IReadOnlyList<WorkTaskSnapshot> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values.OrderBy(x => x.Request.TaskId, StringComparer.Ordinal)
                .Select(Clone).ToArray());
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException($"Journal contains an empty record at line {lineNumber}.");
            WorkTaskSnapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<WorkTaskSnapshot>(line, JsonOptions)
                    ?? throw new InvalidDataException($"Journal record {lineNumber} is null.");
                ValidateSnapshot(snapshot);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"Journal record {lineNumber} is invalid.", exception);
            }

            var id = snapshot.Request.TaskId;
            if (!_latest.TryGetValue(id, out var previous))
            {
                if (snapshot.Events.Count != 0 || snapshot.Status != WorkTaskStatus.Queued ||
                    snapshot.Lease is not null || snapshot.Attempt != 0)
                    throw new InvalidDataException($"Journal task '{id}' does not begin with a queued snapshot.");
            }
            else if (snapshot.Events.Count != previous.Events.Count + 1 ||
                     !snapshot.Events.Take(previous.Events.Count).SequenceEqual(previous.Events) ||
                     !SameRequest(snapshot.Request, previous.Request))
            {
                throw new InvalidDataException($"Journal history for task '{id}' is discontinuous.");
            }
            _latest[id] = Clone(snapshot);
        }
    }

    private void WriteLine(WorkTaskSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool SameRequest(WorkTaskRequest left, WorkTaskRequest right) =>
        left.SchemaVersion == right.SchemaVersion && left.TaskId == right.TaskId &&
        left.CorrelationId == right.CorrelationId && left.Repository == right.Repository &&
        left.IssueNumber == right.IssueNumber && left.IssueUrl == right.IssueUrl &&
        left.BaseSha == right.BaseSha && left.MaximumAttempts == right.MaximumAttempts &&
        left.AllowedPaths.SequenceEqual(right.AllowedPaths, StringComparer.Ordinal);

    private static WorkTaskSnapshot Clone(WorkTaskSnapshot snapshot) =>
        snapshot with
        {
            Request = snapshot.Request with
            {
                AllowedPaths = Array.AsReadOnly(snapshot.Request.AllowedPaths.ToArray())
            },
            Events = Array.AsReadOnly(snapshot.Events.ToArray())
        };

    internal static void ValidateSnapshot(WorkTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Request);
        snapshot.Request.Validate();
        if (snapshot.Events is null)
            throw new ArgumentException("Task event history is required.", nameof(snapshot));
        if (!Enum.IsDefined(snapshot.Status))
            throw new ArgumentException("Task status is invalid.", nameof(snapshot));
        if (snapshot.Attempt < 0 || snapshot.Attempt > snapshot.Request.MaximumAttempts)
            throw new ArgumentException("Task attempt is outside its configured budget.", nameof(snapshot));
        if (snapshot.Lease is { } lease &&
            (string.IsNullOrWhiteSpace(lease.AgentId) || lease.AgentId.Length > 128 ||
             lease.AgentId.Any(char.IsControl) || lease.LeaseId == Guid.Empty))
            throw new ArgumentException("Task lease identity is invalid.", nameof(snapshot));

        if (snapshot.Events.Count == 0)
        {
            if (snapshot.Status != WorkTaskStatus.Queued || snapshot.Lease is not null || snapshot.Attempt != 0)
                throw new ArgumentException("An event-free snapshot must be an untouched queued task.", nameof(snapshot));
            return;
        }

        var previousStatus = WorkTaskStatus.Queued;
        DateTimeOffset? previousTime = null;
        var validationAttempts = 0;
        for (var index = 0; index < snapshot.Events.Count; index++)
        {
            var item = snapshot.Events[index];
            if (item is null || item.Sequence != index + 1 ||
                !Enum.IsDefined(item.Previous) || !Enum.IsDefined(item.Current) ||
                item.Previous != previousStatus || item.Current == item.Previous ||
                item.Current == WorkTaskStatus.Queued ||
                !(item.Current == WorkTaskStatus.Claimed &&
                    item.Previous is not (WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed)) &&
                (!WorkTaskCoordinator.IsAllowedTransition(item.Previous, item.Current) &&
                    !WorkTaskCoordinator.IsValidCodeRedRecoveryEvent(item) &&
                    !WorkTaskCoordinator.IsValidLegacyReceiptRecoveryEvent(item)) ||
                (WorkTaskCoordinator.HasRecoveryMetadata(item) &&
                    (WorkTaskCoordinator.HasApprovalMetadata(item) ||
                     !WorkTaskCoordinator.IsValidCodeRedRecoveryEvent(item))) ||
                (WorkTaskCoordinator.HasApprovalMetadata(item) &&
                    !WorkTaskCoordinator.IsValidLegacyReceiptRecoveryEvent(item)) ||
                (item.ApprovalReceipt is { } legacyReceipt &&
                    legacyReceipt.TaskId != snapshot.Request.TaskId) ||
                (item.Current == WorkTaskStatus.Investigating && item.Previous == WorkTaskStatus.CodeRed &&
                    !WorkTaskCoordinator.IsValidCodeRedRecoveryEvent(item) &&
                    !WorkTaskCoordinator.IsValidLegacyReceiptRecoveryEvent(item)) ||
                (item.ApprovalReceiptId is not null && snapshot.Events.Take(index)
                    .Any(previous => previous.ApprovalReceiptId == item.ApprovalReceiptId)) ||
                string.IsNullOrWhiteSpace(item.AgentId) || item.AgentId.Length > 128 ||
                item.AgentId.Any(char.IsControl) || item.LeaseId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 2048 ||
                item.Reason.Any(char.IsControl) || item.OccurredAt == default ||
                (item.CiRunId.HasValue != item.CiRunAttempt.HasValue) ||
                (item.CiRunId.HasValue != (item.EvidencePayloadSha256 is not null)) ||
                (item.CiRunId is <= 0) || (item.CiRunAttempt is <= 0) ||
                (item.EvidencePayloadSha256 is not null &&
                    (item.EvidencePayloadSha256.Length != 64 ||
                     !item.EvidencePayloadSha256.All(Uri.IsHexDigit))) ||
                (item.Current is WorkTaskStatus.ReviewReady or WorkTaskStatus.RepairRequired &&
                    (!item.CiRunId.HasValue || !item.Reason.StartsWith(
                        item.Current == WorkTaskStatus.ReviewReady
                            ? "repair.validation-passed: "
                            : "repair.actionable-failure: ", StringComparison.Ordinal))) ||
                (previousTime.HasValue && item.OccurredAt < previousTime.Value))
                throw new ArgumentException("Task event history must be contiguous, valid, and chronological.", nameof(snapshot));

            if (item.Current == WorkTaskStatus.Validating)
                validationAttempts++;
            previousStatus = item.Current;
            previousTime = item.OccurredAt;
        }

        var finalEvent = snapshot.Events[^1];
        if (snapshot.Status != previousStatus || snapshot.Attempt != validationAttempts)
            throw new ArgumentException("Snapshot status and attempt count must match its event history.", nameof(snapshot));
        if (snapshot.Lease is { } activeLease)
        {
            if (snapshot.Status is WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed ||
                activeLease.AgentId != finalEvent.AgentId || activeLease.LeaseId != finalEvent.LeaseId ||
                activeLease.ExpiresAt <= finalEvent.OccurredAt)
                throw new ArgumentException("Active lease must match the latest non-terminal event.", nameof(snapshot));
        }
        else if (snapshot.Status is not (WorkTaskStatus.HandedOff or WorkTaskStatus.Stopped or WorkTaskStatus.Failed))
        {
            throw new ArgumentException("A non-terminal task snapshot must retain its agent lease.", nameof(snapshot));
        }
    }
}
