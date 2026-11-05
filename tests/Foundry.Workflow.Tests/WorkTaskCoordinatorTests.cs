using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class WorkTaskCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SubmitValidatesAndRetainsVersionedTaskProvenance()
    {
        var coordinator = new WorkTaskCoordinator();
        var snapshot = coordinator.Submit(Request());
        Assert.Equal(1, snapshot.Request.SchemaVersion);
        Assert.Equal("TrentBest/TheSingularityWorkshop.Foundry", snapshot.Request.Repository);
        Assert.Equal(WorkTaskStatus.Queued, snapshot.Status);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public void ClaimConflictAndExpiredLeaseAreDeterministic()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var claimed = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(2), Now);
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Claim("foundry-8", "agent-b", TimeSpan.FromMinutes(2), Now.AddMinutes(1)));
        var reclaimed = coordinator.Claim("foundry-8", "agent-b", TimeSpan.FromMinutes(2), Now.AddMinutes(3));
        Assert.Equal("agent-b", reclaimed.Lease!.AgentId);
        Assert.Equal(2, reclaimed.Events.Count);
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddMinutes(3), "stale owner"));
    }

    [Fact]
    public void ExpiredCodeRedLeaseCannotBeReclaimedWithoutApprovedRecovery()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(2), Now).Lease!;
        coordinator.Transition("foundry-8", lease.LeaseId, WorkTaskStatus.Investigating,
            Now, "inspect");
        coordinator.Transition("foundry-8", lease.LeaseId, WorkTaskStatus.Implementing,
            Now, "implement");
        coordinator.Transition("foundry-8", lease.LeaseId, WorkTaskStatus.Validating,
            Now, "validate");
        var codeRed = coordinator.Transition("foundry-8", lease.LeaseId, WorkTaskStatus.CodeRed,
            Now, "validation blocked");

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Claim("foundry-8", "agent-b", TimeSpan.FromMinutes(2), Now.AddMinutes(3)));

        var unchanged = coordinator.GetSnapshot("foundry-8");
        Assert.Equal(WorkTaskStatus.CodeRed, unchanged.Status);
        Assert.Equal(lease, unchanged.Lease);
        Assert.Equal(codeRed.Events.Count, unchanged.Events.Count);
    }

    [Fact]
    public void LifecycleTransitionsAreValidatedAndAudited()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.ReviewReady, Now, "skip steps"));
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect issue");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "implement bounded slice");
        var validating = coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "run checks");
        Assert.Equal(1, validating.Attempt);
        Assert.Equal(4, validating.Events.Count);
        Assert.Equal(4L, validating.Events[^1].Sequence);
    }

    [Fact]
    public void RetryBudgetExhaustionBlocksInsteadOfLooping()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request(maximumAttempts: 1));
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "change");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "validate");
        var attempt = FailedAttempt();
        var blocked = coordinator.ApplyRepairDecision("foundry-8", lease,
            Array.Empty<WorkTaskRepairAttempt>(), attempt, Expectation(attempt),
            TimeSpan.FromMinutes(30), Now.AddMinutes(3));
        Assert.Equal(WorkTaskStatus.Blocked, blocked.Status);
        Assert.Equal(1, blocked.Attempt);
    }


    [Fact]
    public void RetryBudgetCannotBeBypassedThroughCodeRed()
    {
        var coordinator = new WorkTaskCoordinator(new TestRecoveryReceiptVerifier());
        coordinator.Submit(Request(maximumAttempts: 1));
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "validate first attempt");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.CodeRed, Now, "validation blocked");
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "skip recovery investigation"));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "skip recovery investigation"));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "unsigned recovery"));
        var incident = TestIncident();
        var receipt = TestReceipt(taskEventSequence: 5, incidentSequence: incident.Sequence);
        var recovered = coordinator.RecoverFromCodeRed("foundry-8", lease, incident, receipt,
            Now.AddMinutes(1), "resume after host approval");
        Assert.Equal(WorkTaskStatus.Investigating, recovered.Status);
        Assert.Equal(receipt.ReceiptId, recovered.Events[^1].ApprovalReceiptId);
        Assert.Equal(receipt.Principal, recovered.Events[^1].ApprovalPrincipal);
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "resume bounded repair");
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "attempt beyond budget"));
        Assert.Equal(1, coordinator.GetSnapshot("foundry-8").Attempt);
    }

    [Fact]
    public void ClaimAndTransitionRejectDefaultTimestamps()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        Assert.Throws<ArgumentException>(() =>
            coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(5), default));
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(5), Now).Lease!.LeaseId;
        Assert.Throws<ArgumentException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, default, "invalid time"));
    }

    [Fact]
    public void DuplicateTaskAndUnsafeScopeAreRejected()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        Assert.Throws<InvalidOperationException>(() => coordinator.Submit(Request()));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(paths: new[] { "../secrets" })));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(baseSha: "abc")));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(paths: new[] { "C:/outside" })));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request() with { Repository = null! }));
    }

    [Fact]
    public void TaskEnvelopeBoundsRejectOversizedIdentifiersAndScopes()
    {
        var coordinator = new WorkTaskCoordinator();
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(taskId: new string('t', 129))));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(correlationId: new string('c', 129))));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(paths:
            Enumerable.Range(0, 129).Select(index => $"src/{index}").ToArray())));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request(paths:
            new[] { new string('p', 513) })));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(Request() with
            { TaskId = "bad\u0001task" }));
    }

    [Fact]
    public void LeaseAndAuditMetadataBoundsRejectOversizedOrControlCharacters()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        Assert.Throws<ArgumentException>(() => coordinator.Claim("foundry-8",
            new string('a', 129), TimeSpan.FromMinutes(5), Now));
        Assert.Throws<ArgumentException>(() => coordinator.Claim("foundry-8",
            "bad\u0001agent", TimeSpan.FromMinutes(5), Now));

        var lease = coordinator.Claim("foundry-8", "agent-a",
            TimeSpan.FromMinutes(5), Now).Lease!.LeaseId;
        Assert.Throws<ArgumentException>(() => coordinator.Transition("foundry-8", lease,
            WorkTaskStatus.Investigating, Now, new string('r', 2049)));
        Assert.Throws<ArgumentException>(() => coordinator.Transition("foundry-8", lease,
            WorkTaskStatus.Investigating, Now, "bad\u0001reason"));
    }

    [Fact]
    public void ReviewReadyCannotBeAssertedByAnOrdinaryLifecycleTransition()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "validate");
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.ReviewReady, Now, "claim success"));
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.CodeRed, Now, "urgent blocker");
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Transition("foundry-8", lease, WorkTaskStatus.ReviewReady, Now, "bypass CI from CODE RED"));
    }

    [Fact]
    public void ExactSuccessfulCiEvidenceIsRequiredToReachReviewReady()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "validate");
        var attempt = SuccessfulAttempt();
        var wrongExpectation = Expectation(attempt) with { CandidateSha = new string('b', 40) };
        var rejected = coordinator.ApplyRepairDecision("foundry-8", lease,
            Array.Empty<WorkTaskRepairAttempt>(), attempt, wrongExpectation,
            TimeSpan.FromMinutes(30), Now.AddMinutes(3));
        Assert.Equal(WorkTaskStatus.Blocked, rejected.Status);
        Assert.Equal("repair.evidence-provenance-mismatch", rejected.Events[^1].Reason.Split(':')[0]);
    }

    [Fact]
    public void TerminalHandoffReleasesLease()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var lease = coordinator.Claim("foundry-8", "agent-a", TimeSpan.FromMinutes(10), Now).Lease!.LeaseId;
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("foundry-8", lease, WorkTaskStatus.Validating, Now, "validate");
        var attempt = SuccessfulAttempt();
        coordinator.ApplyRepairDecision("foundry-8", lease, Array.Empty<WorkTaskRepairAttempt>(),
            attempt, Expectation(attempt), TimeSpan.FromMinutes(30), Now.AddMinutes(3));
        var handedOff = coordinator.Transition("foundry-8", lease, WorkTaskStatus.HandedOff, Now.AddMinutes(4), "draft PR ready");
        Assert.Equal(WorkTaskStatus.HandedOff, handedOff.Status);
        Assert.Null(handedOff.Lease);
        Assert.Equal(6, handedOff.Events.Count);
    }

    private static CodeRedIncident TestIncident() => new(1, "incident-8",
        "TrentBest/TheSingularityWorkshop.Foundry", "foundry-8", 8, "correlation-8",
        "agent-a", "implementation", "Investigating CI failure", "work/code-red-task-lifecycle",
        new string('a', 40), null, null, CodeRedSeverity.Critical, "Validation blocked", 1,
        Now, Now, null, true);

    private static CodeRedRecoveryApprovalReceipt TestReceipt(long taskEventSequence, long incidentSequence) =>
        new(1, "receipt-8", "test-host", "human-reviewer", "foundry-8", "incident-8",
            taskEventSequence, incidentSequence, CodeRedRecoveryAction.ResumeInvestigation,
            Now, Now.AddMinutes(5), "signed-test-receipt");

    private sealed class TestRecoveryReceiptVerifier : ICodeRedRecoveryReceiptVerifier
    {
        public bool IsTrustedIssuer(string issuer) => issuer == "test-host";
        public bool VerifySignature(string issuer, ReadOnlyMemory<byte> signingPayload, string signature) =>
            issuer == "test-host" && signature == "signed-test-receipt" && !signingPayload.IsEmpty;
    }

    private static WorkTaskRepairAttempt FailedAttempt() =>
        new(1, "foundry-8", "correlation-8", "TrentBest/TheSingularityWorkshop.Foundry",
            "work/code-red-task-lifecycle", ".github/workflows/build.yml", Now, 1,
            new string('a', 40), 1001, 1, new string('e', 64), true, false, true,
            new string('d', 64), Now.AddMinutes(1), Now.AddMinutes(2));

    private static WorkTaskRepairAttempt SuccessfulAttempt() =>
        new(1, "foundry-8", "correlation-8", "TrentBest/TheSingularityWorkshop.Foundry",
            "work/code-red-task-lifecycle", ".github/workflows/build.yml", Now, 1,
            new string('a', 40), 1001, 1, new string('f', 64), true, true, false,
            null, Now.AddMinutes(1), Now.AddMinutes(2));

    private static WorkTaskRepairEvidenceExpectation Expectation(WorkTaskRepairAttempt attempt) =>
        new(attempt.TaskId, attempt.CorrelationId, attempt.Repository, attempt.Branch,
            attempt.WorkflowPath, attempt.CandidateSha, attempt.TaskStartedAt);

    private static WorkTaskRequest Request(int maximumAttempts = 3, IReadOnlyList<string>? paths = null,
        string? baseSha = null, string taskId = "foundry-8", string correlationId = "correlation-8") =>
        new(1, taskId, correlationId, "TrentBest/TheSingularityWorkshop.Foundry", 8,
            "https://github.com/TrentBest/TheSingularityWorkshop.Foundry/issues/8",
            baseSha ?? new string('a', 40), paths ?? new[] { "src/Foundry.Workflow", "tests/Foundry.Workflow.Tests" },
            maximumAttempts);
}
