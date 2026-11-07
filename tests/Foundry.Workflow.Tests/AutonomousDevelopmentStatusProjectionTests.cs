using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class AutonomousDevelopmentStatusProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
    private static readonly string Sha = new('a', 40);

    [Fact]
    public void ProjectionIncludesConsumedApprovalProvenanceWithoutReceiptPayload()
    {
        var snapshot = CreateApprovedRecoverySnapshot();
        var projection = AutonomousDevelopmentStatusProjection.Create(snapshot, Start.AddMinutes(6));

        Assert.NotNull(projection.LatestApproval);
        Assert.Equal("receipt-1", projection.LatestApproval.ReceiptId);
        Assert.Equal("test-host", projection.LatestApproval.Issuer);
        Assert.Equal("human-reviewer", projection.LatestApproval.Principal);
        Assert.Equal("incident-1", projection.LatestApproval.IncidentId);
        Assert.Equal(1, projection.LatestApproval.IncidentSequence);
        Assert.Equal(5, projection.LatestApproval.TaskEventSequence);
        Assert.Equal(Start.AddMinutes(5), projection.LatestApproval.ConsumedAt);
    }

    [Fact]
    public void ProjectionRejectsApprovalMetadataWithoutItsValidatedReceipt()
    {
        var snapshot = CreateApprovedRecoverySnapshot();
        var recoveryEvent = snapshot.Events[^1];
        var malformed = snapshot with
        {
            Events = snapshot.Events.Take(snapshot.Events.Count - 1)
                .Append(recoveryEvent with { ApprovalReceipt = null })
                .ToArray()
        };

        Assert.Throws<ArgumentException>(() =>
            AutonomousDevelopmentStatusProjection.Create(malformed, Start.AddMinutes(6)));
    }

    private static WorkTaskSnapshot CreateApprovedRecoverySnapshot()
    {
        var coordinator = new WorkTaskCoordinator(new TestRecoveryReceiptVerifier());
        coordinator.Submit(new WorkTaskRequest(1, "task-1", "corr-1",
            "TrentBest/TheSingularityWorkshop.FSM_Foundry", 8,
            "https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/issues/8",
            Sha, new[] { "src/Foundry.Workflow" }, 3));
        var lease = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(30), Start).Lease!;
        coordinator.Transition("task-1", lease.LeaseId, WorkTaskStatus.Investigating,
            Start.AddSeconds(1), "inspect");
        coordinator.Transition("task-1", lease.LeaseId, WorkTaskStatus.Implementing,
            Start.AddSeconds(2), "implement");
        coordinator.Transition("task-1", lease.LeaseId, WorkTaskStatus.Validating,
            Start.AddSeconds(3), "validate");
        coordinator.Transition("task-1", lease.LeaseId, WorkTaskStatus.CodeRed,
            Start.AddSeconds(4), "validation requires human review");

        var incident = new CodeRedIncident(1, "incident-1",
            "TrentBest/TheSingularityWorkshop.FSM_Foundry", "task-1", 8, "corr-1",
            "agent-a", "implementation", "Review failed CI evidence", "work/code-red-task-lifecycle",
            Sha, null, null, CodeRedSeverity.Critical, "Validation blocked", 1,
            Start.AddMinutes(5), Start.AddMinutes(5), null, true);
        var receipt = new CodeRedRecoveryApprovalReceipt(1, "receipt-1", "test-host",
            "human-reviewer", "task-1", "incident-1", 5, 1,
            CodeRedRecoveryAction.ResumeInvestigation, Start.AddMinutes(5),
            Start.AddMinutes(10), "signed-test-receipt");

        return coordinator.RecoverFromCodeRed("task-1", lease.LeaseId, incident, receipt,
            Start.AddMinutes(5), "resume after host approval");
    }

    private sealed class TestRecoveryReceiptVerifier : ICodeRedRecoveryReceiptVerifier
    {
        public bool IsTrustedIssuer(string issuer) => issuer == "test-host";

        public bool VerifySignature(string issuer, ReadOnlyMemory<byte> signingPayload, string signature) =>
            issuer == "test-host" && signature == "signed-test-receipt" && !signingPayload.IsEmpty;
    }

    private static WorkTaskCoordinator CreateCoordinator()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(new WorkTaskRequest(1, "task-1", "corr-1",
            "TrentBest/TheSingularityWorkshop.FSM_Foundry", 8,
            "https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/issues/8",
            Sha, new[] { "src/Foundry.Workflow" }, 3));
        return coordinator;
    }

    [Fact]
    public void QueuedProjectionIsVersionedAndContainsNoLeaseOrActionCapability()
    {
        var snapshot = CreateCoordinator().GetSnapshot("task-1");

        var projection = AutonomousDevelopmentStatusProjection.Create(snapshot, Start);

        Assert.Equal(AutonomousDevelopmentStatusProjection.CurrentSchemaVersion, projection.SchemaVersion);
        Assert.Equal(Start, projection.ObservedAt);
        Assert.Equal("task-1", projection.TaskId);
        Assert.Equal("corr-1", projection.CorrelationId);
        Assert.Equal(WorkTaskStatus.Queued, projection.Status);
        Assert.Equal(0, projection.EventSequence);
        Assert.Null(projection.AgentId);
        Assert.Null(projection.LeaseExpiresAt);
        Assert.False(projection.LeaseActive);
        Assert.Null(projection.LatestApproval);
    }

    [Fact]
    public void ActiveProjectionReportsLeaseFreshnessAndLatestEventProvenance()
    {
        var coordinator = CreateCoordinator();
        var lease = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Start).Lease!;
        var snapshot = coordinator.Transition("task-1", lease.LeaseId,
            WorkTaskStatus.Investigating, Start.AddSeconds(1), "inspect candidate");

        var fresh = AutonomousDevelopmentStatusProjection.Create(snapshot, Start.AddMinutes(1));
        var expired = AutonomousDevelopmentStatusProjection.Create(snapshot, Start.AddMinutes(6));

        Assert.Equal(Start.AddMinutes(1), fresh.ObservedAt);
        Assert.Equal("agent-a", fresh.AgentId);
        Assert.True(fresh.LeaseActive);
        Assert.False(expired.LeaseActive);
        Assert.Equal(2, fresh.EventSequence);
        Assert.Equal(Start.AddSeconds(1), fresh.LastTransitionAt);
        Assert.Equal("inspect candidate", fresh.LastTransitionReason);
        Assert.Equal("corr-1", fresh.CorrelationId);
        Assert.Equal(Sha, fresh.BaseSha);
    }

    [Fact]
    public void ProjectionRejectsFutureDatedEvents()
    {
        var coordinator = CreateCoordinator();
        var lease = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Start).Lease!;
        var snapshot = coordinator.Transition("task-1", lease.LeaseId,
            WorkTaskStatus.Investigating, Start.AddSeconds(1), "inspect candidate");
        var futureDated = snapshot with
        {
            Events = snapshot.Events.Select(item => item with
            {
                OccurredAt = item.OccurredAt.AddMinutes(1)
            }).ToArray()
        };

        Assert.Throws<ArgumentException>(() =>
            AutonomousDevelopmentStatusProjection.Create(futureDated, Start.AddSeconds(30)));
    }

    [Fact]
    public void ProjectionRejectsReplayedOrDiscontinuousEventSequence()
    {
        var coordinator = CreateCoordinator();
        var lease = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Start).Lease!;
        var snapshot = coordinator.Transition("task-1", lease.LeaseId,
            WorkTaskStatus.Investigating, Start.AddSeconds(1), "inspect candidate");

        var replayed = snapshot with { Events = new[] { snapshot.Events[0], snapshot.Events[0] } };
        var wrongStatus = snapshot with { Status = WorkTaskStatus.Implementing };

        Assert.Throws<ArgumentException>(() =>
            AutonomousDevelopmentStatusProjection.Create(replayed, Start.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() =>
            AutonomousDevelopmentStatusProjection.Create(wrongStatus, Start.AddMinutes(1)));
    }

    [Fact]
    public void ProjectionRejectsLeaseOwnerMismatchAndPreservesCorrelationIdentity()
    {
        var coordinator = CreateCoordinator();
        var lease = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Start).Lease!;
        var snapshot = coordinator.Transition("task-1", lease.LeaseId,
            WorkTaskStatus.Investigating, Start.AddSeconds(1), "inspect candidate");

        var mismatchedLease = snapshot with
        {
            Lease = snapshot.Lease! with { AgentId = "agent-b" }
        };
        var mismatchedCorrelation = snapshot with
        {
            Request = snapshot.Request with { CorrelationId = "corr-other" }
        };

        Assert.Throws<ArgumentException>(() =>
            AutonomousDevelopmentStatusProjection.Create(mismatchedLease, Start.AddMinutes(1)));
        Assert.Equal("corr-other", AutonomousDevelopmentStatusProjection.Create(
            mismatchedCorrelation, Start.AddMinutes(1)).CorrelationId);
    }
}
