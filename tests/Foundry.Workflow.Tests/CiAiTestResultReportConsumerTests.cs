using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiTestResultReportConsumerTests
{
    private static readonly DateTimeOffset TaskStart = DateTimeOffset.Parse("2026-10-31T09:50:00Z");
    private static readonly DateTimeOffset ValidationStart = DateTimeOffset.Parse("2026-10-31T10:10:00Z");
    private static readonly DateTimeOffset RunBoundAt = DateTimeOffset.Parse("2026-10-31T10:15:00Z");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-31T10:45:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T11:00:00Z");
    private const string Repository = "owner/repo";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const long RunId = 123456789;
    private const int RunAttempt = 1;
    private const long JobId = 987654321;
    private const string BranchRef = "refs/heads/work/task-1";
    private const string WorkflowPath = ".github/workflows/ci.yml";

    [Fact]
    public void ConsumesActionableReportIntoBoundAttemptAndHeadlessEvidence()
    {
        var bytes = CreateReport("ActionableFailure", "Failed", "failure", EventSha);
        var consumed = CiAiTestResultReportConsumer.Consume(CreateVerifiedArtifact(bytes), CreateTask(), Now);

        Assert.Equal("ActionableFailure", consumed.Verdict);
        Assert.Equal("task-1", consumed.Attempt.TaskId);
        Assert.Equal("correlation-1", consumed.Attempt.CorrelationId);
        Assert.Equal(Repository, consumed.Attempt.Repository);
        Assert.Equal("work/task-1", consumed.Attempt.Branch);
        Assert.Equal(WorkflowPath, consumed.Attempt.WorkflowPath);
        Assert.Equal(CandidateSha, consumed.Attempt.CandidateSha);
        Assert.Equal(RunId, consumed.Attempt.CiRunId);
        Assert.Equal(RunAttempt, consumed.Attempt.CiRunAttempt);
        Assert.True(consumed.Attempt.EvidenceComplete);
        Assert.False(consumed.Attempt.IsSuccessful);
        Assert.True(consumed.Attempt.HasActionableDiagnostics);
        Assert.NotNull(consumed.Attempt.DiagnosticsFingerprint);
        Assert.Equal(consumed.ReportSha256, consumed.Attempt.EvidencePayloadSha256);
        Assert.Equal(CandidateSha, consumed.HeadlessAiEvidence.CommitSha);
        Assert.Equal(RunId.ToString(), consumed.HeadlessAiEvidence.WorkflowRunId);
        Assert.Contains("test-results.actionable-failure", consumed.HeadlessAiEvidence.BoundedSummary);
    }

    [Fact]
    public void CompletePassIsDerivedOnlyFromCiAiVerdict()
    {
        var bytes = CreateReport("CompletePass", "Passed", "success", EventSha);
        var consumed = CiAiTestResultReportConsumer.Consume(CreateVerifiedArtifact(bytes), CreateTask(), Now);

        Assert.True(consumed.Attempt.EvidenceComplete);
        Assert.True(consumed.Attempt.IsSuccessful);
        Assert.False(consumed.Attempt.HasActionableDiagnostics);
        Assert.Null(consumed.Attempt.DiagnosticsFingerprint);
    }

    [Fact]
    public void RejectsReportWhoseCandidateOrRunProvenanceDiffersFromPersistedDispatch()
    {
        var bytes = CreateReport("ActionableFailure", "Failed", "failure", CandidateSha);

        Assert.Throws<UnauthorizedAccessException>(() =>
            CiAiTestResultReportConsumer.Consume(CreateVerifiedArtifact(bytes), CreateTask(), Now));
    }

    [Fact]
    public void RejectsDispatchBindingWhoseEventShaDiffersFromPersistedIntent()
    {
        var createdAt = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
        var expiresAt = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
        var mapping = new CiAiRequestTaskMapping(
            1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            Repository, BranchRef, CandidateSha, WorkflowPath, "refs/heads/development", WorkflowSha,
            "test-minesweeper", "resolve", "verify", "ciai-provider",
            "https://artifacts.example.com/", "https://foundry.example.com/callback", "auth-profile",
            createdAt, expiresAt, TrustedEventSha: EventSha).Validate();
        var mismatchedRun = new CiAiDispatchRunBinding(
            RunId, RunAttempt, Repository, WorkflowPath, "refs/heads/development", WorkflowSha,
            BranchRef, CandidateSha, "workflow_dispatch", RunBoundAt, EventSha: CandidateSha);

        Assert.Throws<UnauthorizedAccessException>(() => mismatchedRun.ValidateAgainst(mapping));
    }

    [Fact]
    public void RejectsIncompleteReportWithUnknownOrDuplicateRootProperties()
    {
        var bytes = CreateReport("IncompleteOrUntrusted", "Passed", "success", EventSha);
        var json = Encoding.UTF8.GetString(bytes);
        var duplicate = Encoding.UTF8.GetBytes(json[..^1] + ",\"Schema\":\"ciai.test-results.v1\"}");
        Assert.Throws<FormatException>(() =>
            CiAiTestResultReportConsumer.Consume(CreateVerifiedArtifact(duplicate), CreateTask(), Now));
    }

    private static WorkTaskSnapshot CreateTask()
    {
        var request = new WorkTaskRequest(1, "task-1", "correlation-1", Repository, 10,
            $"https://github.com/{Repository}/issues/10",
            "cccccccccccccccccccccccccccccccccccccccc",
            new[] { "src" }, 3).Validate();
        var leaseId = Guid.NewGuid();
        var events = new[]
        {
            new WorkTaskEvent(1, WorkTaskStatus.Queued, WorkTaskStatus.Claimed, "agent", leaseId,
                TaskStart, "Task claimed."),
            new WorkTaskEvent(2, WorkTaskStatus.Claimed, WorkTaskStatus.Investigating, "agent", leaseId,
                TaskStart.AddMinutes(2), "Investigation started."),
            new WorkTaskEvent(3, WorkTaskStatus.Investigating, WorkTaskStatus.Implementing, "agent", leaseId,
                TaskStart.AddMinutes(5), "Implementation started."),
            new WorkTaskEvent(4, WorkTaskStatus.Implementing, WorkTaskStatus.Validating, "agent", leaseId,
                ValidationStart, "Validation started.", RunId, RunAttempt)
        };
        return new WorkTaskSnapshot(request, WorkTaskStatus.Validating,
            new WorkTaskLease("agent", leaseId, Now.AddMinutes(10)), 1, events);
    }

    private static FoundryVerifiedCiAiReportArtifact CreateVerifiedArtifact(byte[] bytes)
    {
        var createdAt = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
        var expiresAt = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
        var mapping = new CiAiRequestTaskMapping(
            1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            Repository, BranchRef, CandidateSha, WorkflowPath, "refs/heads/development", WorkflowSha,
            "test-minesweeper", "resolve", "verify", "ciai-provider",
            "https://artifacts.example.com/", "https://foundry.example.com/callback", "auth-profile",
            createdAt, expiresAt, TrustedEventSha: EventSha).Validate();
        var run = new CiAiDispatchRunBinding(
            RunId, RunAttempt, Repository, WorkflowPath, "refs/heads/development", WorkflowSha,
            BranchRef, CandidateSha, "workflow_dispatch", RunBoundAt, EventSha: EventSha).ValidateAgainst(mapping);
        var snapshot = new CiAiRequestTaskMappingSnapshot(mapping, run, false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return FoundryVerifiedCiAiReportArtifact.Verify(
            snapshot, bytes, "application/json", digest, bytes.Length,
            expiresAt, RunId.ToString(), Now);
    }

    private static byte[] CreateReport(string verdict, string requiredStatus, string conclusion, string eventSha)
    {
        var result = new
        {
            TestId = "test-1",
            TestName = "ExampleFailure",
            Outcome = "Failed",
            RawOutcome = "Failed",
            Message = "Expected value did not match.",
            StackTrace = "at Tests.ExampleFailure()",
            SensitiveContentRedacted = false,
            DiagnosticTruncated = false,
            RecordSha256 = new string('e', 64)
        };
        var report = new
        {
            ContractVersion = 1,
            Schema = "ciai.test-results.v1",
            Repository,
            CandidateRef = BranchRef,
            CandidateSha,
            WorkflowPath,
            WorkflowDefinitionRef = "refs/heads/development",
            PullRequestNumber = (int?)null,
            PullRequestHeadRef = (string?)null,
            PullRequestHeadSha = (string?)null,
            PullRequestBaseRef = (string?)null,
            PullRequestBaseSha = (string?)null,
            RunEvidenceUrl = $"https://github.com/{Repository}/actions/runs/{RunId}",
            JobEvidenceUrl = $"https://github.com/{Repository}/actions/runs/{RunId}/job/{JobId}",
            WorkflowDefinitionSha = WorkflowSha,
            EventName = "workflow_dispatch",
            EventRef = "refs/heads/development",
            EventSha = eventSha,
            RunId,
            RunAttempt,
            JobId,
            JobName = "test-minesweeper",
            JobStatus = "completed",
            JobConclusion = conclusion,
            RequiredCheckStatus = requiredStatus,
            Verdict = verdict,
            ReasonCodes = new[] { verdict == "ActionableFailure" ? "test-results.actionable-failure" : "test-results.complete-pass" },
            TestFramework = ".NET",
            Format = "trx",
            SourceArtifactId = (long?)7001,
            SourceArtifactName = $"ciai-test-results-{RunId}-{RunAttempt}-test-minesweeper",
            SourceJobKey = "test-minesweeper",
            ProviderArchiveDigest = "sha256:" + new string('a', 64),
            ArchiveSizeBytes = (long?)512,
            ArtifactCreatedAt = DateTimeOffset.Parse("2026-10-31T10:20:00Z"),
            ArtifactExpiresAt = DateTimeOffset.Parse("2026-11-07T10:20:00Z"),
            PayloadSha256 = new string('f', 64),
            PayloadSizeBytes = 512L,
            Total = verdict == "CompletePass" ? 1 : 1,
            Passed = verdict == "CompletePass" ? 1 : 0,
            Failed = verdict == "CompletePass" ? 0 : 1,
            Skipped = 0,
            Other = 0,
            Results = verdict == "CompletePass" ? Array.Empty<object>() : new object[] { result },
            ObservedAt
        };
        return JsonSerializer.SerializeToUtf8Bytes(report);
    }
}
