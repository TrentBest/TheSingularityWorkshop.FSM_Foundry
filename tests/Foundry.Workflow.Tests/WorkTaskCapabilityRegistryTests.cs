using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class WorkTaskCapabilityRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);
    private static readonly string BaseSha = new('a', 40);
    private static readonly string CandidateSha = new('b', 40);

    private static WorkTaskRequest Request() => new(1, "task-8", "correlation-8",
        "TrentBest/TheSingularityWorkshop.Foundry", 8,
        "https://github.com/TrentBest/TheSingularityWorkshop.Foundry/issues/8",
        BaseSha, new[] { "src/Foundry.Workflow", "tests/Foundry.Workflow.Tests" }, 3);

    private static WorkTaskSnapshot ActiveTask(TimeSpan? leaseDuration = null)
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        return coordinator.Claim("task-8", "agent-a", leaseDuration ?? TimeSpan.FromMinutes(20), Now);
    }

    private static WorkTaskCapability Grant(WorkTaskCapabilityRegistry registry,
        WorkTaskSnapshot task, TimeSpan? lifetime = null) =>
        registry.Grant(task, "work/task-8", CandidateSha,
            new[] { "src/Foundry.Workflow" },
            new[] { WorkTaskAction.ReadRepository, WorkTaskAction.WriteFiles, WorkTaskAction.CreateCommit },
            Now, lifetime ?? TimeSpan.FromMinutes(10));

    private static void Demand(WorkTaskCapabilityRegistry registry, WorkTaskCapability capability,
        WorkTaskSnapshot task, WorkTaskAction action, string? path = null,
        DateTimeOffset? now = null, string? branch = null, string? candidateSha = null) =>
        registry.Demand(capability.CapabilityId, task, branch ?? "work/task-8",
            candidateSha ?? CandidateSha, action, path, now ?? Now.AddMinutes(1));

    [Fact]
    public void CapabilityAuthorizesOnlyExactTaskProvenanceLeaseActionsAndPaths()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var task = ActiveTask();
        var capability = Grant(registry, task);

        Demand(registry, capability, task, WorkTaskAction.ReadRepository,
            "src/Foundry.Workflow/WorkTaskCoordinator.cs");
        Demand(registry, capability, task, WorkTaskAction.WriteFiles,
            "src/Foundry.Workflow/WorkTaskCoordinator.cs");
        Demand(registry, capability, task, WorkTaskAction.CreateCommit);

        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            task with { Request = task.Request with { TaskId = "other-task" } },
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            task with { Request = task.Request with { Repository = "Other/Repository" } },
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            task with { Lease = task.Lease! with { AgentId = "agent-b" } },
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            task with { Lease = task.Lease! with { LeaseId = Guid.NewGuid() } },
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs", branch: "work/other"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs", candidateSha: new string('c', 40)));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.CreateDraftPullRequest));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            task with { Status = WorkTaskStatus.Stopped, Lease = null }, WorkTaskAction.WriteFiles,
            "src/Foundry.Workflow/file.cs"));
    }

    [Fact]
    public void CodeRedSuspendsMutationAndRequiresFreshCapabilityAfterRecovery()
    {
        var coordinator = new WorkTaskCoordinator(new RecoveryReceiptVerifier());
        coordinator.Submit(Request());
        var claimed = coordinator.Claim("task-8", "agent-a", TimeSpan.FromMinutes(20), Now);
        var registry = new WorkTaskCapabilityRegistry();
        var beforeCodeRed = Grant(registry, claimed);

        var codeRed = coordinator.Transition("task-8", claimed.Lease!.LeaseId,
            WorkTaskStatus.CodeRed, Now.AddMinutes(1), "Escalated for human review.");

        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, beforeCodeRed,
            codeRed, WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, beforeCodeRed,
            codeRed, WorkTaskAction.CreateCommit));

        Assert.Throws<UnauthorizedAccessException>(() => registry.Grant(codeRed, "work/task-8",
            CandidateSha, new[] { "src/Foundry.Workflow" },
            new[] { WorkTaskAction.ReadRepository, WorkTaskAction.WriteFiles },
            Now.AddMinutes(1), TimeSpan.FromMinutes(5)));

        var readOnly = registry.Grant(codeRed, "work/task-8", CandidateSha,
            new[] { "src/Foundry.Workflow" }, new[] { WorkTaskAction.ReadRepository },
            Now.AddMinutes(1), TimeSpan.FromMinutes(5));
        Demand(registry, readOnly, codeRed, WorkTaskAction.ReadRepository,
            "src/Foundry.Workflow/WorkTaskCoordinator.cs", Now.AddMinutes(2));

        var incidentTime = Now.AddMinutes(1);
        var incident = new CodeRedIncident(1, "incident-8",
            "TrentBest/TheSingularityWorkshop.Foundry", "task-8", 8, "correlation-8",
            "agent-a", "implementation", "Investigating blocker", "work/task-8", CandidateSha,
            null, null, CodeRedSeverity.Critical, "Human review required", 3,
            incidentTime, incidentTime, null, true);
        var receipt = new CodeRedRecoveryApprovalReceipt(1, "receipt-8", "test-host",
            "human-reviewer", "task-8", incident.IncidentId, codeRed.Events.Count,
            incident.Sequence, CodeRedRecoveryAction.ResumeInvestigation,
            Now.AddMinutes(2), Now.AddMinutes(7), "signed-test-receipt");
        var recovered = coordinator.RecoverFromCodeRed("task-8", claimed.Lease.LeaseId,
            incident, receipt, Now.AddMinutes(3), "Host-approved CODE RED recovery.");

        // The pre-CODE RED mutation grant stays invalid even after the task resumes.
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, beforeCodeRed,
            recovered, WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs",
            Now.AddMinutes(4)));

        var fresh = registry.Grant(recovered, "work/task-8", CandidateSha,
            new[] { "src/Foundry.Workflow" },
            new[] { WorkTaskAction.ReadRepository, WorkTaskAction.WriteFiles, WorkTaskAction.CreateCommit },
            Now.AddMinutes(3), TimeSpan.FromMinutes(10));
        Demand(registry, fresh, recovered, WorkTaskAction.WriteFiles,
            "src/Foundry.Workflow/WorkTaskCoordinator.cs", Now.AddMinutes(4));
    }

    [Fact]
    public void CapabilityRejectsEscapingPathsAndScopeExpansion()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var task = ActiveTask();
        Assert.Throws<ArgumentException>(() => registry.Grant(task, "work/task-8", CandidateSha,
            new[] { "../secrets" }, new[] { WorkTaskAction.WriteFiles }, Now, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentException>(() => registry.Grant(task, "work/task-8", CandidateSha,
            new[] { "docs" }, new[] { WorkTaskAction.WriteFiles }, Now, TimeSpan.FromMinutes(5)));

        var capability = Grant(registry, task);
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/../../secrets.txt"));
    }

    [Fact]
    public void ReviewReadyTaskCanGrantOnlyDraftPullRequestHandoff()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var claimed = coordinator.Claim("task-8", "agent-a", TimeSpan.FromMinutes(20), Now);
        var leaseId = claimed.Lease!.LeaseId;
        coordinator.Transition("task-8", leaseId, WorkTaskStatus.Investigating,
            Now.AddSeconds(10), "Inspect the requested change.");
        coordinator.Transition("task-8", leaseId, WorkTaskStatus.Implementing,
            Now.AddSeconds(20), "Implement the isolated change.");
        coordinator.Transition("task-8", leaseId, WorkTaskStatus.Validating,
            Now.AddSeconds(30), "Validate the exact candidate.");

        var attempt = new WorkTaskRepairAttempt(1, "task-8", "correlation-8",
            "TrentBest/TheSingularityWorkshop.Foundry", "work/task-8",
            ".github/workflows/build.yml", Now, 1, BaseSha, 1001, 1,
            new string('f', 64), true, true, false, null,
            Now.AddMinutes(1), Now.AddMinutes(2));
        var expectation = new WorkTaskRepairEvidenceExpectation("task-8", "correlation-8",
            "TrentBest/TheSingularityWorkshop.Foundry", "work/task-8",
            ".github/workflows/build.yml", CandidateSha, Now, 1001, 1);
        // Evidence must identify the exact candidate being authorized.
        attempt = attempt with { CandidateSha = CandidateSha };
        var reviewReady = coordinator.ApplyRepairDecision("task-8", leaseId,
            Array.Empty<WorkTaskRepairAttempt>(), attempt, expectation,
            TimeSpan.FromMinutes(30), Now.AddMinutes(3));
        Assert.Equal(WorkTaskStatus.ReviewReady, reviewReady.Status);
        Assert.NotNull(reviewReady.Lease);

        var registry = new WorkTaskCapabilityRegistry();
        var capability = registry.Grant(reviewReady, "work/task-8", CandidateSha,
            new[] { "src/Foundry.Workflow" }, new[] { WorkTaskAction.CreateDraftPullRequest },
            Now.AddMinutes(3), TimeSpan.FromMinutes(5));

        Demand(registry, capability, reviewReady, WorkTaskAction.CreateDraftPullRequest,
            now: Now.AddMinutes(2));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            reviewReady, WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs", Now.AddMinutes(2)));
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability,
            reviewReady, WorkTaskAction.RequestCiRun, now: Now.AddMinutes(2)));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Grant(reviewReady, "work/task-8",
            CandidateSha, new[] { "src/Foundry.Workflow" },
            new[] { WorkTaskAction.ReadRepository, WorkTaskAction.WriteFiles },
            Now.AddMinutes(1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DraftPullRequestHandoffCannotBeAuthorizedBeforeReviewReady()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var claimed = ActiveTask();
        Assert.Throws<UnauthorizedAccessException>(() => registry.Grant(claimed,
            "work/task-8", CandidateSha, new[] { "src/Foundry.Workflow" },
            new[] { WorkTaskAction.CreateDraftPullRequest },
            Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void CapabilityExpiresAndCanBeRevoked()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var task = ActiveTask();
        var capability = Grant(registry, task, TimeSpan.FromMinutes(5));

        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs", Now.AddMinutes(5)));

        registry.Revoke(capability.CapabilityId);
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs"));
        registry.Revoke(capability.CapabilityId);
    }

    [Fact]
    public void CapabilityCannotOutliveItsOwningAgentLease()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var task = ActiveTask(TimeSpan.FromMinutes(2));
        var capability = Grant(registry, task, TimeSpan.FromMinutes(30));

        Assert.Equal(task.Lease!.ExpiresAt, capability.ExpiresAt);
        Assert.Throws<UnauthorizedAccessException>(() => Demand(registry, capability, task,
            WorkTaskAction.WriteFiles, "src/Foundry.Workflow/file.cs", task.Lease.ExpiresAt));
    }

    [Fact]
    public void CapabilityCannotBeIssuedWithoutAnActiveLease()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var queued = new WorkTaskCoordinator().Submit(Request());
        Assert.Throws<UnauthorizedAccessException>(() => Grant(registry, queued));
    }

    [Fact]
    public void CapabilityRejectsCanonicalBranchesUnknownActionsAndExcessiveLifetime()
    {
        var registry = new WorkTaskCapabilityRegistry();
        var task = ActiveTask();
        Assert.Throws<ArgumentException>(() => registry.Grant(task, "development", CandidateSha,
            new[] { "src/Foundry.Workflow" }, new[] { WorkTaskAction.WriteFiles },
            Now, TimeSpan.FromMinutes(5)));
        foreach (var invalidBranch in new[]
        {
            "work/feature name", "work/feature?", "work/feature*", "work/feature.lock",
            "work/.hidden", "work/feature.", "work/feature@{1}", "work//feature",
            "work/feature~1", "work/feature^1", "work/feature:name",
            "work/segment./feature", "work/bad\\u0001branch"
        })
        {
            Assert.Throws<ArgumentException>(() => registry.Grant(task, invalidBranch, CandidateSha,
                new[] { "src/Foundry.Workflow" }, new[] { WorkTaskAction.WriteFiles },
                Now, TimeSpan.FromMinutes(5)));
        }
        Assert.Throws<ArgumentException>(() => registry.Grant(task, "work/task-8", CandidateSha,
            new[] { "src/Foundry.Workflow" }, new[] { (WorkTaskAction)99 },
            Now, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(registry, task, TimeSpan.FromHours(2)));
    }
    private sealed class RecoveryReceiptVerifier : ICodeRedRecoveryReceiptVerifier
    {
        public bool IsTrustedIssuer(string issuer) => issuer == "test-host";
        public bool VerifySignature(string issuer, ReadOnlyMemory<byte> signingPayload, string signature) =>
            issuer == "test-host" && signature == "signed-test-receipt" && !signingPayload.IsEmpty;
    }

}
