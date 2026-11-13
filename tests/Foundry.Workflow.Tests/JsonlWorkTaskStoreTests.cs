using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlWorkTaskStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReopensFromJournalAndRetainsEverySnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        try
        {
            var coordinator = new WorkTaskCoordinator();
            var initial = coordinator.Submit(Request());
            var store = new JsonlWorkTaskStore(path);
            store.Create(initial);

            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);
            store.Append(claimed);
            var investigating = coordinator.Transition("task-1", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "inspect task");
            store.Append(investigating);

            var reopened = new JsonlWorkTaskStore(path);
            var recovered = reopened.Get("task-1");
            Assert.Equal(WorkTaskStatus.Investigating, recovered.Status);
            Assert.Equal("agent-a", recovered.Lease!.AgentId);
            Assert.Equal(2, recovered.Events.Count);
            Assert.Equal(3, File.ReadAllLines(path).Length);
            Assert.Single(reopened.GetAll());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RejectsDuplicateCreationAndNonAppendOnlyHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        try
        {
            var coordinator = new WorkTaskCoordinator();
            var initial = coordinator.Submit(Request());
            var store = new JsonlWorkTaskStore(path);
            store.Create(initial);
            Assert.Throws<InvalidOperationException>(() => store.Create(initial));
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);
            store.Append(claimed);
            Assert.Throws<InvalidOperationException>(() => store.Append(claimed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RejectsCorruptJournalInsteadOfSilentlyDroppingHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{not-json}\n");
            Assert.Throws<InvalidDataException>(() => new JsonlWorkTaskStore(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }



    [Fact]
    public void RestoreRejectsSnapshotStatusAndLeaseThatDisagreeWithAuditHistory()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);
        var lease = claimed.Lease!;

        var wrongStatus = claimed with { Status = WorkTaskStatus.Investigating };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(wrongStatus));

        var wrongLease = claimed with
        {
            Lease = new WorkTaskLease("agent-b", Guid.NewGuid(), lease.ExpiresAt)
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(wrongLease));

        var brokenChain = claimed with
        {
            Events = new[] { claimed.Events[0] with { Previous = WorkTaskStatus.Implementing } }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(brokenChain));

        var invalidTransition = claimed with
        {
            Status = WorkTaskStatus.ReviewReady,
            Events = new[] { claimed.Events[0] with { Current = WorkTaskStatus.ReviewReady } }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(invalidTransition));
    }

    [Fact]
    public void RestoreRejectsOversizedOrControlCharacterAuditMetadata()
    {
        var coordinator = new WorkTaskCoordinator();
        coordinator.Submit(Request());
        var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);

        var oversizedLease = claimed with
        {
            Lease = claimed.Lease! with { AgentId = new string('a', 129) }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(oversizedLease));

        var controlAgent = claimed with
        {
            Events = new[] { claimed.Events[0] with { AgentId = "bad\u0001agent" } }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(controlAgent));

        var oversizedReason = claimed with
        {
            Events = new[] { claimed.Events[0] with { Reason = new string('r', 2049) } }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(oversizedReason));

        var controlReason = claimed with
        {
            Events = new[] { claimed.Events[0] with { Reason = "bad\u0001reason" } }
        };
        Assert.Throws<ArgumentException>(() => new WorkTaskCoordinator().Restore(controlReason));
    }

    [Fact]
    public void RehydratesCoordinatorAndReclaimsExpiredLeaseAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        try
        {
            var coordinator = new WorkTaskCoordinator();
            var initial = coordinator.Submit(Request());
            var store = new JsonlWorkTaskStore(path);
            store.Create(initial);
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);
            store.Append(claimed);
            var investigating = coordinator.Transition("task-1", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "inspect task");
            store.Append(investigating);

            var recoveredCoordinator = new WorkTaskCoordinator();
            foreach (var snapshot in new JsonlWorkTaskStore(path).GetAll())
                recoveredCoordinator.Restore(snapshot);

            var reclaimed = recoveredCoordinator.Claim("task-1", "agent-b",
                TimeSpan.FromMinutes(5), Now.AddMinutes(6));
            Assert.Equal(WorkTaskStatus.Claimed, reclaimed.Status);
            Assert.Equal("agent-b", reclaimed.Lease!.AgentId);
            Assert.Equal(3, reclaimed.Events.Count);
            Assert.Equal(WorkTaskStatus.Investigating, reclaimed.Events[2].Previous);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static WorkTaskRequest Request() => new(1, "task-1", "correlation-1",
        "TrentBest/TheSingularityWorkshop.Foundry", 8,
        "https://github.com/TrentBest/TheSingularityWorkshop.Foundry/issues/8",
        new string('a', 40), new[] { "src/Foundry.Workflow" }, 3);

    [Fact]
    public void PersistentCoordinatorRestoresAndPersistsEveryAcceptedMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        try
        {
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            coordinator.Submit(Request());
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now);
            var investigating = coordinator.Transition("task-1", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "inspect task");

            var restarted = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            var recovered = restarted.GetSnapshot("task-1");
            Assert.Equal(WorkTaskStatus.Investigating, recovered.Status);
            Assert.Equal(2, recovered.Events.Count);
            Assert.Equal(investigating.Lease!.LeaseId, recovered.Lease!.LeaseId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PersistentCoordinatorPersistsEvidenceGatedReviewDecisionAndCiIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        var attemptsPath = Path.Combine(Path.GetTempPath(), $"foundry-attempts-{Guid.NewGuid():N}.jsonl");
        try
        {
            var attemptJournal = new JsonlWorkTaskRepairAttemptJournal(attemptsPath);
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path), attemptJournal);
            coordinator.Submit(Request());
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(10), Now);
            var lease = claimed.Lease!.LeaseId;
            coordinator.Transition("task-1", lease, WorkTaskStatus.Investigating, Now, "inspect");
            coordinator.Transition("task-1", lease, WorkTaskStatus.Implementing, Now, "implement");
            coordinator.Transition("task-1", lease, WorkTaskStatus.Validating, Now, "validate");
            var attempt = new WorkTaskRepairAttempt(WorkTaskRepairAttempt.CurrentSchemaVersion, "task-1", "correlation-1",
                "TrentBest/TheSingularityWorkshop.Foundry", "work/task-1",
                ".github/workflows/build.yml", Now, 1, new string('a', 40), 1001, 1,
                new string('f', 64), true, true, false, null,
                Now.AddMinutes(1), Now.AddMinutes(2))
            { SourceArtifactIdentitySha256 = new string('c', 64) };
            var expectation = new WorkTaskRepairEvidenceExpectation("task-1", "correlation-1",
                "TrentBest/TheSingularityWorkshop.Foundry", "work/task-1",
                ".github/workflows/build.yml", new string('a', 40), Now);
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, attemptJournal.Append(attempt));

            // Simulate a crash after the attempt journal is flushed but before the task
            // snapshot records its decision. Recovery must consume the durable orphan attempt.
            coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path),
                new JsonlWorkTaskRepairAttemptJournal(attemptsPath));
            var ready = coordinator.ApplyRepairDecision("task-1", lease, attempt, expectation,
                TimeSpan.FromMinutes(30), Now.AddMinutes(3));

            Assert.Equal(WorkTaskStatus.ReviewReady, ready.Status);
            Assert.Equal(1001, ready.Events[^1].CiRunId);
            Assert.Equal(1, ready.Events[^1].CiRunAttempt);
            Assert.Equal(new string('f', 64), ready.Events[^1].EvidencePayloadSha256);
            Assert.Equal(new string('c', 64), ready.Events[^1].SourceArtifactIdentitySha256);

            var restarted = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path),
                new JsonlWorkTaskRepairAttemptJournal(attemptsPath));
            var recovered = restarted.GetSnapshot("task-1");
            Assert.Equal(WorkTaskStatus.ReviewReady, recovered.Status);
            Assert.Equal(ready.Events[^1], recovered.Events[^1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(attemptsPath)) File.Delete(attemptsPath);
        }
    }

    [Fact]
    public void PersistentRepairDecisionRequiresDurableExactCurrentAttempt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        var attemptsPath = Path.Combine(Path.GetTempPath(), $"foundry-attempts-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(attemptsPath);
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path), journal);
            coordinator.Submit(Request());
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(10), Now);
            var lease = claimed.Lease!.LeaseId;
            coordinator.Transition("task-1", lease, WorkTaskStatus.Investigating, Now, "inspect");
            coordinator.Transition("task-1", lease, WorkTaskStatus.Implementing, Now, "implement");
            coordinator.Transition("task-1", lease, WorkTaskStatus.Validating, Now, "validate");
            var attempt = new WorkTaskRepairAttempt(WorkTaskRepairAttempt.CurrentSchemaVersion, "task-1", "correlation-1",
                "TrentBest/TheSingularityWorkshop.Foundry", "work/task-1",
                ".github/workflows/build.yml", Now, 1, new string('a', 40), 1001, 1,
                new string('f', 64), true, true, false, null,
                Now.AddMinutes(1), Now.AddMinutes(2))
            { SourceArtifactIdentitySha256 = new string('c', 64) };
            var expectation = new WorkTaskRepairEvidenceExpectation("task-1", "correlation-1",
                "TrentBest/TheSingularityWorkshop.Foundry", "work/task-1",
                ".github/workflows/build.yml", new string('a', 40), Now);

            Assert.Throws<InvalidOperationException>(() => coordinator.ApplyRepairDecision(
                "task-1", lease, attempt, expectation, TimeSpan.FromMinutes(30), Now.AddMinutes(3)));
            Assert.Equal(WorkTaskStatus.Validating, coordinator.GetSnapshot("task-1").Status);

            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(attempt));
            var altered = attempt with { EvidencePayloadSha256 = new string('e', 64) };
            Assert.Throws<InvalidOperationException>(() => coordinator.ApplyRepairDecision(
                "task-1", lease, altered, expectation, TimeSpan.FromMinutes(30), Now.AddMinutes(3)));

            var ready = coordinator.ApplyRepairDecision("task-1", lease, attempt, expectation,
                TimeSpan.FromMinutes(30), Now.AddMinutes(3));
            Assert.Equal(WorkTaskStatus.ReviewReady, ready.Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(attemptsPath)) File.Delete(attemptsPath);
        }
    }

    [Fact]
    public void PersistentCoordinatorFailsClosedAfterJournalWriteFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-{Guid.NewGuid():N}.jsonl");
        Directory.CreateDirectory(path);
        try
        {
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            Assert.Throws<InvalidOperationException>(() => coordinator.Submit(Request()));
            Assert.Throws<InvalidOperationException>(() =>
                coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(5), Now));
            Assert.Throws<InvalidOperationException>(() => coordinator.GetSnapshot("task-1"));
        }
        finally
        {
            Directory.Delete(path);
        }
    }

}
