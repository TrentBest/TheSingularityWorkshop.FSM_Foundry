using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CodeRedRecoveryAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PersistentRecoveryDelegatesHumanApprovalToHostAndPersistsIncidentProvenance()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            var (lease, incident) = CreateCodeRedTask(tasks);
            incidents.Create(incident);
            var gate = new TestApprovalGate();
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks, gate);

            var recovered = recovery.RecoverFromCodeRed("task-recovery", lease,
                incident.IncidentId, Now.AddMinutes(1), "host approved resume after investigation");

            Assert.Equal(1, gate.Calls);
            Assert.Equal(WorkTaskStatus.CodeRed, gate.LastTask!.Status);
            Assert.Equal(incident.IncidentId, gate.LastIncident!.IncidentId);
            Assert.Equal(incident.Sequence, gate.LastIncident.Sequence);
            Assert.Equal(WorkTaskStatus.Investigating, recovered.Status);
            var audit = recovered.Events[^1];
            Assert.Equal(incident.IncidentId, audit.RecoveryIncidentId);
            Assert.Equal(incident.Sequence, audit.RecoveryIncidentSequence);

            var restarted = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var restored = restarted.GetSnapshot("task-recovery");
            Assert.Equal(WorkTaskStatus.Investigating, restored.Status);
            Assert.Equal(audit, restored.Events[^1]);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public void HostApprovalGateRejectsRecoveryWithoutMutatingTask()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            var (lease, incident) = CreateCodeRedTask(tasks);
            incidents.Create(incident);
            var gate = new TestApprovalGate { Approved = false };
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks, gate);
            var before = tasks.GetSnapshot("task-recovery");

            Assert.Throws<UnauthorizedAccessException>(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, Now.AddMinutes(1), "not approved"));

            var after = tasks.GetSnapshot("task-recovery");
            Assert.Equal(1, gate.Calls);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.Events, after.Events);
            Assert.Equal(WorkTaskStatus.CodeRed, after.Status);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public void IncidentChangeDuringHostApprovalInvalidatesTheDecision()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            var (lease, incident) = CreateCodeRedTask(tasks);
            incidents.Create(incident);
            var gate = new TestApprovalGate
            {
                OnDemand = (current, _, _) => incidents.Update(current.Update(
                    "Rechecking evidence", "A newer CI attempt arrived",
                    new string('b', 40), 1235, 2, Now.AddSeconds(30)))
            };
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks, gate);

            Assert.Throws<UnauthorizedAccessException>(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, Now.AddMinutes(1), "stale approval"));

            Assert.Equal(WorkTaskStatus.CodeRed, tasks.GetSnapshot("task-recovery").Status);
            Assert.Equal(2, incidents.Get(incident.IncidentId).Sequence);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public void TaskChangeDuringHostApprovalInvalidatesTheDecision()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            var (lease, incident) = CreateCodeRedTask(tasks);
            incidents.Create(incident);
            var gate = new TestApprovalGate
            {
                OnDemand = (_, task, _) => Assert.Equal(WorkTaskStatus.CodeRed, task.Status)
            };
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks, gate);

            // Simulate a task mutation after the host evaluated the exact snapshot.
            gate.OnDemand = (_, _, _) => tasks.Transition("task-recovery", lease,
                WorkTaskStatus.Blocked, Now.AddSeconds(30), "new blocker arrived");

            Assert.Throws<InvalidOperationException>(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, Now.AddMinutes(1), "stale task approval"));

            Assert.Equal(WorkTaskStatus.Blocked, tasks.GetSnapshot("task-recovery").Status);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public void ApprovalGateRunsOutsideIncidentStoreLock()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            var (lease, incident) = CreateCodeRedTask(tasks);
            incidents.Create(incident);
            var updateCompleted = false;
            var gate = new TestApprovalGate
            {
                OnDemand = (current, _, _) =>
                {
                    incidents.Update(current.Update("Approval pending", "Incident updated during host check",
                        new string('c', 40), 1236, 3, Now.AddSeconds(30)));
                    updateCompleted = true;
                }
            };
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks, gate);

            Assert.Throws<UnauthorizedAccessException>(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, Now.AddMinutes(1), "approval became stale"));
            Assert.True(updateCompleted);
            Assert.Equal(WorkTaskStatus.CodeRed, tasks.GetSnapshot("task-recovery").Status);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    private static (Guid Lease, CodeRedIncident Incident) CreateCodeRedTask(
        PersistentWorkTaskCoordinator coordinator)
    {
        coordinator.Submit(Request());
        var lease = coordinator.Claim("task-recovery", "agent-recovery",
            TimeSpan.FromMinutes(30), Now).Lease!.LeaseId;
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Validating, Now, "validate");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.CodeRed, Now, "required check failed");
        return (lease, CreateIncident());
    }

    private static CodeRedIncident CreateIncident() => new(1, "incident-recovery-1",
        "TrentBest/TheSingularityWorkshop.Foundry", "task-recovery", 8, "correlation-recovery",
        "agent-recovery", "implementation", "Investigating CI failure", "work/code-red-task-lifecycle",
        new string('a', 40), 1234, 1, CodeRedSeverity.Critical, "Required workflow failed",
        3, Now, Now, null, true);

    private static WorkTaskRequest Request() => new(1, "task-recovery", "correlation-recovery",
        "TrentBest/TheSingularityWorkshop.Foundry", 8,
        "https://github.com/TrentBest/TheSingularityWorkshop.Foundry/issues/8",
        new string('a', 40), new[] { "src/Foundry.Workflow" }, 3);

    private sealed class TestApprovalGate : ICodeRedRecoveryApprovalGate
    {
        public bool Approved { get; init; } = true;
        public int Calls { get; private set; }
        public CodeRedIncident? LastIncident { get; private set; }
        public WorkTaskSnapshot? LastTask { get; private set; }
        public Action<CodeRedIncident, WorkTaskSnapshot, DateTimeOffset>? OnDemand { get; set; }

        public void DemandApproval(CodeRedIncident incident, WorkTaskSnapshot task, DateTimeOffset requestedAt)
        {
            Calls++;
            LastIncident = incident;
            LastTask = task;
            OnDemand?.Invoke(incident, task, requestedAt);
            if (!Approved)
                throw new UnauthorizedAccessException("The host has no explicit human approval for this recovery.");
        }
    }
}
