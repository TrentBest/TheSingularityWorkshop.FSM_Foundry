using Xunit;
using TheSingularityWorkshop.Foundry;

namespace Foundry.Workflow.Tests;

public sealed class HeadlessAiTurnBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 5, 0, 0, TimeSpan.Zero);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Journal_RefreshesAndSerializesAcrossMultipleInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        try
        {
            // Both instances load the empty file before either writes. Each operation must
            // take the shared file lock and refresh the durable sequence before proceeding.
            var first = new JsonlHeadlessAiTurnJournal(path);
            var second = new JsonlHeadlessAiTurnJournal(path);

            Assert.Null(first.BeginOrGet("task-1", "turn-1", fingerprint));
            Assert.Throws<InvalidOperationException>(() =>
                second.BeginOrGet("task-1", "turn-1", fingerprint));

            Assert.Null(second.BeginOrGet("task-2", "turn-2", fingerprint));
            Assert.Null(first.BeginOrGet("task-3", "turn-3", fingerprint));

            var result = new FoundryHeadlessAiTurnResult
            {
                TurnId = "turn-1",
                TaskId = "task-1",
                CorrelationId = "corr-1",
                Repository = "TrentBest/MyVR",
                AuthorizedBranch = "feature/myvr-agent-work",
                BaseCommitSha = Sha,
                CandidateCommitSha = Sha,
                RequestFingerprint = fingerprint,
                ProviderId = "test-provider",
                ModelId = "test-model",
                StartedAt = Now,
                CompletedAt = Now.AddSeconds(1),
                IdempotencyKey = "turn-1"
            };
            first.Complete("task-1", "turn-1", fingerprint, result);

            var replayed = second.BeginOrGet("task-1", "turn-1", fingerprint);
            Assert.NotNull(replayed);
            Assert.Equal(result.TurnId, replayed!.TurnId);

            var sequences = File.ReadAllLines(path)
                .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64())
                .ToArray();
            Assert.Equal(new long[] { 1, 2, 3, 4 }, sequences);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task InvokeAsync_BindsRequestAndResultToCurrentTaskLeaseAndCandidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlWorkTaskStore(path);
            var tasks = new PersistentWorkTaskCoordinator(store);
            tasks.Submit(new WorkTaskRequest(1, "task-1", "corr-1", "TrentBest/MyVR",
                42, "https://github.com/TrentBest/MyVR/issues/42", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-1", "agent-1", TimeSpan.FromMinutes(30), Now);
            var investigating = tasks.Transition("task-1", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient();
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            var result = await coordinator.InvokeAsync("task-1", investigating.Lease!.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "Inspect the issue and propose a bounded implementation.",
                "Return a JSON proposal matching the supplied schema.",
                """{"type":"object","required":["diagnosis","patchIntent"]}""");

            Assert.Equal("task-1", client.Request!.TaskId);
            Assert.Equal("corr-1", client.Request.CorrelationId);
            Assert.Equal("TrentBest/MyVR", client.Request.Repository);
            Assert.Equal("feature/myvr-agent-work", client.Request.AuthorizedBranch);
            Assert.Equal(Sha, client.Request.BaseCommitSha);
            Assert.Equal(Sha, client.Request.CandidateCommitSha);
            Assert.StartsWith("foundry-", client.Request.IdempotencyKey);
            Assert.Equal("turn-1", result.TurnId);
            var duplicate = await coordinator.InvokeAsync("task-1", investigating.Lease!.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "Inspect the issue and propose a bounded implementation.",
                "Return a JSON proposal matching the supplied schema.",
                """{"type":"object","required":["diagnosis","patchIntent"]}""");
            Assert.Equal(result.TurnId, duplicate.TurnId);
            Assert.Equal(1, client.CallCount);
            var restartedClient = new RecordingClient();
            var restartedCoordinator = new FoundryHeadlessAiTurnCoordinator(tasks, restartedClient,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));
            var replayed = await restartedCoordinator.InvokeAsync("task-1", investigating.Lease!.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "Inspect the issue and propose a bounded implementation.",
                "Return a JSON proposal matching the supplied schema.",
                """{"type":"object","required":["diagnosis","patchIntent"]}""");
            Assert.Equal(result.TurnId, replayed.TurnId);
            Assert.Equal(0, restartedClient.CallCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsExpiredOrSupersededLeaseBeforeProviderCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-2", "corr-2", "TrentBest/MyVR",
                43, "https://github.com/TrentBest/MyVR/issues/43", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-2", "agent-1", TimeSpan.FromMinutes(1), Now);
            tasks.Transition("task-2", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient();
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(2)));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.InvokeAsync(
                "task-2", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));
            Assert.Null(client.Request);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsResultWithDifferentCandidateProvenance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-3", "corr-3", "TrentBest/MyVR",
                44, "https://github.com/TrentBest/MyVR/issues/44", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-3", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-3", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient { WrongCandidate = true };
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InvokeAsync(
                "task-3", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsResultWithDifferentPriorCandidateProvenance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-prior", "corr-prior", "TrentBest/MyVR",
                48, "https://github.com/TrentBest/MyVR/issues/48", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-prior", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-prior", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient { WrongPriorCandidate = true };
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InvokeAsync(
                "task-prior", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema",
                priorTurnId: "prior-turn",
                priorCandidateCommitSha: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsResultWithMismatchedCanonicalHeadlessAiFingerprint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-fingerprint", "corr-fingerprint", "TrentBest/MyVR",
                50, "https://github.com/TrentBest/MyVR/issues/50", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-fingerprint", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-fingerprint", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient { WrongFingerprint = true };
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InvokeAsync(
                "task-fingerprint", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));
            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_PersistsProviderResultButRejectsCallerWhenLeaseExpiresDuringCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-lease-race", "corr-lease-race", "TrentBest/MyVR",
                51, "https://github.com/TrentBest/MyVR/issues/51", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-lease-race", "agent-1", TimeSpan.FromMinutes(1), Now);
            tasks.Transition("task-lease-race", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var clock = new MutableTimeProvider(Now.AddSeconds(30));
            var client = new RecordingClient { BeforeReturn = () => clock.Set(Now.AddMinutes(2)) };
            var journalPath = path + ".turns.jsonl";
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(journalPath), clock);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.InvokeAsync(
                "task-lease-race", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));

            Assert.Equal(1, client.CallCount);
            var records = File.ReadAllLines(journalPath);
            Assert.Equal(2, records.Length);
            Assert.Contains("\"result\":", records[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_DoesNotReserveTurnWhenAlreadyCancelled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-cancelled", "corr-cancelled", "TrentBest/MyVR",
                47, "https://github.com/TrentBest/MyVR/issues/47", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-cancelled", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-cancelled", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient();
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.InvokeAsync(
                "task-cancelled", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema",
                cancellationToken: new CancellationToken(canceled: true)));

            var result = await coordinator.InvokeAsync("task-cancelled", claimed.Lease.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "instructions", "prompt", "schema");

            Assert.Equal("turn-1", result.TurnId);
            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_DoesNotRepeatProviderCallWhenPriorOutcomeIsIndeterminate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-pending", "corr-pending", "TrentBest/MyVR",
                46, "https://github.com/TrentBest/MyVR/issues/46", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-pending", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-pending", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient { ThrowOnCall = true };
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InvokeAsync(
                "task-pending", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));
            var restartedCoordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restartedCoordinator.InvokeAsync(
                "task-pending", claimed.Lease.LeaseId, "feature/myvr-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));

            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_ChangesIdempotencyKeyWhenCanonicalPromptChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            tasks.Submit(new WorkTaskRequest(1, "task-4", "corr-4", "TrentBest/MyVR",
                45, "https://github.com/TrentBest/MyVR/issues/45", Sha,
                new[] { "src/MyVR.Core" }, 3));
            var claimed = tasks.Claim("task-4", "agent-1", TimeSpan.FromMinutes(30), Now);
            tasks.Transition("task-4", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
            var client = new RecordingClient();
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(path + ".turns.jsonl"), new FixedTimeProvider(Now.AddMinutes(1)));

            await coordinator.InvokeAsync("task-4", claimed.Lease.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "Inspect the issue.", "Prompt version one.", "schema");
            var firstKey = client.Request!.IdempotencyKey;

            await coordinator.InvokeAsync("task-4", claimed.Lease.LeaseId,
                "feature/myvr-agent-work", Sha, "software-engineer",
                "Inspect the issue.", "Prompt version two.", "schema");

            Assert.NotEqual(firstKey, client.Request!.IdempotencyKey);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    [Fact]
    public async Task InvokeAsync_RevalidatesGovernedAdmissionBeforeProviderAndRejectsWithoutModelCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-headlessai-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            var request = new WorkTaskRequest(1, "governed-model-task", "governed-model-correlation",
                "TrentBest/TheSingularityWorkshop.SingularityBot", 39,
                "https://github.com/TrentBest/TheSingularityWorkshop.SingularityBot/issues/39",
                Sha, new[] { "src" }, 3);
            var acceptedEvidence = GovernedEvidence();
            await tasks.SubmitAsync(request, new TestAdmissionVerifier(acceptedEvidence));
            var claimed = tasks.Claim(request.TaskId, "agent-1", TimeSpan.FromMinutes(30), Now);
            var investigating = tasks.Transition(request.TaskId, claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");

            var rejectedEvidence = acceptedEvidence with
            {
                IsEligible = false,
                Outcome = "NOT_ELIGIBLE",
                FailureCode = "onboarding.missing",
                ContentSha256 = null
            };
            var verifier = new TestAdmissionVerifier(rejectedEvidence);
            var client = new RecordingClient();
            var turnJournalPath = path + ".turns.jsonl";
            var coordinator = new FoundryHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(turnJournalPath),
                new FixedTimeProvider(Now.AddMinutes(1)), verifier);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.InvokeAsync(
                request.TaskId, investigating.Lease!.LeaseId, "feature/governed-agent-work", Sha,
                "software-engineer", "instructions", "prompt", "schema"));

            Assert.Equal(1, verifier.Calls);
            Assert.Equal(0, client.CallCount);
            Assert.Null(client.Request);
            Assert.False(File.Exists(turnJournalPath));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".turns.jsonl")) File.Delete(path + ".turns.jsonl");
        }
    }

    private static AgentOnboardingAdmissionEvidence GovernedEvidence() => new(
        "TrentBest/TheSingularityWorkshop.SingularityBot", Sha,
        AgentOnboardingAdmissionEvidence.RequiredPath, AgentOnboardingAdmissionEvidence.RequiredContractVersion,
        true, "ELIGIBLE", null, "Exact-SHA CiAi contract passed.", new string('f', 64));

    private sealed class TestAdmissionVerifier(AgentOnboardingAdmissionEvidence evidence)
        : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(string repository, string targetSha,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(evidence);
        }
    }

    [Fact]
    public void RequestValidation_RejectsCanonicalBranchesAndEvidenceForAnotherCandidate()
    {
        var request = new FoundryHeadlessAiTurnRequest
        {
            TaskId = "task",
            CorrelationId = "corr",
            Repository = "TrentBest/MyVR",
            AuthorizedBranch = "development",
            BaseCommitSha = Sha,
            CandidateCommitSha = Sha,
            IssueUrl = "https://github.com/TrentBest/MyVR/issues/42",
            IssueNumber = 42,
            Role = "engineer",
            Instructions = "instructions",
            CanonicalPrompt = "prompt",
            ResponseSchema = "schema",
            ExpiresAt = Now.AddMinutes(5),
            IdempotencyKey = "turn-key",
            Evidence = new[]
            {
                new FoundryHeadlessAiEvidence
                {
                    Repository = "TrentBest/MyVR",
                    CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    WorkflowRunId = "100",
                    Attempt = 1,
                    WorkflowName = "CI",
                    Conclusion = "success",
                    SourceUrl = "https://github.com/TrentBest/MyVR/actions/runs/100",
                    ObservedAt = Now
                }
            }
        };

        Assert.Throws<ArgumentException>(() => request.Validate(Now));
        var validBranchWithBadEvidence = request with { AuthorizedBranch = "feature/valid-branch" };
        Assert.Throws<ArgumentException>(() => validBranchWithBadEvidence.Validate(Now));
        var segmentTrailingDot = request with { AuthorizedBranch = "feature/segment." };
        Assert.Throws<ArgumentException>(() => segmentTrailingDot.Validate(Now));
        var incompletePriorTurn = request with
        {
            AuthorizedBranch = "feature/valid-branch",
            Evidence = Array.Empty<FoundryHeadlessAiEvidence>(),
            PriorTurnId = "prior-turn"
        };
        Assert.Throws<ArgumentException>(() => incompletePriorTurn.Validate(Now));
    }

    private sealed class RecordingClient : IHeadlessAiAgentTurnClient
    {
        public FoundryHeadlessAiTurnRequest? Request { get; private set; }
        public bool WrongCandidate { get; init; }
        public bool WrongPriorCandidate { get; init; }
        public bool WrongFingerprint { get; init; }
        public bool ThrowOnCall { get; init; }
        public Action? BeforeReturn { get; init; }
        public int CallCount { get; private set; }

        public IFoundryHeadlessAiPreparedTurn Prepare(FoundryHeadlessAiTurnRequest request, DateTimeOffset now) =>
            new PreparedTurn(new string('a', 64));

        public Task<FoundryHeadlessAiTurnResult> ExecuteAsync(
            FoundryHeadlessAiTurnRequest request, IFoundryHeadlessAiPreparedTurn prepared,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            if (ThrowOnCall)
                throw new InvalidOperationException("Simulated provider outcome interruption.");
            BeforeReturn?.Invoke();
            return Task.FromResult(new FoundryHeadlessAiTurnResult
            {
                TurnId = "turn-1",
                TaskId = request.TaskId,
                CorrelationId = request.CorrelationId,
                Repository = request.Repository,
                AuthorizedBranch = request.AuthorizedBranch,
                BaseCommitSha = request.BaseCommitSha,
                CandidateCommitSha = WrongCandidate ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : request.CandidateCommitSha,
                RequestFingerprint = new string(WrongFingerprint ? 'b' : 'a', 64),
                ProviderId = "fake-provider",
                ModelId = "fake-model",
                StartedAt = Now,
                CompletedAt = Now.AddSeconds(1),
                PriorTurnId = request.PriorTurnId,
                PriorCandidateCommitSha = WrongPriorCandidate ? "cccccccccccccccccccccccccccccccccccccccc" : request.PriorCandidateCommitSha,
                IdempotencyKey = request.IdempotencyKey,
                Diagnosis = "The task is ready for bounded implementation."
            });
        }
    }

    private sealed record PreparedTurn(string RequestFingerprint) : IFoundryHeadlessAiPreparedTurn;

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
        public void Set(DateTimeOffset value) => _value = value;
    }
}
