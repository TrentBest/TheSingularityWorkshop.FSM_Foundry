using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Result of consuming a verified CiAi test-result artifact. The attempt is constructed
/// only from CiAi's versioned report verdict; callers cannot supply verdict booleans.
/// HeadlessAi context remains untrusted proposal input.
/// </summary>
public sealed record CiAiConsumedTestResult(
    WorkTaskRepairAttempt Attempt,
    FoundryHeadlessAiEvidence HeadlessAiEvidence,
    string Verdict,
    IReadOnlyList<string> ReasonCodes,
    string ReportSha256);

/// <summary>
/// Consumes the versioned ciai.test-results.v1 JSON report after Foundry has authenticated
/// the sender, validated its dispatch notice, retrieved the artifact, and verified its bytes.
/// This adapter verifies the report's binding to Foundry's persisted dispatch intent; it does
/// not parse TRX, reinterpret test failures, or decide whether diagnostics are actionable.
/// </summary>
public static class CiAiTestResultReportConsumer
{
    public const int MaximumReportBytes = 16 * 1024 * 1024;
    private const string Schema = "ciai.test-results.v1";

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "ContractVersion", "Schema", "Repository", "CandidateRef", "CandidateSha",
        "WorkflowPath", "WorkflowDefinitionRef", "PullRequestNumber", "PullRequestHeadRef",
        "PullRequestHeadSha", "PullRequestBaseRef", "PullRequestBaseSha", "RunEvidenceUrl",
        "JobEvidenceUrl", "WorkflowDefinitionSha", "EventName", "EventRef", "EventSha",
        "RunId", "RunAttempt", "JobId", "JobName", "JobStatus", "JobConclusion",
        "RequiredCheckStatus", "Verdict", "ReasonCodes", "TestFramework", "Format",
        "SourceArtifactId", "SourceArtifactName", "SourceJobKey", "ProviderArchiveDigest",
        "ArchiveSizeBytes", "ArtifactCreatedAt", "ArtifactExpiresAt", "PayloadSha256",
        "PayloadSizeBytes", "Total", "Passed", "Failed", "Skipped", "Other", "Results",
        "ObservedAt"
    };

    /// <summary>
    /// Validates the report against the task and exact run binding retained in the verified
    /// artifact notice, then derives the durable attempt and bounded HeadlessAi evidence.
    /// </summary>
    public static CiAiConsumedTestResult Consume(
        CiAiVerifiedArtifactContent artifact,
        WorkTaskSnapshot task,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(task);
        if (now == default)
            throw new ArgumentException("A current consumption time is required.", nameof(now));

        var notice = artifact.Notice;
        var request = notice.Request;
        var mapping = request.Mapping.Validate();
        var run = request.RunBinding
            ?? throw new InvalidOperationException("CiAi report cannot be consumed before the dispatch is bound to an exact GitHub run.");
        if (request.Cancelled || now < mapping.CreatedAt || now > mapping.ExpiresAt ||
            notice.ExpiresAt <= now)
            throw new InvalidOperationException("CiAi report notice or dispatch intent is cancelled, expired, or outside its validity window.");
        run.ValidateAgainst(mapping);
        if (notice.ProviderRunId is not null &&
            !string.Equals(notice.ProviderRunId, run.RunId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("CiAi notice provider run identity does not match the persisted GitHub run binding.");

        if (task.Request.TaskId != mapping.TaskId ||
            task.Request.CorrelationId != mapping.CorrelationId ||
            !string.Equals(task.Request.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi report dispatch mapping does not identify the supplied Foundry task.");
        if (task.Status is not (WorkTaskStatus.Validating or WorkTaskStatus.CodeRed))
            throw new InvalidOperationException("CiAi evidence can be consumed only while the task is validating or under CODE RED review.");
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
        if (bytes.IsEmpty || bytes.Length > MaximumReportBytes || bytes.Length != notice.SizeBytes)
            throw new InvalidDataException("CiAi report is empty, oversized, or does not match the verified notice length.");
        var reportDigest = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(reportDigest), Convert.FromHexString(notice.Digest)))
            throw new InvalidDataException("CiAi report bytes do not match the verified notice digest.");

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

        if (ReadInt32(properties, "ContractVersion") != 1 || ReadText(properties, "Schema", 64) != Schema)
            throw new FormatException("CiAi test-result report contract version or schema is unsupported.");

        RequireEqual(ReadText(properties, "Repository", 256), mapping.Repository, "repository");
        RequireEqual(ReadText(properties, "CandidateRef", 255), mapping.CandidateRef, "candidate ref");
        RequireEqualSha(ReadText(properties, "CandidateSha", 40), mapping.CandidateSha, "candidate SHA");
        RequireEqual(ReadText(properties, "WorkflowPath", 512), mapping.WorkflowPath, "workflow path");
        RequireEqual(ReadText(properties, "WorkflowDefinitionRef", 255), mapping.TrustedWorkflowRef, "trusted workflow ref");
        RequireEqualSha(ReadText(properties, "WorkflowDefinitionSha", 40), mapping.TrustedWorkflowSha, "trusted workflow SHA");
        RequireEqual(ReadText(properties, "EventName", 64), run.Event, "event name");
        RequireEqual(ReadText(properties, "EventRef", 255), mapping.CandidateRef, "event ref");
        RequireEqualSha(ReadText(properties, "EventSha", 40), mapping.CandidateSha, "event SHA");
        if (ReadInt64(properties, "RunId") != run.RunId ||
            ReadInt32(properties, "RunAttempt") != run.RunAttempt)
            throw new UnauthorizedAccessException("CiAi report run identity does not match the exact persisted GitHub run binding.");
        var jobId = ReadInt64(properties, "JobId");
        if (jobId <= 0 || ReadText(properties, "JobName", 200) != mapping.JobName)
            throw new UnauthorizedAccessException("CiAi report job identity does not match the registered workflow job.");

        var runUrl = ReadGitHubEvidenceUrl(properties, "RunEvidenceUrl",
            $"/{mapping.Repository}/actions/runs/{run.RunId}");
        _ = ReadGitHubEvidenceUrl(properties, "JobEvidenceUrl",
            $"/{mapping.Repository}/actions/runs/{run.RunId}/job/{jobId}");

        if (ReadText(properties, "JobStatus", 64) != "completed")
            throw new InvalidDataException("CiAi report job is not complete.");
        var jobConclusion = OptionalText(properties, "JobConclusion", 64);
        if (string.IsNullOrWhiteSpace(jobConclusion))
            jobConclusion = null;
        var requiredCheckStatus = ReadText(properties, "RequiredCheckStatus", 64);
        var verdict = ReadText(properties, "Verdict", 64);
        ValidateVerdictShape(verdict, requiredCheckStatus, jobConclusion);

        var observedAt = ReadTimestamp(properties, "ObservedAt");
        if (observedAt < run.BoundAt || observedAt > now || observedAt > notice.ExpiresAt)
            throw new InvalidDataException("CiAi report observation time is outside the bound run and artifact validity window.");

        var reasonCodes = ReadReasonCodes(properties);
        var (failedResultDigests, failedSummary) = ReadFailedResults(properties);
        var evidenceComplete = verdict != "IncompleteOrUntrusted";
        var successful = verdict == "CompletePass";
        var actionable = verdict == "ActionableFailure";
        if (actionable && failedResultDigests.Count == 0)
            throw new InvalidDataException("CiAi ActionableFailure report contains no failed test records.");

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
            observedAt).Validate();

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

        return new CiAiConsumedTestResult(attempt, headlessEvidence, verdict,
            Array.AsReadOnly(reasonCodes.ToArray()), reportDigest);
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
        var element = properties["ReasonCodes"];
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

    private static (List<string> Digests, List<string> Summary) ReadFailedResults(
        Dictionary<string, JsonElement> properties)
    {
        var results = properties["Results"];
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 10_000)
            throw new FormatException("CiAi report test results must be a bounded array.");
        var digests = new List<string>();
        var summaries = new List<string>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("CiAi report test result must be an object.");
            ValidateUniqueProperties(item);
            var values = item.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value, StringComparer.Ordinal);
            if (!values.TryGetValue("Outcome", out var outcome) ||
                outcome.ValueKind != JsonValueKind.String ||
                !string.Equals(outcome.GetString(), "Failed", StringComparison.Ordinal))
                continue;
            if (!values.TryGetValue("RecordSha256", out var digest) ||
                digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 64 } digestText ||
                !digestText.All(Uri.IsHexDigit))
                throw new FormatException("CiAi failed test record is missing its SHA-256 identity.");
            digests.Add(digestText.ToLowerInvariant());
            var name = OptionalText(values, "TestName", 512);
            var message = OptionalText(values, "Message", 4_000);
            var stack = OptionalText(values, "StackTrace", 8_000);
            summaries.Add($"{name ?? "Failed test"}: {message ?? stack ?? "No diagnostic text supplied"}");
        }
        if (digests.Distinct(StringComparer.Ordinal).Count() != digests.Count)
            throw new FormatException("CiAi report contains duplicate failed-test record digests.");
        return (digests, summaries);
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
                DateTimeStyles.RoundtripKind, out var timestamp) || timestamp == default)
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
