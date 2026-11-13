using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Immutable, host-approved provenance captured before a CI dispatch is attempted.
/// No credential material is stored here; acknowledgement authentication is by profile ID.
/// </summary>
public sealed record CiAiRequestTaskMapping(
    int SchemaVersion,
    string RequestId,
    string OperationId,
    string IdempotencyKey,
    string TaskId,
    string CorrelationId,
    string Repository,
    string CandidateRef,
    string CandidateSha,
    string WorkflowPath,
    string TrustedWorkflowRef,
    string TrustedWorkflowSha,
    string JobName,
    string ResolutionStepName,
    string VerificationStepName,
    string ProviderIdentity,
    string ArtifactOrigin,
    string AcknowledgementDestination,
    string AcknowledgementAuthProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? TrustedEventSha = null)
{
    /// <summary>Configured required-check key; distinct from the provider's display job name.</summary>
    public string? JobKey { get; init; }
    /// <summary>Exact provider artifact name registered for the required job key.</summary>
    public string? SourceArtifactName { get; init; }

    public const int CurrentSchemaVersion = 1;

    public CiAiRequestTaskMapping Validate()
    {
        static bool Clean(string? value, int max = 256) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        if (SchemaVersion != CurrentSchemaVersion ||
            !Clean(RequestId) || !Clean(OperationId) || !Clean(IdempotencyKey) ||
            !Clean(TaskId) || !Clean(CorrelationId) || !Clean(ProviderIdentity) ||
            !Clean(AcknowledgementAuthProfileId))
            throw new ArgumentException("Request mapping schema and bounded identities are required.");
        var repo = Repository?.Split('/') ?? Array.Empty<string>();
        if (repo.Length != 2 || repo.Any(part => !Clean(part, 100)))
            throw new ArgumentException("Repository must use owner/name identity.", nameof(Repository));
        if (!Clean(CandidateRef, 255) || CandidateRef is "refs/heads/master" or "refs/heads/development" ||
            !CandidateRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new ArgumentException("Candidate ref must identify an isolated branch.", nameof(CandidateRef));
        ValidateSha(CandidateSha, nameof(CandidateSha));
        ValidateSha(TrustedWorkflowSha, nameof(TrustedWorkflowSha));
        if (TrustedEventSha is not null)
            ValidateSha(TrustedEventSha, nameof(TrustedEventSha));
        if (!Clean(WorkflowPath, 512) ||
            !WorkflowPath.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            !(WorkflowPath.EndsWith(".yml", StringComparison.Ordinal) || WorkflowPath.EndsWith(".yaml", StringComparison.Ordinal)) ||
            WorkflowPath.Contains("..", StringComparison.Ordinal) || WorkflowPath.Contains('\\'))
            throw new ArgumentException("Workflow path is invalid.", nameof(WorkflowPath));
        if (!Clean(TrustedWorkflowRef, 255) || TrustedWorkflowRef != "refs/heads/development")
            throw new ArgumentException("Trusted workflow ref must be the registered development ref.", nameof(TrustedWorkflowRef));
        if (!Clean(JobName, 128) || !Clean(ResolutionStepName, 128) || !Clean(VerificationStepName, 128) ||
            (JobKey is not null && (!Clean(JobKey, 100) || !Clean(SourceArtifactName, 200))) ||
            (JobKey is null && SourceArtifactName is not null))
            throw new ArgumentException("Registered job, required-check key, and expected source artifact name must be consistent.");
        ValidateHttpsOrigin(ArtifactOrigin, nameof(ArtifactOrigin), originOnly: true);
        ValidateHttpsOrigin(AcknowledgementDestination, nameof(AcknowledgementDestination), originOnly: false);
        if (CreatedAt == default || ExpiresAt <= CreatedAt || ExpiresAt - CreatedAt > TimeSpan.FromDays(7))
            throw new ArgumentException("Mapping expiry must be after creation and no more than seven days later.");
        return this;
    }

    private static void ValidateSha(string value, string name)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal SHA is required.", name);
    }

    private static void ValidateHttpsOrigin(string value, string name, bool originOnly)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (originOnly && uri.AbsolutePath != "/"))
            throw new ArgumentException("A credential-free HTTPS URI on port 443 is required.", name);
    }
}

