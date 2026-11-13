using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactHostCompositionTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
    private static readonly DateTimeOffset ValidationStarted = DateTimeOffset.Parse("2026-10-31T10:10:00Z");
    private static readonly DateTimeOffset RunBoundAt = DateTimeOffset.Parse("2026-10-31T10:15:00Z");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-31T10:45:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T10:46:00Z");
    private const string Repository = "owner/repo";
    private const string ProviderRepository = "owner/ciai";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BaseSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const string BranchRef = "refs/heads/work/task-1";
    private const string WorkflowPath = ".github/workflows/ci.yml";
    private const long RunId = 123456789;
    private const int RunAttempt = 1;
    private const long JobId = 987654321;
    private static readonly Uri ArtifactUri = new("https://artifacts.example.test/immutable/report.json");

    private sealed class FakeAuthenticator(string sender) : ICiAiArtifactNoticeAuthenticator
    {
        public ValueTask<string> AuthenticateSenderAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(sender);
        }
    }

    private sealed class FakeTransport(byte[] bytes) : ICiAiArtifactTransport
    {
        public Uri? RequestedUri { get; private set; }

        public ValueTask<CiAiArtifactTransportResponse> GetAsync(
            Uri validatedRetrievalUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUri = validatedRetrievalUri;
            return ValueTask.FromResult(new CiAiArtifactTransportResponse(
                System.Net.HttpStatusCode.OK,
                "application/json",
                ArtifactUri,
                false,
                new MemoryStream(bytes, writable: false)));
        }
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
                AcceptedReportSchemaVersion = request.AcceptedCiAiReport?.SchemaVersion,
                AcceptedReportVerdict = request.AcceptedCiAiReport?.Verdict,
                AcceptedReportIdentity = request.AcceptedCiAiReport?.ReportIdentity,
                AcceptedReportNoticeIdentitySha256 = request.AcceptedCiAiReport?.NoticeIdentitySha256,
                AcceptedReportSha256 = request.AcceptedCiAiReport?.ReportSha256,
                AcceptedReportWorkflowRunId = request.AcceptedCiAiReport?.WorkflowRunId,
                AcceptedReportRunAttempt = request.AcceptedCiAiReport?.RunAttempt,
                AcceptedReportCandidateCommitSha = request.AcceptedCiAiReport?.CandidateCommitSha,
                AcceptedReportSourceArtifactIdentitySha256 = request.AcceptedCiAiReport?.SourceArtifactIdentitySha256,
                Diagnosis = "The exact candidate contains an actionable test failure.",
                PatchIntent = "Propose a narrowly scoped correction."
            });
        }
    }

    [Fact]
    public async Task AuthenticatedNoticeRetrievalAndAcceptedReportComposeIntoSameTaskRepairTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "foundry-ciai-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mappingJournal = CreateMappingJournal(Path.Combine(root, "mappings.jsonl"));
            var receiptLedger = new JsonlCiAiArtifactNoticeReceiptLedger(Path.Combine(root, "receipts.jsonl"));
            var taskStore = new JsonlWorkTaskStore(Path.Combine(root, "tasks.jsonl"));
            var attemptJournal = new JsonlWorkTaskRepairAttemptJournal(Path.Combine(root, "attempts.jsonl"));
            var tasks = new PersistentWorkTaskCoordinator(taskStore, attemptJournal);

            tasks.Submit(new WorkTaskRequest(
                1, "task-1", "correlation-1", Repository, 10,
                "https://github.com/owner/repo/issues/10",
                BaseSha, new[] { "src" }, 3));
            var claimed = tasks.Claim("task-1", "agent-1", TimeSpan.FromHours(1), Created);
            var leaseId = claimed.Lease!.LeaseId;
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Investigating,
                Created.AddMinutes(2), "Investigation started.");
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Implementing,
                Created.AddMinutes(5), "Candidate implementation completed.");
            tasks.Transition("task-1", leaseId, WorkTaskStatus.Validating,
                ValidationStarted, "Validate exact candidate.");

            var reportBytes = CreateReport();
            var reportDigest = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant();
            var noticeBytes = CreateNotice(reportBytes, reportDigest);
            var ingress = new CiAiArtifactNoticeIngress(
                new FakeAuthenticator("ciai-production"),
                mappingJournal,
                new CiAiArtifactNoticeValidationPolicy(
                    "foundry-production", ProviderRepository,
                    "workflow-evidence", "1", "implementation-1"),
                receiptLedger);

            var accepted = await ingress.AcceptAsync(noticeBytes, Now);
            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Accepted, accepted.Outcome);
            Assert.Equal("request-1", accepted.ValidatedNotice.Request.Mapping.RequestId);
            Assert.Equal(RunId, accepted.ValidatedNotice.Request.RunBinding!.RunId);
            Assert.Single(receiptLedger.GetAll());

            var retry = await ingress.AcceptAsync(noticeBytes, Now.AddMinutes(1));
            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Duplicate, retry.Outcome);
            Assert.Equal(accepted.Receipt.ReceiptId, retry.Receipt.ReceiptId);
            Assert.Single(receiptLedger.GetAll());

            var transport = new FakeTransport(reportBytes);
            var retrieved = await new CiAiArtifactRetrievalCoordinator(transport)
                .RetrieveAndVerifyAsync(accepted.ValidatedNotice, Now);
            Assert.Equal(ArtifactUri, transport.RequestedUri);
            Assert.Equal(reportBytes, retrieved.Bytes.ToArray());

            var artifact = FoundryVerifiedCiAiReportArtifact.Verify(retrieved, accepted.Receipt, Now);
            var feedback = tasks.ApplyCiAiReport(
                "task-1", leaseId, artifact, CreateSourceBinding(), TimeSpan.FromHours(1), Now);

            Assert.Equal(WorkTaskStatus.RepairRequired, feedback.Snapshot.Status);
            Assert.False(feedback.IsDuplicateDelivery);
            Assert.NotNull(feedback.Consumed);
            Assert.Equal(CandidateSha, feedback.Consumed!.Attempt.CandidateSha);
            Assert.Equal(CreateSourceBinding().IdentitySha256, feedback.Consumed.Attempt.SourceArtifactIdentitySha256);
            Assert.Equal(CreateSourceBinding().IdentitySha256, feedback.Consumed.AcceptedReport.SourceArtifactIdentitySha256);
            Assert.Equal(RunId, feedback.Consumed.Attempt.CiRunId);
            Assert.Equal(CreateSourceBinding().IdentitySha256,
                feedback.Snapshot.Events[^1].SourceArtifactIdentitySha256);
            Assert.Equal(reportBytes, Encoding.UTF8.GetBytes(feedback.Consumed.AcceptedReport.CanonicalReportJson));

            var duplicateApply = tasks.ApplyCiAiReport(
                "task-1", leaseId, artifact, CreateSourceBinding(), TimeSpan.FromHours(1), Now);
            Assert.True(duplicateApply.IsDuplicateDelivery);
            Assert.Null(duplicateApply.Consumed);
            Assert.Single(attemptJournal.GetAttempts("task-1"));

            var turns = new JsonlHeadlessAiTurnJournal(Path.Combine(root, "turns.jsonl"));
            const string priorFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.Null(turns.BeginOrGet("task-1", "turn-initial-key", priorFingerprint));
            turns.Complete("task-1", "turn-initial-key", priorFingerprint, new FoundryHeadlessAiTurnResult
            {
                TurnId = "turn-initial-1",
                TaskId = "task-1",
                CorrelationId = "correlation-1",
                Repository = Repository,
                AuthorizedBranch = "work/task-1",
                BaseCommitSha = BaseSha,
                CandidateCommitSha = CandidateSha,
                RequestFingerprint = priorFingerprint,
                ProviderId = "fake-provider",
                ModelId = "fake-model",
                StartedAt = Created.AddMinutes(1),
                CompletedAt = Created.AddMinutes(2),
                IdempotencyKey = "turn-initial-key"
            });

            var client = new RecordingHeadlessAiClient();
            var turnCoordinator = new FoundryHeadlessAiTurnCoordinator(
                tasks, client, turns, new FixedTimeProvider(Now.AddMinutes(1)));
            var turn = await turnCoordinator.InvokeAsync(
                "task-1", leaseId, "work/task-1", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "experiences-developer",
                "Repair the exact failing candidate using the supplied CiAi evidence.",
                "Return a bounded patch proposal; do not claim authorization.",
                """{"type":"object","required":["diagnosis","patchIntent"]}""",
                new[] { feedback.Consumed.HeadlessAiEvidence },
                priorTurnId: "turn-initial-1",
                priorCandidateCommitSha: CandidateSha,
                acceptedCiAiReport: feedback.Consumed.AcceptedReport);

            Assert.Equal("turn-repair-1", turn.TurnId);
            Assert.Equal(WorkTaskStatus.RepairRequired, tasks.GetSnapshot("task-1").Status);
            Assert.NotNull(client.Request?.AcceptedCiAiReport);
            Assert.Equal("sha256:" + reportDigest, client.Request!.AcceptedCiAiReport!.ReportIdentity);
            Assert.Equal(artifact.NoticeIdentitySha256, client.Request.AcceptedCiAiReport.NoticeIdentitySha256);
            Assert.Equal(Encoding.UTF8.GetString(reportBytes), client.Request.AcceptedCiAiReport.CanonicalReportJson);
            Assert.Equal(CandidateSha, client.Request.AcceptedCiAiReport.CandidateCommitSha);
            Assert.Equal(RunId.ToString(), client.Request.AcceptedCiAiReport.WorkflowRunId);
            Assert.Equal(RunAttempt, client.Request.AcceptedCiAiReport.RunAttempt);
            Assert.Equal(CreateSourceBinding().IdentitySha256,
                client.Request.AcceptedCiAiReport.SourceArtifactIdentitySha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonlCiAiRequestTaskMappingJournal CreateMappingJournal(string path)
    {
        var journal = new JsonlCiAiRequestTaskMappingJournal(path);
        var mapping = new CiAiRequestTaskMapping(
            1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            Repository, BranchRef, CandidateSha, WorkflowPath,
            "refs/heads/development", WorkflowSha,
            "test-minesweeper", "resolve", "verify", "ciai-production",
            "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2),
            TrustedEventSha: EventSha)
            { JobKey = "minesweeper-required-check", SourceArtifactName = "minesweeper-test-results" }.Validate();
        journal.CreateDispatchIntent(mapping);
        journal.BindRun("request-1", new CiAiDispatchRunBinding(
            RunId, RunAttempt, Repository, WorkflowPath,
            "refs/heads/development", WorkflowSha,
            BranchRef, CandidateSha, "workflow_dispatch", RunBoundAt,
            EventSha: EventSha), RunBoundAt);
        return journal;
    }

    private static byte[] CreateNotice(byte[] reportBytes, string digest)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["requestId"] = "request-1",
            ["operationId"] = "operation-1",
            ["idempotencyKey"] = "idempotency-1",
            ["providerRepository"] = ProviderRepository,
            ["capabilityIdentity"] = "workflow-evidence",
            ["capabilityVersion"] = "1",
            ["implementationRevision"] = "implementation-1",
            ["artifactId"] = "artifact-123",
            ["artifactVersion"] = "v1",
            ["mediaType"] = "application/json",
            ["sizeBytes"] = reportBytes.Length,
            ["retrievalUri"] = ArtifactUri.AbsoluteUri,
            ["digest"] = digest,
            ["expiresAt"] = "2026-10-31T11:30:00Z",
            ["sourceSha"] = CandidateSha,
            ["clientIdentity"] = "foundry-production",
            ["providerRunId"] = RunId.ToString()
        };
        var payload = new
        {
            contractVersion = 1,
            noticeId = "notice-1",
            recipientIdentity = "foundry-production",
            notifiedAt = "2026-10-31T10:20:00Z",
            manifest
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static CiAiHostObservedSourceArtifactBinding CreateSourceBinding() =>
        new(
            Repository, RunId, RunAttempt, CandidateSha, WorkflowPath,
            JobId, "test-minesweeper", "minesweeper-required-check",
            7001, "minesweeper-test-results", "sha256:" + new string('a', 64), 512,
            DateTimeOffset.Parse("2026-10-31T10:18:00Z"),
            DateTimeOffset.Parse("2026-10-31T10:30:00Z"),
            DateTimeOffset.Parse("2026-10-31T10:20:00Z"),
            DateTimeOffset.Parse("2026-11-07T10:20:00Z"),
            new string('f', 64), 512);

    private static byte[] CreateReport()
    {
        var failedResult = new
        {
            TestId = "test-1",
            TestName = "MinesweeperRegression",
            Outcome = "Failed",
            RawOutcome = "Failed",
            Message = "Expected adjacent mine count to equal the observed count.",
            StackTrace = "at Experiences.Minesweeper.Tests.BoardTests.ValidateAdjacentCounts()",
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
            RunEvidenceUrl = "https://github.com/" + Repository + "/actions/runs/" + RunId,
            JobEvidenceUrl = "https://github.com/" + Repository + "/actions/runs/" + RunId + "/job/" + JobId,
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
            SourceArtifactName = "minesweeper-test-results",
            SourceJobKey = "minesweeper-required-check",
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
        return JsonSerializer.SerializeToUtf8Bytes(
            report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
