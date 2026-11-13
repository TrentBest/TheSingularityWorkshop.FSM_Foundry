using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

public sealed record CiAiRepairFeedbackResult(
    WorkTaskSnapshot Snapshot,
    CiAiConsumedTestResult? Consumed,
    bool IsDuplicateDelivery);


/// <summary>
/// Result of consuming a verified CiAi test-result artifact. The attempt is constructed
/// only from CiAi's versioned report verdict; callers cannot supply verdict booleans.
/// HeadlessAi context remains untrusted proposal input.
/// </summary>
/// <summary>
/// Host-accepted, byte-exact CiAi report context for one subsequent HeadlessAi repair turn.
/// The report remains opaque to HeadlessAi; Foundry preserves the accepted bytes and trusted lineage.
/// </summary>
public sealed class FoundryAcceptedCiAiReportContext
{
    public const string RequiredSchema = "ciai.test-results.v1";
    public const int MaximumReportBytes = 1024 * 1024;

    public string Repository { get; }
    public string WorkflowPath { get; }
    public DateTimeOffset ReportObservedAt { get; }
    public string SchemaVersion { get; }
    public string Verdict { get; }
    public string ReportIdentity { get; }
    public string NoticeIdentitySha256 { get; }
    public string CanonicalReportJson { get; }
    public string ReportSha256 { get; }
    public string WorkflowRunId { get; }
    public int RunAttempt { get; }
    public string CandidateCommitSha { get; }
    /// <summary>Host-observed nested source artifact provenance, distinct from the outer report artifact.</summary>
    public CiAiHostObservedSourceArtifactBinding? SourceArtifactBinding { get; }
    public string? SourceArtifactIdentitySha256 => SourceArtifactBinding?.IdentitySha256;

    internal FoundryAcceptedCiAiReportContext(
        string repository,
        string workflowPath,
        DateTimeOffset reportObservedAt,
        string schemaVersion,
        string verdict,
        string reportIdentity,
        string noticeIdentitySha256,
        string canonicalReportJson,
        string reportSha256,
        string workflowRunId,
        int runAttempt,
        string candidateCommitSha,
        CiAiHostObservedSourceArtifactBinding? sourceArtifactBinding)
    {
        Repository = repository;
        WorkflowPath = workflowPath;
        ReportObservedAt = reportObservedAt;
        SchemaVersion = schemaVersion;
        Verdict = verdict;
        ReportIdentity = reportIdentity;
        NoticeIdentitySha256 = noticeIdentitySha256;
        CanonicalReportJson = canonicalReportJson;
        ReportSha256 = reportSha256;
        WorkflowRunId = workflowRunId;
        RunAttempt = runAttempt;
        CandidateCommitSha = candidateCommitSha;
        SourceArtifactBinding = sourceArtifactBinding;
    }