/// <summary>Exact GitHub run metadata observed after dispatch; never inferred from callback data.</summary>
public sealed record CiAiDispatchRunBinding(
    long RunId,
    int RunAttempt,
    string Repository,
    string WorkflowPath,
    string WorkflowRef,
    string WorkflowSha,
    string CandidateRef,
    string CandidateSha,
    string Event,
    DateTimeOffset BoundAt,
    string? EventSha = null)
{
    public CiAiDispatchRunBinding ValidateAgainst(CiAiRequestTaskMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var expectedEventSha = mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
        var observedEventSha = EventSha ?? WorkflowSha;
        if (RunId <= 0 || RunAttempt <= 0 || BoundAt == default ||
            !string.Equals(Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            WorkflowPath != mapping.WorkflowPath || WorkflowRef != mapping.TrustedWorkflowRef ||
            !string.Equals(WorkflowSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observedEventSha, expectedEventSha, StringComparison.OrdinalIgnoreCase) ||
            CandidateRef != mapping.CandidateRef ||
            !string.Equals(CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            Event != "workflow_dispatch" || BoundAt < mapping.CreatedAt || BoundAt > mapping.ExpiresAt)
            throw new InvalidOperationException("GitHub run metadata does not match the persisted dispatch intent.");
        return this;
    }
}

public enum CiAiRequestMappingAppendOutcome { Appended, Duplicate, Conflict }

public sealed record CiAiRequestTaskMappingSnapshot(
    CiAiRequestTaskMapping Mapping,
    CiAiDispatchRunBinding? RunBinding,
    bool Cancelled);

/// <summary>
/// Append-only local dispatch-intent/run-binding journal. Single-process only.
/// Persist intent before dispatch; bind once after independently verifying GitHub run metadata.
/// </summary>
public sealed class JsonlCiAiRequestTaskMappingJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, CiAiRequestTaskMappingSnapshot> _latest = new(StringComparer.Ordinal);

    public JsonlCiAiRequestTaskMappingJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) Replay();
    }

    public CiAiRequestMappingAppendOutcome CreateDispatchIntent(CiAiRequestTaskMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate();
        lock (_gate)
        {
            if (_latest.TryGetValue(mapping.RequestId, out var existing))
                return existing.Mapping == mapping
                    ? CiAiRequestMappingAppendOutcome.Duplicate
                    : CiAiRequestMappingAppendOutcome.Conflict;
            if (_latest.Values.Any(item => item.Mapping.OperationId == mapping.OperationId ||
                item.Mapping.IdempotencyKey == mapping.IdempotencyKey))
                return CiAiRequestMappingAppendOutcome.Conflict;
            Append(new CiAiRequestTaskMappingSnapshot(mapping, null, false));
            return CiAiRequestMappingAppendOutcome.Appended;
        }
    }

    public CiAiRequestMappingAppendOutcome BindRun(string requestId, CiAiDispatchRunBinding binding, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (!_latest.TryGetValue(requestId, out var current))
                throw new KeyNotFoundException($"Unknown CI request '{requestId}'.");
            if (current.Cancelled || now == default || now > current.Mapping.ExpiresAt || now < current.Mapping.CreatedAt)
                throw new InvalidOperationException("Dispatch intent is cancelled, expired, or outside its validity window.");
            binding.ValidateAgainst(current.Mapping);
            if (current.RunBinding is not null)
                return current.RunBinding == binding
                    ? CiAiRequestMappingAppendOutcome.Duplicate
                    : CiAiRequestMappingAppendOutcome.Conflict;
            if (_latest.Values.Any(item => item.RunBinding is { } other &&
                string.Equals(other.Repository, binding.Repository, StringComparison.OrdinalIgnoreCase) &&
                other.RunId == binding.RunId && other.RunAttempt == binding.RunAttempt))
                return CiAiRequestMappingAppendOutcome.Conflict;
            Append(current with { RunBinding = binding });
            return CiAiRequestMappingAppendOutcome.Appended;
        }
    }

    public CiAiRequestTaskMappingSnapshot Get(string requestId)
    {
        lock (_gate)
            return _latest.TryGetValue(requestId, out var snapshot)
                ? snapshot
                : throw new KeyNotFoundException($"Unknown CI request '{requestId}'.");
    }

    /// <summary>Callback authorization gate: no callback can resolve until an exact run is bound.</summary>
    public CiAiRequestTaskMappingSnapshot GetActionable(string requestId, DateTimeOffset now)
    {
        var snapshot = Get(requestId);
        if (snapshot.Cancelled || snapshot.RunBinding is null ||
            now == default || now < snapshot.Mapping.CreatedAt || now > snapshot.Mapping.ExpiresAt)
            throw new InvalidOperationException("CI request is not bound to an active exact run.");
        return snapshot;
    }

    public IReadOnlyList<CiAiRequestTaskMappingSnapshot> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values.OrderBy(item => item.Mapping.RequestId, StringComparer.Ordinal).ToArray());
    }

    private void Append(CiAiRequestTaskMappingSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        _latest[snapshot.Mapping.RequestId] = snapshot;
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10)
                throw new InvalidDataException("Request-mapping journal has an incomplete final record.");
            stream.Position = 0;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            try
            {
                var snapshot = JsonSerializer.Deserialize<CiAiRequestTaskMappingSnapshot>(line, JsonOptions)
                    ?? throw new InvalidDataException("Null mapping snapshot.");
                snapshot.Mapping.Validate();
                if (snapshot.RunBinding is not null)
                    snapshot.RunBinding.ValidateAgainst(snapshot.Mapping);
                if (_latest.TryGetValue(snapshot.Mapping.RequestId, out var previous))
                {
                    if (previous.Cancelled || previous.RunBinding is not null ||
                        snapshot.Mapping != previous.Mapping ||
                        (snapshot.RunBinding is null && !snapshot.Cancelled) ||
                        (snapshot.RunBinding is not null && snapshot.Cancelled))
                        throw new InvalidDataException("Request mapping history is conflicting or discontinuous.");
                }
                else if (snapshot.RunBinding is not null || snapshot.Cancelled)
                    throw new InvalidDataException("Request mapping must begin as an unbound dispatch intent.");
                if (_latest.Values.Any(item => item.Mapping.RequestId != snapshot.Mapping.RequestId &&
                    (item.Mapping.OperationId == snapshot.Mapping.OperationId ||
                     item.Mapping.IdempotencyKey == snapshot.Mapping.IdempotencyKey ||
                     (snapshot.RunBinding is not null && item.RunBinding is { } run &&
                      run.Repository == snapshot.RunBinding.Repository && run.RunId == snapshot.RunBinding.RunId &&
                      run.RunAttempt == snapshot.RunBinding.RunAttempt))))
                    throw new InvalidDataException("Request mapping identity or CI run was reused.");
                _latest[snapshot.Mapping.RequestId] = snapshot;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException($"Request-mapping journal record {lineNumber} is invalid.", exception);
            }
        }
    }
}
