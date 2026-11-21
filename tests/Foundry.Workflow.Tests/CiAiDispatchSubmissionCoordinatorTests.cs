using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiDispatchSubmissionCoordinatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static CiAiRequestTaskMapping Mapping(string candidateSha = null!) =>
        new(1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            "TrentBest/TheSingularityWorkshop.Foundry", "refs/heads/work/task-1",
            candidateSha ?? new string('a', 40), ".github/workflows/build.yml",
            "refs/heads/development", new string('b', 40), "build", "Resolve candidate",
            "Verify candidate", "ciai-production", "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2));

    private sealed class RecordingDispatcher : ICiAiWorkflowDispatcher
    {
        public int Calls { get; private set; }
        public Func<CiAiRequestTaskMapping, CancellationToken, Task>? OnDispatch { get; init; }

        public Task DispatchAsync(CiAiRequestTaskMapping mapping, CancellationToken cancellationToken)
        {
            Calls++;
            return OnDispatch?.Invoke(mapping, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingAdmissionVerifier : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }
        public bool Reject { get; init; }
        public int? RejectOnCall { get; init; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
            string repository, string targetSha, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Reject || Calls == RejectOnCall)
                throw new UnauthorizedAccessException("Exact-SHA onboarding rejected.");
            return ValueTask.FromResult(new AgentOnboardingAdmissionEvidence(
                repository, targetSha, "AgentOnboarding.md", "ciai.agent-onboarding.v1",
                true, "ELIGIBLE", null, "All required onboarding checks passed.",
                new string('d', 64)));
        }
    }

    private static CiAiRequestTaskMapping GovernedMapping() =>
        Mapping() with { Repository = "TrentBest/TheSingularityWorkshop.SingularityBot" };

    [Fact]
    public async Task GovernedTargetIsVerifiedBeforeIntentAndEvidenceIsPersisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher
            {
                OnDispatch = (mapping, _) =>
                {
                    Assert.NotNull(mapping.AdmissionEvidence);
                    Assert.Equal(mapping, journal.Get(mapping.RequestId).Mapping);
                    return Task.CompletedTask;
                }
            };
            var verifier = new RecordingAdmissionVerifier();

            await CiAiDispatchSubmissionCoordinator.SubmitAsync(
                journal, GovernedMapping(), dispatcher, verifier);

            Assert.Equal(2, verifier.Calls);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal("ELIGIBLE", journal.Get("request-1").Mapping.AdmissionEvidence!.Outcome);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task GovernedTargetRejectionCreatesNoIntentAndCallsNoDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher();
            var verifier = new RecordingAdmissionVerifier { Reject = true };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(
                    journal, GovernedMapping(), dispatcher, verifier));

            Assert.Equal(1, verifier.Calls);
            Assert.Equal(0, dispatcher.Calls);
            Assert.Empty(journal.GetAll());
            Assert.False(File.Exists(path) && new FileInfo(path).Length > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TargetHeadChangeAfterIntentCreationPreventsDispatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher();
            var verifier = new RecordingAdmissionVerifier { RejectOnCall = 2 };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(
                    journal, GovernedMapping(), dispatcher, verifier));

            Assert.Equal(2, verifier.Calls);
            Assert.Equal(0, dispatcher.Calls);
            Assert.Single(journal.GetAll());
            Assert.NotNull(journal.Get("request-1").Mapping.AdmissionEvidence);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GovernedTargetCannotCreateIntentWithCallerAuthoredEvidenceAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var forged = GovernedMapping() with
            {
                AdmissionEvidence = new AgentOnboardingAdmissionEvidence(
                    "TrentBest/TheSingularityWorkshop.SingularityBot", new string('a', 40),
                    "AgentOnboarding.md", "ciai.agent-onboarding.v1", true,
                    "ELIGIBLE", null, "Caller says eligible.", new string('d', 64))
            };

            Assert.Throws<UnauthorizedAccessException>(() => journal.CreateDispatchIntent(forged));
            Assert.Empty(journal.GetAll());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PersistsIntentBeforeCallingHostDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher
            {
                OnDispatch = (mapping, _) =>
                {
                    var persisted = journal.Get(mapping.RequestId);
                    Assert.Equal(mapping, persisted.Mapping);
                    Assert.Null(persisted.RunBinding);
                    return Task.CompletedTask;
                }
            };

            var result = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, result.Outcome);
            Assert.Equal("request-1", result.RequestId);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExactExistingIntentRequiresReconciliationAndIsNeverRedispatched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher();

            var first = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);
            var retry = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, first.Outcome);
            Assert.Equal(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, retry.Outcome);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ConflictingIntentNeverCallsHostDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            journal.CreateDispatchIntent(Mapping());
            var dispatcher = new RecordingDispatcher();

            var result = await CiAiDispatchSubmissionCoordinator.SubmitAsync(
                journal, Mapping(new string('c', 40)), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.Conflict, result.Outcome);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task AmbiguousDispatchFailureLeavesIntentDurableAndPreventsBlindRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher
            {
                OnDispatch = (_, _) => Task.FromException(new TimeoutException("Dispatch response was lost."))
            };

            await Assert.ThrowsAsync<TimeoutException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher));

            Assert.Equal(Mapping(), journal.Get("request-1").Mapping);
            Assert.Null(journal.Get("request-1").RunBinding);

            var retry = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, retry.Outcome);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
