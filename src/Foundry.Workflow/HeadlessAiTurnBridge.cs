using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// The provider-neutral invocation data Foundry maps to HeadlessAi's versioned
/// <c>HeadlessAiAgentTurnEnvelope</c>. This port keeps Foundry independent of the
/// HeadlessAi assembly; a host adapter performs the explicit contract mapping.
/// </summary>
public sealed record FoundryHeadlessAiTurnRequest
{
    public required string TaskId { get; init; }
    public required string CorrelationId { get; init; }
    public required string Repository { get; init; }
    public required string AuthorizedBranch { get; init; }
    public required string BaseCommitSha { get; init; }
    public required string CandidateCommitSha { get; init; }
    public required string IssueUrl { get; init; }
    public required int IssueNumber { get; init; }
    public required string Role { get; init; }
    public required string Instructions { get; init; }
    public required string CanonicalPrompt { get; init; }
    public required string ResponseSchema { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? PriorTurnId { get; init; }
    public string? PriorCandidateCommitSha { get; init; }
    public IReadOnlyList<FoundryHeadlessAiEvidence> Evidence { get; init; } =
        Array.Empty<FoundryHeadlessAiEvidence>();
    /// <summary>Exact host-accepted CiAi report bytes and lineage for a repair turn.</summary>
    public FoundryAcceptedCiAiReportContext? AcceptedCiAiReport { get; init; }

    public void Validate(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(TaskId) || TaskId.Length > 128 || TaskId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(CorrelationId) || CorrelationId.Length > 128 || CorrelationId.Any(char.IsControl))
            throw new ArgumentException("Task and correlation identities are required and bounded.");
        if (string.IsNullOrWhiteSpace(Repository) || Repository.Length > 256 ||
            Repository.Split('/') is not { Length: 2 } parts ||
            parts.Any(part => string.IsNullOrWhiteSpace(part) ||
                part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Repository must use owner/name identity.", nameof(Repository));
        ValidateSha(BaseCommitSha, nameof(BaseCommitSha));
        ValidateSha(CandidateCommitSha, nameof(CandidateCommitSha));
        if (string.IsNullOrWhiteSpace(AuthorizedBranch) ||
            string.Equals(AuthorizedBranch, "master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(AuthorizedBranch, "development", StringComparison.OrdinalIgnoreCase) ||
            AuthorizedBranch == "@" || AuthorizedBranch.StartsWith('/') || AuthorizedBranch.EndsWith('/') ||
            AuthorizedBranch.Contains("..", StringComparison.Ordinal) ||
            AuthorizedBranch.Contains('\\') || AuthorizedBranch.Contains("@{", StringComparison.Ordinal) ||
            AuthorizedBranch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) ||
                c is '~' or '^' or ':' or '?' or '*' or '[' or ']') ||
            AuthorizedBranch.Split('/').Any(segment => segment.Length == 0 ||
                segment.StartsWith('.') || segment.EndsWith('.') ||
                segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("HeadlessAi turns must target an isolated Git-valid feature branch.", nameof(AuthorizedBranch));
        if (string.IsNullOrWhiteSpace(IssueUrl) || IssueUrl.Length > 512 ||
            !Uri.TryCreate(IssueUrl, UriKind.Absolute, out var issueUri) ||
            issueUri.Scheme != Uri.UriSchemeHttps || issueUri.Host != "github.com" || !issueUri.IsDefaultPort ||
            !string.IsNullOrEmpty(issueUri.UserInfo) || !string.IsNullOrEmpty(issueUri.Query) ||
            !string.IsNullOrEmpty(issueUri.Fragment) ||
            issueUri.AbsolutePath != $"/{parts[0]}/{parts[1]}/issues/{IssueNumber}" || IssueNumber <= 0)
            throw new ArgumentException("Issue URL must be a clean HTTPS GitHub issue URL for the task repository.", nameof(IssueUrl));
        if (string.IsNullOrWhiteSpace(Role) || Role.Length > 128 ||
            string.IsNullOrWhiteSpace(Instructions) || Instructions.Length > 16_384 ||
            string.IsNullOrWhiteSpace(CanonicalPrompt) || CanonicalPrompt.Length > 64_000 ||
            string.IsNullOrWhiteSpace(ResponseSchema) || ResponseSchema.Length > 16_384)
            throw new ArgumentException("Role, instructions, prompt, and response schema must be non-empty and bounded.");
        if (ExpiresAt <= now || ExpiresAt - now > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(ExpiresAt), "Turn expiry must be within the next two hours.");
        if (string.IsNullOrWhiteSpace(IdempotencyKey) || IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("A bounded host-generated idempotency key is required.", nameof(IdempotencyKey));
        if ((PriorTurnId is null) != (PriorCandidateCommitSha is null))
            throw new ArgumentException("Resumable turns must provide both the prior turn ID and prior candidate SHA.");
        if (PriorTurnId is not null && (string.IsNullOrWhiteSpace(PriorTurnId) || PriorTurnId.Length > 128 || PriorTurnId.Any(char.IsControl)))
            throw new ArgumentException("Prior turn identity must be non-empty, control-free, and at most 128 characters.", nameof(PriorTurnId));
        if (PriorCandidateCommitSha is not null)
            ValidateSha(PriorCandidateCommitSha, nameof(PriorCandidateCommitSha));
        ArgumentNullException.ThrowIfNull(Evidence);
        if (Evidence.Count > 64)
            throw new ArgumentException("At most 64 CI evidence references may be supplied.", nameof(Evidence));
        foreach (var item in Evidence)
            item.Validate(Repository, CandidateCommitSha, PriorCandidateCommitSha);
        if (AcceptedCiAiReport is not null)
        {
            AcceptedCiAiReport.Validate(Repository, CandidateCommitSha, PriorCandidateCommitSha);
            if (Role == "repair" &&
                (AcceptedCiAiReport.Verdict != "ActionableFailure" || AcceptedCiAiReport.SourceArtifactBinding is null))
                throw new ArgumentException("HeadlessAi repair turns require an actionable report plus host-observed nested source-artifact provenance.", nameof(AcceptedCiAiReport));
            if (!Evidence.Any(item =>
                    item.WorkflowRunId == AcceptedCiAiReport.WorkflowRunId &&
                    item.Attempt == AcceptedCiAiReport.RunAttempt &&
                    string.Equals(item.CommitSha, AcceptedCiAiReport.CandidateCommitSha, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Accepted CiAi report must have a matching exact-run evidence reference.", nameof(AcceptedCiAiReport));
        }
    }

    private static void ValidateSha(string? value, string name)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal commit SHA is required.", name);
    }

    internal static string CreateIdempotencyKey(WorkTaskSnapshot task, string branch,
        string candidateSha, string role, string instructions, string canonicalPrompt,
        string responseSchema, string? priorTurnId, string? priorCandidateCommitSha,
        IReadOnlyList<FoundryHeadlessAiEvidence> evidence,
        FoundryAcceptedCiAiReportContext? acceptedCiAiReport)
    {
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            task.Request.TaskId,
            task.Request.CorrelationId,
            task.Request.Repository,
            task.Request.IssueNumber,
            task.Request.BaseSha,
            branch,
            candidateSha,
            role,
            instructions,
            canonicalPrompt,
            responseSchema,
            priorTurnId,
            priorCandidateCommitSha,
            evidence,
            acceptedReportIdentity = acceptedCiAiReport?.ReportIdentity,
            acceptedReportSha256 = acceptedCiAiReport?.ReportSha256,
            acceptedReportRunId = acceptedCiAiReport?.WorkflowRunId,
            acceptedReportRunAttempt = acceptedCiAiReport?.RunAttempt,
            acceptedReportCandidateSha = acceptedCiAiReport?.CandidateCommitSha,
            acceptedSourceArtifactIdentitySha256 = acceptedCiAiReport?.SourceArtifactIdentitySha256
        });
        return "foundry-" + Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }
}

/// <summary>CI evidence passed to HeadlessAi as untrusted, provenance-bound context.</summary>
public sealed record FoundryHeadlessAiEvidence
{
    public required string Repository { get; init; }
    public required string CommitSha { get; init; }
    public required string WorkflowRunId { get; init; }
    public required int Attempt { get; init; }
    public required string WorkflowName { get; init; }
    public string? JobName { get; init; }
    public string? StepName { get; init; }
    public required string Conclusion { get; init; }
    public required string SourceUrl { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? BoundedSummary { get; init; }

    internal void Validate(string repository, string candidateSha, string? priorCandidateSha)
    {
        if (!string.Equals(Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CI evidence repository does not match the task repository.");
        if (!string.Equals(CommitSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            (priorCandidateSha is null ||
             !string.Equals(CommitSha, priorCandidateSha, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("CI evidence must refer to the current or prior candidate SHA.");
        if (string.IsNullOrWhiteSpace(WorkflowRunId) || WorkflowRunId.Length > 128 ||
            Attempt < 1 || string.IsNullOrWhiteSpace(WorkflowName) || WorkflowName.Length > 256 || ObservedAt == default ||
            JobName?.Length > 256 || StepName?.Length > 256 ||
            string.IsNullOrWhiteSpace(Conclusion) || Conclusion.Length > 64 ||
            string.IsNullOrWhiteSpace(SourceUrl) || SourceUrl.Length > 2_048 ||
            !Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            BoundedSummary?.Length > 16_384)
            throw new ArgumentException("CI evidence is malformed or exceeds its bounded contract.");
    }
}

/// <summary>
/// Normalized HeadlessAi output returned to Foundry. Every proposal field remains untrusted;
/// this result cannot authorize a repository operation or lifecycle transition.
/// </summary>
public sealed record FoundryHeadlessAiTurnResult
{
    public required string TurnId { get; init; }
    public required string TaskId { get; init; }
    public required string CorrelationId { get; init; }
    public required string Repository { get; init; }
    public required string AuthorizedBranch { get; init; }
    public required string BaseCommitSha { get; init; }
    public required string CandidateCommitSha { get; init; }
    public required string RequestFingerprint { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? PriorTurnId { get; init; }
    public string? PriorCandidateCommitSha { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? AcceptedReportSchemaVersion { get; init; }
    public string? AcceptedReportVerdict { get; init; }
    public string? AcceptedReportIdentity { get; init; }
    public string? AcceptedReportNoticeIdentitySha256 { get; init; }
    public string? AcceptedReportSha256 { get; init; }
    public string? AcceptedReportWorkflowRunId { get; init; }
    public int? AcceptedReportRunAttempt { get; init; }
    public string? AcceptedReportCandidateCommitSha { get; init; }
    public string? AcceptedReportSourceArtifactIdentitySha256 { get; init; }
    public string? ErrorCode { get; init; }
    public string? Diagnosis { get; init; }
    public string? PatchIntent { get; init; }
    public IReadOnlyList<string> Questions { get; init; } = Array.Empty<string>();
    public double? Confidence { get; init; }
    public string? Uncertainty { get; init; }
    public string? OperationalStatus { get; init; }
    public string? Diagnostic { get; init; }
    public string? FinishReason { get; init; }
    public string? CurrentAction { get; init; }
    public string? Blocker { get; init; }
    public string? NextExpectedUpdate { get; init; }
    public string? StopOrEscalateReason { get; init; }
    public string? HumanDecisionRequired { get; init; }

    public void ValidateAgainst(FoundryHeadlessAiTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TaskId != request.TaskId || CorrelationId != request.CorrelationId ||
            !string.Equals(Repository, request.Repository, StringComparison.OrdinalIgnoreCase) ||
            AuthorizedBranch != request.AuthorizedBranch ||
            !string.Equals(BaseCommitSha, request.BaseCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CandidateCommitSha, request.CandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
            PriorTurnId != request.PriorTurnId ||
            !string.Equals(PriorCandidateCommitSha, request.PriorCandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
            IdempotencyKey != request.IdempotencyKey)
            throw new InvalidOperationException("HeadlessAi returned a result for different task or candidate provenance.");

        var accepted = request.AcceptedCiAiReport;
        if (accepted is null)
        {
            if (AcceptedReportSchemaVersion is not null || AcceptedReportVerdict is not null || AcceptedReportIdentity is not null ||
                AcceptedReportSha256 is not null || AcceptedReportNoticeIdentitySha256 is not null || AcceptedReportWorkflowRunId is not null ||
                AcceptedReportRunAttempt is not null || AcceptedReportCandidateCommitSha is not null ||
                AcceptedReportSourceArtifactIdentitySha256 is not null)
                throw new InvalidOperationException("HeadlessAi returned accepted-report lineage for a turn that supplied no accepted report.");
        }
        else if (AcceptedReportSchemaVersion != accepted.SchemaVersion ||
            AcceptedReportIdentity != accepted.ReportIdentity ||
            AcceptedReportNoticeIdentitySha256 != accepted.NoticeIdentitySha256 ||
            AcceptedReportSha256 != accepted.ReportSha256 ||
            AcceptedReportWorkflowRunId != accepted.WorkflowRunId ||
            AcceptedReportRunAttempt != accepted.RunAttempt ||
            !string.Equals(AcceptedReportCandidateCommitSha, accepted.CandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(AcceptedReportSourceArtifactIdentitySha256, accepted.SourceArtifactIdentitySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HeadlessAi result accepted-report lineage does not match the exact report supplied to the turn.");
        if (string.IsNullOrWhiteSpace(TurnId) || TurnId.Length > 128 ||
            string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 128 ||
            string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 256 ||
            RequestFingerprint is not { Length: 64 } || !RequestFingerprint.All(Uri.IsHexDigit) ||
            CompletedAt < StartedAt || CompletedAt > request.ExpiresAt || Questions is null || Questions.Count > 64 ||
            Questions.Any(question => string.IsNullOrWhiteSpace(question) || question.Length > 2_048) ||
            Confidence is < 0 or > 1 || (Confidence.HasValue && double.IsNaN(Confidence.Value)))
            throw new InvalidOperationException("HeadlessAi returned a malformed or unbounded result.");
        if (Diagnosis?.Length > 16_384 || PatchIntent?.Length > 16_384 ||
            Uncertainty?.Length > 4_096 || OperationalStatus?.Length > 2_048 ||
            Diagnostic?.Length > 4_096 || FinishReason?.Length > 128 || ErrorCode?.Length > 128 ||
            CurrentAction?.Length > 2_048 ||
            Blocker?.Length > 4_096 || NextExpectedUpdate?.Length > 2_048 ||
            StopOrEscalateReason?.Length > 4_096 || HumanDecisionRequired?.Length > 4_096)
            throw new InvalidOperationException("HeadlessAi returned proposal text beyond the accepted bounds.");
    }
}

/// <summary>One provider invocation. Implementations adapt this port to HeadlessAi's executor.</summary>
/// <summary>
/// Opaque host-prepared snapshot of the exact HeadlessAi request that will be executed.
/// Its fingerprint is the SHA-256 of HeadlessAiAgentTurnRequest.CanonicalPayload.
/// </summary>
public interface IFoundryHeadlessAiPreparedTurn
{
    string RequestFingerprint { get; }
}

/// <summary>
/// Host adapter for HeadlessAi. Prepare must be local and side-effect-free: it creates the
/// immutable HeadlessAi request snapshot once, then ExecuteAsync must execute that same snapshot.
/// </summary>
public interface IHeadlessAiAgentTurnClient
{
    IFoundryHeadlessAiPreparedTurn Prepare(FoundryHeadlessAiTurnRequest request, DateTimeOffset now);
    Task<FoundryHeadlessAiTurnResult> ExecuteAsync(
        FoundryHeadlessAiTurnRequest request, IFoundryHeadlessAiPreparedTurn prepared,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds and invokes one HeadlessAi turn for an already claimed Foundry task in Investigating or RepairRequired.
/// It deliberately performs no repository writes, CI dispatch, retries, or workflow transitions.
/// </summary>
public sealed class FoundryHeadlessAiTurnCoordinator
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly IHeadlessAiAgentTurnClient _client;
    private readonly JsonlHeadlessAiTurnJournal _journal;
    private readonly TimeProvider _timeProvider;

    public FoundryHeadlessAiTurnCoordinator(PersistentWorkTaskCoordinator tasks,
        IHeadlessAiAgentTurnClient client, JsonlHeadlessAiTurnJournal journal,
        TimeProvider? timeProvider = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FoundryHeadlessAiTurnResult> InvokeAsync(string taskId, Guid leaseId,
        string authorizedBranch, string candidateCommitSha, string role, string instructions,
        string canonicalPrompt, string responseSchema, IReadOnlyList<FoundryHeadlessAiEvidence>? evidence = null,
        string? priorTurnId = null, string? priorCandidateCommitSha = null,
        TimeSpan? turnLifetime = null, CancellationToken cancellationToken = default,
        FoundryAcceptedCiAiReportContext? acceptedCiAiReport = null)
    {
        var now = _timeProvider.GetUtcNow();
        var task = _tasks.GetSnapshot(taskId);
        if (task.Lease is not { } lease || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
            throw new UnauthorizedAccessException("A current, unexpired Foundry task lease is required.");
        var expectedStatus = task.Status;
        if (expectedStatus is not (WorkTaskStatus.Investigating or WorkTaskStatus.RepairRequired))
            throw new InvalidOperationException("HeadlessAi may be invoked only during investigation or for a CiAi-authorized repair-required turn.");
        if (expectedStatus == WorkTaskStatus.RepairRequired)
        {
            if (string.IsNullOrWhiteSpace(priorTurnId) ||
                string.IsNullOrWhiteSpace(priorCandidateCommitSha) ||
                evidence is null || evidence.Count == 0 || acceptedCiAiReport is null)
                throw new InvalidOperationException("A repair turn requires prior-turn provenance, accepted report bytes, and exact-run evidence.");
            if (!string.Equals(acceptedCiAiReport.CandidateCommitSha, priorCandidateCommitSha, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The accepted CiAi report must be bound to the prior candidate being repaired.");
            var priorTurn = _journal.GetCompletedTurn(taskId, priorTurnId)
                ?? throw new UnauthorizedAccessException("A repair turn must resume a completed prior turn from the same task journal.");
            if (priorTurn.CorrelationId != task.Request.CorrelationId ||
                !string.Equals(priorTurn.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
                priorTurn.AuthorizedBranch != authorizedBranch ||
                !string.Equals(priorTurn.BaseCommitSha, task.Request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(priorTurn.CandidateCommitSha, priorCandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
                priorTurn.CompletedAt > now)
                throw new UnauthorizedAccessException("The prior HeadlessAi turn does not match this task, branch, base, or prior candidate provenance.");
        }
        var lifetime = turnLifetime ?? TimeSpan.FromMinutes(15);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(turnLifetime));
        var request = new FoundryHeadlessAiTurnRequest
        {
            TaskId = task.Request.TaskId,
            CorrelationId = task.Request.CorrelationId,
            Repository = task.Request.Repository,
            AuthorizedBranch = authorizedBranch,
            BaseCommitSha = task.Request.BaseSha,
            CandidateCommitSha = candidateCommitSha,
            IssueUrl = task.Request.IssueUrl,
            IssueNumber = task.Request.IssueNumber,
            Role = role,
            Instructions = instructions,
            CanonicalPrompt = canonicalPrompt,
            ResponseSchema = responseSchema,
            ExpiresAt = now.Add(lifetime),
            IdempotencyKey = "pending",
            PriorTurnId = priorTurnId,
            PriorCandidateCommitSha = priorCandidateCommitSha,
            Evidence = Array.AsReadOnly((evidence ?? Array.Empty<FoundryHeadlessAiEvidence>()).ToArray()),
            AcceptedCiAiReport = acceptedCiAiReport
        };
        request = request with
        {
            IdempotencyKey = FoundryHeadlessAiTurnRequest.CreateIdempotencyKey(
                task, authorizedBranch, candidateCommitSha, role, instructions,
                canonicalPrompt, responseSchema, priorTurnId, priorCandidateCommitSha, request.Evidence, request.AcceptedCiAiReport)
        };
        request.Validate(now);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = _client.Prepare(request, now)
            ?? throw new InvalidOperationException("The HeadlessAi host adapter returned no prepared request.");
        ValidateRequestFingerprint(prepared.RequestFingerprint);
        var invocationFingerprint = CreateInvocationFingerprint(request);
        var completed = _journal.BeginOrGet(request.TaskId, request.IdempotencyKey, invocationFingerprint);
        if (completed is not null)
        {
            completed.ValidateAgainst(request);
            return completed;
        }

        // A durable pending entry is written before this single provider call. If the call
        // fails or the process stops before completion is journaled, a later invocation is
        // blocked as indeterminate rather than risking an automatic duplicate provider call.
        // The adapter executes the exact immutable snapshot returned by Prepare.
        var result = await _client.ExecuteAsync(request, prepared, cancellationToken).ConfigureAwait(false);
        result.ValidateAgainst(request);
        if (!string.Equals(result.RequestFingerprint, prepared.RequestFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "HeadlessAi returned a result for a different canonical request snapshot.");
        _journal.Complete(request.TaskId, request.IdempotencyKey, invocationFingerprint, result);

        // Preserve a validated provider response before checking the lease again. If the lease
        // expired or was superseded during the provider call, the durable result remains available
        // for host reconciliation, but this stale invocation must not hand it to its caller.
        var current = _tasks.GetSnapshot(taskId);
        var completedAt = _timeProvider.GetUtcNow();
        if (current.Status != expectedStatus ||
            current.Lease is not { } currentLease || currentLease.LeaseId != leaseId ||
            currentLease.ExpiresAt <= completedAt)
            throw new UnauthorizedAccessException(
                "The HeadlessAi result was durably recorded, but the task lease or investigation state became stale during the provider call.");
        return result;
    }

    private static void ValidateRequestFingerprint(string fingerprint)
    {
        if (fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "The prepared HeadlessAi request must expose a full SHA-256 canonical request fingerprint.");
    }

    private static string CreateInvocationFingerprint(FoundryHeadlessAiTurnRequest request)
    {
        // Expiry is a transport freshness bound, not a change in the logical work request.
        // Omitting it lets an exact retry retrieve the prior result while all substantive
        // prompt, scope, candidate, prior-turn, and evidence fields remain fingerprinted.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(request with { ExpiresAt = default });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}
