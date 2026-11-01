using System.Security.Cryptography;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiRepairFeedbackCoordinatorTests
{
    private static readonly DateTimeOffset TaskStart = DateTimeOffset.Parse("2026-10-31T09:50:00Z");
    private static readonly DateTimeOffset ValidationStart = DateTimeOffset.Parse("2026-10-31T10:10:00Z");
    private static readonly DateTimeOffset RunBoundAt = DateTimeOffset.Parse("2026-10-31T10:15:00Z");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-31T10:45:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T10:46:00Z");
    private const string Repository = "owner/repo";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BaseSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const long RunId = 123456789;
    private const int RunAttempt = 1;
    private const long JobId = 987654321;
    private const string BranchRef = "refs/heads/work/task-1";
    private const string WorkflowPath = ".github/workflows/ci.yml";

    [Fact]
    public async Task AppliesCiAiVerdictOnceAndResumesSameTaskThroughHeadlessAiRepairTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"foundry-ciai-feedback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonlWorkTaskStore(Path.Combine(root, "tasks.jsonl"));
            var attempts = new JsonlWorkTaskRepairAttemptJournal(Path.Combine(root, "attempts.jsonl"));
            var tasks = new PersistentWorkTaskCoordinator(store, attempts);
            tasks.Submit(new WorkTaskRequest(1, "task-1", "correlation-1", Repository, 10,
                $"https://github.com/{Repository}/issues/10", BaseSha, new[] { "src" }, 3));
            var claimed = tasks.Claim("task-1", "agent-1", TimeSpan.FromHours(1), TaskStart);
            var leaseId = claimed.Lease!.LeaseId;
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Investigating,
                TaskStart.AddMinutes(2), "Investigation started.");
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Implementing,
                TaskStart.AddMinutes(5), "Initial implementation started.");
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Validating,
                ValidationStart, "Validate exact candidate.");

            var artifact = CreateArtifact();
            var feedback = tasks.ApplyCiAiReport("task-1", leaseId, artifact,
                TimeSpan.FromHours(1), Now);

            Assert.Equal(WorkTaskStatus.RepairRequired, feedback.Snapshot.Status);
            Assert.NotNull(feedback.Consumed);
            Assert.False(feedback.IsDuplicateDelivery);
            Assert.Equal(1, attempts.GetAttempts("task-1").Count);
            Assert.Equal(RunId, feedback.Snapshot.Events[^1].CiRunId);
            Assert.Equal(RunAttempt, feedback.Snapshot.Events[^1].CiRunAttempt);

            var duplicate = tasks.ApplyCiAiReport("task-1", leaseId, artifact,
                TimeSpan.FromHours(1), Now);
            Assert.True(duplicate.IsDuplicateDelivery);
            Assert.Null(duplicate.Consumed);
            Assert.Equal(feedback.Snapshot.Events.Count, duplicate.Snapshot.Events.Count);
            Assert.Equal(1, attempts.GetAttempts("task-1").Count);

            var client = new RecordingHeadlessAiClient();
            var turnJournal = new JsonlHeadlessAiTurnJournal(Path.Combine(root, "turns.jsonl"));
            const string priorFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.Null(turnJournal.BeginOrGet("task-1", "turn-initial-key", priorFingerprint));
            turnJournal.Complete("task-1", "turn-initial-key", priorFingerprint,
                new FoundryHeadlessAiTurnResult
                {
                    TurnId = "turn-initial-1",
                    TaskId = "task-1",
                    CorrelationId = "correlation-1",
                    Repository = Repository,
                    AuthorizedBranch = "work/task-1",
                    BaseCommitSha = BaseSha,
                    CandidateCommitSha = BaseSha,
                    RequestFingerprint = priorFingerprint,
                    ProviderId = "fake-provider",
                    ModelId = "fake-model",
                    StartedAt = TaskStart.AddMinutes(1),
                    CompletedAt = TaskStart.AddMinutes(2),
                    IdempotencyKey = "turn-initial-key"
                });
            var turnCoordinator = new FoundryHeadlessAiTurnCoordinator(
                tasks, client, turnJournal, new FixedTimeProvider(Now.AddMinutes(1)));
            var turn = await turnCoordinator.InvokeAsync(
                "task-1", leaseId, "work/task-1", CandidateSha,
                "experiences-developer", "Repair the exact failing candidate using the supplied CiAi evidence.",
                "Return a bounded patch proposal; do not claim authorization.",
                """{"type":"object","required":["diagnosis","patchIntent"]}""",
                new[] { feedback.Consumed!.HeadlessAiEvidence },
                priorTurnId: "turn-initial-1",
                priorCandidateCommitSha: BaseSha);

            Assert.Equal("task-1", turn.TaskId);
            Assert.Equal("correlation-1", turn.CorrelationId);
            Assert.Equal("turn-repair-1", turn.TurnId);
            Assert.Equal(WorkTaskStatus.RepairRequired, tasks.GetSnapshot("task-1").Status);
            Assert.Equal("turn-initial-1", client.Request!.PriorTurnId);
            Assert.Equal(BaseSha, client.Request.PriorCandidateCommitSha);
            Assert.Single(client.Request.Evidence);
            Assert.Equal(CandidateSha, client.Request.Evidence[0].CommitSha);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FoundryVerifiedCiAiReportArtifact CreateArtifact()
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
        var bytes = CreateReport();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return FoundryVerifiedCiAiReportArtifact.Verify(
            snapshot, bytes, "application/json", digest, bytes.Length,
            expiresAt, RunId.ToString(), Now);
    }

    private static byte[] CreateReport()
    {
        var failedResult = new
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
            EventSha,
            RunId,
            RunAttempt,
            JobId,
            JobName = "test-minesweeper",
            JobStatus = "completed",
            JobConclusion = "failure",
            RequiredCheckStatus = "Failed",
            Verdict = "ActionableFailure",
            ReasonCodes = new[] { "test-results.actionable-failure" },
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
            Total = 1,
            Passed = 0,
            Failed = 1,
            Skipped = 0,
            Other = 0,
            Results = new[] { failedResult },
            ObservedAt
        };
        return JsonSerializer.SerializeToUtf8Bytes(report);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record PreparedTurn(string RequestFingerprint) : IFoundryHeadlessAiPreparedTurn;

    private sealed class RecordingHeadlessAiClient : IHeadlessAiAgentTurnClient
    {
        public FoundryHeadlessAiTurnRequest? Request { get; private set; }

        public IFoundryHeadlessAiPreparedTurn Prepare(FoundryHeadlessAiTurnRequest request, DateTimeOffset now)
        {
            Request = request;
            return new PreparedTurn(new string('a', 64));
        }

        public Task<FoundryHeadlessAiTurnResult> ExecuteAsync(
            FoundryHeadlessAiTurnRequest request,
            IFoundryHeadlessAiPreparedTurn prepared,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FoundryHeadlessAiTurnResult
            {
                TurnId = "turn-repair-1",
                TaskId = request.TaskId,
                CorrelationId = request.CorrelationId,
                Repository = request.Repository,
                AuthorizedBranch = request.AuthorizedBranch,
                BaseCommitSha = request.BaseCommitSha,
                CandidateCommitSha = request.CandidateCommitSha,
                RequestFingerprint = prepared.RequestFingerprint,
                ProviderId = "fake-provider",
                ModelId = "fake-model",
                StartedAt = Now.AddMinutes(1),
                CompletedAt = Now.AddMinutes(1).AddSeconds(1),
                PriorTurnId = request.PriorTurnId,
                PriorCandidateCommitSha = request.PriorCandidateCommitSha,
                IdempotencyKey = request.IdempotencyKey,
                Diagnosis = "The required action is rejected in terminal state.",
                PatchIntent = "Propose a narrowly scoped correction."
            });
        }
    }
}