    public void Validate(string repository, string currentCandidateSha, string? priorCandidateSha)
    {
        if (!string.Equals(Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Accepted CiAi report repository does not match the HeadlessAi task.");
        if (SchemaVersion != RequiredSchema ||
            string.IsNullOrWhiteSpace(ReportIdentity) ||
            string.IsNullOrWhiteSpace(CanonicalReportJson) ||
            ReportSha256 is not { Length: 64 } || !ReportSha256.All(Uri.IsHexDigit) ||
            !ReportSha256.Equals(ReportSha256.ToLowerInvariant(), StringComparison.Ordinal) ||
            RunAttempt < 1 || !long.TryParse(WorkflowRunId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var runId) || runId <= 0 ||
            CandidateCommitSha is not { Length: 40 } || !CandidateCommitSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Accepted CiAi report context is malformed or incomplete.");

        if (NoticeIdentitySha256 is not { Length: 64 } || !NoticeIdentitySha256.All(Uri.IsHexDigit) ||
            !string.Equals(NoticeIdentitySha256, NoticeIdentitySha256.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException("Accepted outer notice identity must be a lowercase SHA-256 fingerprint.");

        if (string.IsNullOrWhiteSpace(WorkflowPath) || WorkflowPath.Length > 512 ||
            !WorkflowPath.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            WorkflowPath.Any(char.IsControl) || ReportObservedAt == default)
            throw new InvalidDataException("Accepted CiAi report workflow path or observation timestamp is invalid.");
        if (Verdict is not ("CompletePass" or "ActionableFailure" or "NonActionableFailure" or "IncompleteOrUntrusted"))
            throw new InvalidDataException("Accepted CiAi report verdict is unknown.");
        if ((Verdict is "CompletePass" or "ActionableFailure" or "NonActionableFailure") &&
            SourceArtifactBinding is null)
            throw new InvalidDataException("Complete accepted CiAi evidence requires host-observed nested source-artifact provenance.");

        if (ReportIdentity != "sha256:" + ReportSha256)
            throw new InvalidDataException("Accepted CiAi report identity does not match its SHA-256 digest.");
        if (!string.Equals(CandidateCommitSha, currentCandidateSha, StringComparison.OrdinalIgnoreCase) &&
            (priorCandidateSha is null ||
             !string.Equals(CandidateCommitSha, priorCandidateSha, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Accepted CiAi report candidate does not match the current or prior candidate.");
        var sourceBinding = SourceArtifactBinding;
        var sourceIdentity = sourceBinding?.IdentitySha256;
        if (sourceBinding is not null &&
            (!string.Equals(sourceBinding.Repository, Repository, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(sourceBinding.RunId.ToString(CultureInfo.InvariantCulture), WorkflowRunId, StringComparison.Ordinal) ||
             sourceBinding.RunAttempt != RunAttempt ||
             !string.Equals(sourceBinding.CandidateSha, CandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(sourceBinding.WorkflowPath, WorkflowPath, StringComparison.Ordinal) ||
             sourceBinding.JobCompletedAt > ReportObservedAt ||
             sourceBinding.ArtifactCreatedAt > ReportObservedAt ||
             sourceBinding.ArtifactExpiresAt <= ReportObservedAt ||
             sourceIdentity is not { Length: 64 } || !sourceIdentity.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Accepted CiAi report nested source-artifact provenance does not match the outer report run lineage.");
        var bytes = new UTF8Encoding(false, true).GetBytes(CanonicalReportJson);
        if (bytes.Length > MaximumReportBytes)
            throw new InvalidDataException("Accepted CiAi report exceeds the shared one-megabyte limit.");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(ReportSha256)))
            throw new InvalidDataException("Accepted CiAi report bytes do not match their trusted digest.");
    }
}

public sealed record CiAiConsumedTestResult(
    WorkTaskRepairAttempt Attempt,
    FoundryHeadlessAiEvidence HeadlessAiEvidence,
    string Verdict,
    IReadOnlyList<string> ReasonCodes,
    string ReportSha256,
    FoundryAcceptedCiAiReportContext AcceptedReport);

/// <summary>
/// Consumes the versioned ciai.test-results.v1 JSON report after Foundry has authenticated
/// the sender, validated its dispatch notice, retrieved the artifact, and verified its bytes.
/// This adapter verifies the report's binding to Foundry's persisted dispatch intent; it does
/// not parse TRX, reinterpret test failures, or decide whether diagnostics are actionable.
/// </summary>
public static class CiAiTestResultReportConsumer
{
    public const int MaximumReportBytes = 1024 * 1024;
    private const string Schema = "ciai.test-results.v1";

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "contractVersion", "schema", "repository", "candidateRef", "candidateSha",
        "workflowPath", "workflowDefinitionRef", "pullRequestNumber", "pullRequestHeadRef",
        "pullRequestHeadSha", "pullRequestBaseRef", "pullRequestBaseSha", "runEvidenceUrl",
        "jobEvidenceUrl", "workflowDefinitionSha", "eventName", "eventRef", "eventSha",
        "runId", "runAttempt", "jobId", "jobName", "jobStatus", "jobConclusion",
        "requiredCheckStatus", "verdict", "reasonCodes", "testFramework", "format",
        "sourceArtifactId", "sourceArtifactName", "sourceJobKey", "providerArchiveDigest",
        "archiveSizeBytes", "artifactCreatedAt", "artifactExpiresAt", "payloadSha256",
        "payloadSizeBytes", "total", "passed", "failed", "skipped", "other", "results",
        "observedAt"
    };

    /// <summary>
    /// Validates the report against the task and exact run binding retained in the verified
    /// artifact notice, then derives the durable attempt and bounded HeadlessAi evidence.
    /// </summary>
    public static CiAiConsumedTestResult Consume(
        FoundryVerifiedCiAiReportArtifact artifact,
        WorkTaskSnapshot task,
        CiAiHostObservedSourceArtifactBinding? sourceArtifactBinding,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(task);
        if (now == default)
            throw new ArgumentException("A current consumption time is required.", nameof(now));

        var request = artifact.Request;
        var mapping = request.Mapping.Validate();
        var run = request.RunBinding
            ?? throw new InvalidOperationException("CiAi report cannot be consumed before the dispatch is bound to an exact GitHub run.");
        sourceArtifactBinding?.ValidateAgainst(mapping, run, now);
        if (request.Cancelled || now < mapping.CreatedAt || now > mapping.ExpiresAt ||
            artifact.ExpiresAt <= now)
            throw new InvalidOperationException("CiAi report artifact or dispatch intent is cancelled, expired, or outside its validity window.");
        if (!string.Equals(artifact.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("CiAi report artifact media type must be application/json.");
        run.ValidateAgainst(mapping);
        if (artifact.ProviderRunId is not null &&
            !string.Equals(artifact.ProviderRunId, run.RunId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice provider run identity does not match the persisted GitHub run binding.");

        if (task.Request.TaskId != mapping.TaskId ||
            task.Request.CorrelationId != mapping.CorrelationId ||
            !string.Equals(task.Request.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi report dispatch mapping does not identify the supplied Foundry task.");
        if (task.Status != WorkTaskStatus.Validating)
            throw new InvalidOperationException("A new CiAi report can be applied only to the task's active validation attempt; CODE RED recovery requires the host approval path first.");
        if (task.Attempt < 1 || task.Events.Count == 0)
            throw new InvalidOperationException("Foundry task has no durable validation-attempt history.");

        var candidateBranch = mapping.CandidateRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? mapping.CandidateRef["refs/heads/".Length..]
            : throw new InvalidOperationException("Persisted CiAi candidate ref is not a branch ref.");
        if (candidateBranch is "master" or "development")
            throw new UnauthorizedAccessException("CiAi evidence cannot target a canonical branch.");

        var validatingEvent = task.Events.LastOrDefault(item => item.Current == WorkTaskStatus.Validating)
            ?? throw new InvalidOperationException("Foundry task has no durable Validating transition for this CI attempt.");
        var taskStartedAt = task.Events[0].OccurredAt;
        if (validatingEvent.CiRunId.HasValue &&
            (validatingEvent.CiRunId != run.RunId || validatingEvent.CiRunAttempt != run.RunAttempt))
            throw new InvalidOperationException("The exact GitHub run does not match the task's persisted validation transition.");

        var bytes = artifact.Bytes;
        if (bytes.IsEmpty || bytes.Length > MaximumReportBytes || bytes.Length != artifact.SizeBytes)
            throw new InvalidDataException("CiAi report is empty, oversized, or does not match the verified artifact length.");
        var reportDigest = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(reportDigest), Convert.FromHexString(artifact.Digest)))
            throw new InvalidDataException("CiAi report bytes do not match the verified artifact digest.");

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("CiAi test-result report must be a JSON object.");
        ValidateUniqueProperties(root);
        var properties = root.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        if (properties.Keys.Any(name => !RootProperties.Contains(name)) ||
            RootProperties.Any(name => !properties.ContainsKey(name)))
            throw new FormatException("CiAi test-result report has unknown or missing v1 properties.");

        if (ReadInt32(properties, "contractVersion") != 1 || ReadText(properties, "schema", 64) != Schema)
            throw new FormatException("CiAi test-result report contract version or schema is unsupported.");

        RequireEqual(ReadText(properties, "repository", 256), mapping.Repository, "repository");
        RequireEqual(ReadText(properties, "candidateRef", 255), mapping.CandidateRef, "candidate ref");
        RequireEqualSha(ReadText(properties, "candidateSha", 40), mapping.CandidateSha, "candidate SHA");
        RequireEqual(ReadText(properties, "workflowPath", 512), mapping.WorkflowPath, "workflow path");
        RequireEqual(ReadText(properties, "workflowDefinitionRef", 255), mapping.TrustedWorkflowRef, "trusted workflow ref");
        RequireEqualSha(ReadText(properties, "workflowDefinitionSha", 40), mapping.TrustedWorkflowSha, "trusted workflow SHA");
        RequireEqual(ReadText(properties, "eventName", 64), run.Event, "event name");
        RequireEqual(ReadText(properties, "eventRef", 255), mapping.TrustedWorkflowRef, "event ref");
        RequireEqualSha(ReadText(properties, "eventSha", 40),
            run.EventSha ?? mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha, "event SHA");
        if (ReadInt64(properties, "runId") != run.RunId ||
            ReadInt32(properties, "runAttempt") != run.RunAttempt)
            throw new UnauthorizedAccessException("CiAi report run identity does not match the exact persisted GitHub run binding.");
        var jobId = ReadInt64(properties, "jobId");
        var jobName = ReadText(properties, "jobName", 200);
        if (jobId <= 0 || jobName != mapping.JobName)
            throw new UnauthorizedAccessException("CiAi report job identity does not match the registered workflow job.");

        var runUrl = ReadGitHubEvidenceUrl(properties, "runEvidenceUrl",
            $"/{mapping.Repository}/actions/runs/{run.RunId}");
        _ = ReadGitHubEvidenceUrl(properties, "jobEvidenceUrl",
            $"/{mapping.Repository}/actions/runs/{run.RunId}/job/{jobId}");

        if (ReadText(properties, "jobStatus", 64) != "completed")
            throw new InvalidDataException("CiAi report job is not complete.");
        var jobConclusion = OptionalText(properties, "jobConclusion", 64);
        if (string.IsNullOrWhiteSpace(jobConclusion))
            jobConclusion = null;
        var requiredCheckStatus = ReadText(properties, "requiredCheckStatus", 64);
        var verdict = ReadText(properties, "verdict", 64);
        ValidateVerdictShape(verdict, requiredCheckStatus, jobConclusion);

        var observedAt = ReadTimestamp(properties, "observedAt");
        if (observedAt < run.BoundAt || observedAt > now || observedAt > artifact.ExpiresAt)
            throw new InvalidDataException("CiAi report observation time is outside the bound run and artifact validity window.");

        // The trusted host adapter is the nested source-artifact attestor. Compare every
        // report claim to its independent provider/job/archive observation; do not confuse
        // this nested TRX archive identity with the outer JSON report artifact verified by the notice.
        if (verdict is "CompletePass" or "ActionableFailure" or "NonActionableFailure")
        {
            if (sourceArtifactBinding is null)
                throw new UnauthorizedAccessException("A host-observed source-artifact binding is required before accepting complete CI evidence.");
            ValidateSourceArtifactMetadata(properties, mapping, sourceArtifactBinding,
                jobId, jobName, observedAt);
        }


        var reasonCodes = ReadReasonCodes(properties);
        var (failedResultDigests, failedSummary, counts) = ReadAndValidateResults(properties);
        ValidateResultCounters(properties, counts);
        ValidateVerdictResults(verdict, counts, failedSummary, failedResultDigests);
        var evidenceComplete = verdict != "IncompleteOrUntrusted";
        var successful = verdict == "CompletePass";
        var actionable = verdict == "ActionableFailure";

        var diagnosticsFingerprint = actionable
            ? HashText(string.Join("\n", reasonCodes.Order(StringComparer.Ordinal)
                .Concat(failedResultDigests.Order(StringComparer.Ordinal))))
            : null;
        var evidencePayloadSha = reportDigest;

        var attempt = new WorkTaskRepairAttempt(
            WorkTaskRepairAttempt.CurrentSchemaVersion,
            mapping.TaskId,
            mapping.CorrelationId,
            mapping.Repository,
            candidateBranch,
            mapping.WorkflowPath,
            taskStartedAt,
            task.Attempt,
            mapping.CandidateSha,
            run.RunId,
            run.RunAttempt,
            evidencePayloadSha,
            evidenceComplete,
            successful,
            actionable,
            diagnosticsFingerprint,
            validatingEvent.OccurredAt,
            observedAt)
        {
            SourceArtifactIdentitySha256 = sourceArtifactBinding?.IdentitySha256
        }.Validate();

        var summary = BuildBoundedSummary(verdict, reasonCodes, failedSummary);
        var headlessEvidence = new FoundryHeadlessAiEvidence
        {
            Repository = mapping.Repository,
            CommitSha = mapping.CandidateSha,
            WorkflowRunId = run.RunId.ToString(CultureInfo.InvariantCulture),
            Attempt = run.RunAttempt,
            WorkflowName = mapping.WorkflowPath,
            JobName = mapping.JobName,
            Conclusion = jobConclusion ?? "unknown",
            SourceUrl = runUrl.AbsoluteUri,
            ObservedAt = observedAt,
            BoundedSummary = summary
        };
        headlessEvidence.Validate(mapping.Repository, mapping.CandidateSha, null);

        var acceptedReport = new FoundryAcceptedCiAiReportContext(
            mapping.Repository,
            mapping.WorkflowPath,
            observedAt,
            Schema,
            verdict,
            "sha256:" + reportDigest,
            artifact.NoticeIdentitySha256,
            new UTF8Encoding(false, true).GetString(bytes.Span),
            reportDigest,
            run.RunId.ToString(CultureInfo.InvariantCulture),
            run.RunAttempt,
            mapping.CandidateSha,
            sourceArtifactBinding);
        acceptedReport.Validate(mapping.Repository, mapping.CandidateSha, null);

        return new CiAiConsumedTestResult(attempt, headlessEvidence, verdict,
            Array.AsReadOnly(reasonCodes.ToArray()), reportDigest, acceptedReport);
    }

    private static void ValidateSourceArtifactMetadata(
        Dictionary<string, JsonElement> properties,
        CiAiRequestTaskMapping mapping,
        CiAiHostObservedSourceArtifactBinding sourceBinding,
        long reportJobId,
        string reportJobName,
        DateTimeOffset observedAt)
    {
        var sourceArtifactId = ReadInt64(properties, "sourceArtifactId");
        var sourceArtifactName = ReadText(properties, "sourceArtifactName", 200);
        var sourceJobKey = ReadText(properties, "sourceJobKey", 100);
        var archiveDigest = ReadText(properties, "providerArchiveDigest", 71);
        var archiveSize = ReadInt64(properties, "archiveSizeBytes");
        var artifactCreatedAt = ReadTimestamp(properties, "artifactCreatedAt");
        var artifactExpiresAt = ReadTimestamp(properties, "artifactExpiresAt");
        var payloadDigest = ReadText(properties, "payloadSha256", 64);
        var payloadSize = ReadInt64(properties, "payloadSizeBytes");

        if (sourceArtifactId <= 0 || string.IsNullOrWhiteSpace(sourceArtifactName) ||
            (string.IsNullOrWhiteSpace(mapping.JobKey) ||
             !string.Equals(sourceJobKey, mapping.JobKey, StringComparison.Ordinal)) ||
            reportJobId != sourceBinding.JobId ||
            !string.Equals(reportJobName, sourceBinding.JobName, StringComparison.Ordinal) ||
            sourceArtifactId != sourceBinding.ArtifactId ||
            !string.Equals(sourceArtifactName, sourceBinding.ArtifactName, StringComparison.Ordinal) ||
            !string.Equals(sourceJobKey, sourceBinding.JobKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi report source artifact ID/name or job ID/name/key does not match the independent host observation.");

        if (archiveDigest.Length != 71 ||
            !archiveDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !archiveDigest.AsSpan(7).ToString().All(Uri.IsHexDigit) ||
            archiveSize is <= 0 or > 20 * 1024 * 1024)
            throw new InvalidDataException("CiAi source archive digest or size is invalid.");

        if (payloadDigest.Length != 64 || !payloadDigest.All(Uri.IsHexDigit) ||
            payloadSize is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("CiAi source payload digest or size is invalid.");

        if (!string.Equals(archiveDigest, sourceBinding.ProviderArchiveDigest, StringComparison.OrdinalIgnoreCase) ||
            archiveSize != sourceBinding.ArchiveSizeBytes ||
            artifactCreatedAt != sourceBinding.ArtifactCreatedAt ||
            artifactExpiresAt != sourceBinding.ArtifactExpiresAt ||
            !string.Equals(payloadDigest, sourceBinding.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            payloadSize != sourceBinding.PayloadSizeBytes)
            throw new UnauthorizedAccessException("CiAi report archive/payload digest, size, or timestamps do not match the independent host-observed source binding.");

        if (sourceBinding.JobCompletedAt > observedAt ||
            artifactCreatedAt > observedAt || artifactExpiresAt <= artifactCreatedAt || artifactExpiresAt < observedAt)
            throw new InvalidDataException("CiAi source artifact timestamps are outside the exact observation and retention window.");
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FormatException("CiAi report contains a duplicate JSON property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateUniqueProperties(item);
        }
    }

    private static void ValidateVerdictShape(string verdict, string requiredCheckStatus, string? conclusion)
    {
        var valid = verdict switch
        {
            "CompletePass" => requiredCheckStatus == "Passed" && conclusion == "success",
            "ActionableFailure" or "NonActionableFailure" =>
                requiredCheckStatus == "Failed" && conclusion == "failure",
            "IncompleteOrUntrusted" => requiredCheckStatus is "Passed" or "Failed" or "Incomplete" or "NotRequired",
            _ => false
        };
        if (!valid)
            throw new InvalidDataException("CiAi verdict, required-check status, and observed job conclusion are inconsistent.");
    }

    private static List<string> ReadReasonCodes(Dictionary<string, JsonElement> properties)
    {
        var element = properties["reasonCodes"];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 32)
            throw new FormatException("CiAi report reason codes must contain between one and 32 entries.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new FormatException("CiAi report reason codes must be strings.");
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ||
                values.Contains(value, StringComparer.Ordinal))
                throw new FormatException("CiAi report reason code is empty, duplicated, or outside its bounds.");
            values.Add(value);
        }
        return values;
    }

    private sealed record CiAiResultCounts(int Total, int Passed, int Failed, int Skipped, int Other);

    private static (List<string> Digests, List<string> Summary, CiAiResultCounts Counts)
        ReadAndValidateResults(Dictionary<string, JsonElement> properties)
    {
        var results = properties["results"];
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 10_000)
            throw new FormatException("CiAi report test results must be a bounded array.");

        var digests = new List<string>();
        var summaries = new List<string>();
        var counts = new CiAiResultCounts(results.GetArrayLength(), 0, 0, 0, 0);
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var other = 0;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("CiAi report test result must be an object.");
            ValidateUniqueProperties(item);
            var values = item.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value, StringComparer.Ordinal);
            if (!values.TryGetValue("outcome", out var outcomeElement) ||
                outcomeElement.ValueKind != JsonValueKind.String)
                throw new FormatException("CiAi test result outcome must be a string.");

            var outcome = outcomeElement.GetString();
            switch (outcome)
            {
                case "Passed":
                    passed++;
                    break;
                case "Failed":
                    failed++;
                    break;
                case "Skipped":
                    skipped++;
                    break;
                case "Other":
                    other++;
                    break;
                default:
                    throw new FormatException("CiAi test result outcome is not a recognized normalized outcome.");
            }

            if (outcome != "Failed")
                continue;

            if (!values.TryGetValue("recordSha256", out var digest) ||
                digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 64 } digestText ||
                !digestText.All(Uri.IsHexDigit))
                throw new FormatException("CiAi failed test record is missing its SHA-256 identity.");
            digests.Add(digestText.ToLowerInvariant());

            var name = OptionalText(values, "testName", 512);
            var message = OptionalText(values, "message", 4_000);
            var stack = OptionalText(values, "stackTrace", 8_000);
            if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(stack))
                throw new FormatException("CiAi failed test record is missing actionable diagnostic text.");
            summaries.Add($"{name ?? "Failed test"}: {message ?? stack}");
        }

        if (digests.Distinct(StringComparer.Ordinal).Count() != digests.Count)
            throw new FormatException("CiAi report contains duplicate failed-test record digests.");

        counts = counts with { Passed = passed, Failed = failed, Skipped = skipped, Other = other };
        return (digests, summaries, counts);
    }

    private static void ValidateResultCounters(Dictionary<string, JsonElement> properties,
        CiAiResultCounts actual)
    {
        var declared = new CiAiResultCounts(
            ReadNonNegativeCount(properties, "total"),
            ReadNonNegativeCount(properties, "passed"),
            ReadNonNegativeCount(properties, "failed"),
            ReadNonNegativeCount(properties, "skipped"),
            ReadNonNegativeCount(properties, "other"));

        if (declared != actual ||
            (long)declared.Passed + declared.Failed + declared.Skipped + declared.Other != declared.Total)
            throw new InvalidDataException("CiAi report aggregate test counters do not match the individual result records.");
    }

    private static int ReadNonNegativeCount(Dictionary<string, JsonElement> properties, string name)
    {
        var value = ReadInt32(properties, name);
        if (value < 0)
            throw new InvalidDataException($"CiAi report test counter '{name}' cannot be negative.");
        return value;
    }

    private static void ValidateVerdictResults(string verdict, CiAiResultCounts counts,
        IReadOnlyList<string> failedSummaries, IReadOnlyList<string> failedDigests)
    {
        if (verdict == "CompletePass" && (counts.Failed != 0 || counts.Other != 0))
            throw new InvalidDataException("CiAi CompletePass report cannot contain failed or unknown test outcomes.");
        if (verdict == "ActionableFailure")
        {
            if (counts.Failed == 0 || failedDigests.Count != counts.Failed ||
                failedSummaries.Count != counts.Failed)
                throw new InvalidDataException("CiAi ActionableFailure report must contain a diagnostic record for every failed test.");
        }
    }

    private static string BuildBoundedSummary(string verdict, IReadOnlyList<string> reasons,
        IReadOnlyList<string> failures)
    {
        var builder = new StringBuilder();
        builder.Append("CiAi verdict: ").Append(verdict).AppendLine();
        builder.Append("Reason codes: ").Append(string.Join(", ", reasons)).AppendLine();
        foreach (var failure in failures)
        {
            if (builder.Length + failure.Length + 2 > 15_500)
                break;
            builder.Append("- ").AppendLine(failure);
        }
        return builder.ToString();
    }

    private static Uri ReadGitHubEvidenceUrl(Dictionary<string, JsonElement> properties,
        string name, string expectedPath)
    {
        var value = ReadText(properties, name, 2_048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != expectedPath)
            throw new UnauthorizedAccessException($"CiAi report {name} does not identify the exact expected GitHub evidence resource.");
        return uri;
    }

    private static DateTimeOffset ReadTimestamp(Dictionary<string, JsonElement> properties, string name)
    {
        var value = ReadText(properties, name, 64);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) || timestamp == default)
            throw new FormatException($"CiAi report timestamp '{name}' is invalid.");
        return timestamp;
    }

    private static string ReadText(Dictionary<string, JsonElement> properties, string name, int maximum)
    {
        if (!properties.TryGetValue(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
            throw new FormatException($"CiAi report property '{name}' must be a string.");
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new FormatException($"CiAi report property '{name}' is empty or outside its bounds.");
        return value;
    }

    private static string? OptionalText(Dictionary<string, JsonElement> properties, string name, int maximum)
    {
        if (!properties.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new FormatException($"CiAi report property '{name}' must be a string or null.");
        var value = element.GetString();
        if (value is { Length: > 0 } && (value.Length > maximum || value.Any(char.IsControl)))
            throw new FormatException($"CiAi report property '{name}' is outside its bounds.");
        return value;
    }

    private static int ReadInt32(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var element) || !element.TryGetInt32(out var value))
            throw new FormatException($"CiAi report property '{name}' must be a 32-bit integer.");
        return value;
    }

    private static long ReadInt64(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var element) || !element.TryGetInt64(out var value))
            throw new FormatException($"CiAi report property '{name}' must be a 64-bit integer.");
        return value;
    }

    private static void RequireEqual(string actual, string expected, string identity)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"CiAi report {identity} does not match the persisted dispatch intent.");
    }

    private static void RequireEqualSha(string actual, string expected, string identity)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"CiAi report {identity} does not match the persisted dispatch intent.");
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
