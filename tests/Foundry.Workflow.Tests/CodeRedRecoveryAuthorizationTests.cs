using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CodeRedRecoveryAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PersistentRecoveryVerifiesReceiptAndPersistsSingleUseAuditEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-recovery-{Guid.NewGuid():N}.jsonl");
        try
        {
            var verifier = new TestVerifier();
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path),
                recoveryReceiptVerifier: verifier);
            var (lease, incident, receipt) = CreateCodeRedTask(coordinator);
            var recovered = coordinator.RecoverFromCodeRed("task-recovery", lease, incident,
                receipt, Now.AddMinutes(1), "resume after approved investigation");

            Assert.Equal(WorkTaskStatus.Investigating, recovered.Status);
            var audit = recovered.Events[^1];
            Assert.Equal(receipt.ReceiptId, audit.ApprovalReceiptId);
            Assert.Equal(receipt.Issuer, audit.ApprovalIssuer);
            Assert.Equal(receipt.Principal, audit.ApprovalPrincipal);
            Assert.Equal(receipt, audit.ApprovalReceipt);
            Assert.Equal(incident.IncidentId, audit.ApprovalIncidentId);
            Assert.Equal(incident.Sequence, audit.ApprovalIncidentSequence);
            Assert.Equal(receipt.TaskEventSequence, audit.ApprovalTaskEventSequence);

            var restarted = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path),
                recoveryReceiptVerifier: verifier);
            var restored = restarted.GetSnapshot("task-recovery");
            Assert.Equal(audit, restored.Events[^1]);
            Assert.Equal(WorkTaskStatus.Investigating, restored.Status);
            Assert.Throws<InvalidOperationException>(() => restarted.RecoverFromCodeRed(
                "task-recovery", lease, incident, receipt, Now.AddMinutes(2), "replay"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InvalidSignatureAndUntrustedIssuerLeaveTaskInCodeRed()
    {
        var coordinator = new WorkTaskCoordinator(new TestVerifier(acceptSignature: false));
        var (lease, incident, receipt) = CreateCodeRedTask(coordinator);
        Assert.Throws<UnauthorizedAccessException>(() => coordinator.RecoverFromCodeRed(
            "task-recovery", lease, incident, receipt, Now.AddMinutes(1), "invalid signature"));
        Assert.Equal(WorkTaskStatus.CodeRed, coordinator.GetSnapshot("task-recovery").Status);

        var untrusted = receipt with { Issuer = "unknown-host" };
        Assert.Throws<UnauthorizedAccessException>(() => coordinator.RecoverFromCodeRed(
            "task-recovery", lease, incident, untrusted, Now.AddMinutes(1), "untrusted issuer"));
        Assert.Equal(WorkTaskStatus.CodeRed, coordinator.GetSnapshot("task-recovery").Status);
    }

    [Fact]
    public void ExpiredOrSequenceMismatchedReceiptIsRejectedWithoutMutation()
    {
        var coordinator = new WorkTaskCoordinator(new TestVerifier());
        var (lease, incident, receipt) = CreateCodeRedTask(coordinator);

        Assert.Throws<UnauthorizedAccessException>(() => coordinator.RecoverFromCodeRed(
            "task-recovery", lease, incident, receipt, Now.AddMinutes(11), "expired"));
        var wrongTaskSequence = receipt with { TaskEventSequence = receipt.TaskEventSequence - 1 };
        Assert.Throws<UnauthorizedAccessException>(() => coordinator.RecoverFromCodeRed(
            "task-recovery", lease, incident, wrongTaskSequence, Now.AddMinutes(1), "stale task sequence"));
        var wrongIncidentSequence = receipt with { IncidentSequence = incident.Sequence + 1 };
        Assert.Throws<UnauthorizedAccessException>(() => coordinator.RecoverFromCodeRed(
            "task-recovery", lease, incident, wrongIncidentSequence, Now.AddMinutes(1), "stale incident sequence"));
        Assert.Equal(WorkTaskStatus.CodeRed, coordinator.GetSnapshot("task-recovery").Status);
    }

    [Fact]
    public void PersistentRecoveryReadsTheLatestDurableIncidentAndRejectsStaleApproval()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        try
        {
            var verifier = new TestVerifier();
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath),
                recoveryReceiptVerifier: verifier);
            var (lease, incident, receipt) = CreateCodeRedTask(tasks);
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            incidents.Create(incident);
            incidents.Update(incident.Update("Rechecking evidence", "A newer CI attempt arrived",
                new string('b', 40), 1235, 2, Now.AddSeconds(30)));
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks);

            Assert.Throws<UnauthorizedAccessException>(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, receipt,
                Now.AddMinutes(1), "stale approval must not recover newer incident"));
            Assert.Equal(WorkTaskStatus.CodeRed, tasks.GetSnapshot("task-recovery").Status);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public async Task IncidentUpdatesAreSerializedUntilPersistentRecoveryCommits()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-task-{Guid.NewGuid():N}.jsonl");
        var incidentPath = Path.Combine(Path.GetTempPath(), $"foundry-recovery-incident-{Guid.NewGuid():N}.jsonl");
        using var verifier = new BlockingTestVerifier();
        try
        {
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath),
                recoveryReceiptVerifier: verifier);
            var (lease, incident, receipt) = CreateCodeRedTask(tasks);
            var incidents = new JsonlCodeRedIncidentStore(incidentPath);
            incidents.Create(incident);
            var recovery = new PersistentCodeRedRecoveryCoordinator(incidents, tasks);
            var recoveryTask = Task.Run(() => recovery.RecoverFromCodeRed(
                "task-recovery", lease, incident.IncidentId, receipt,
                Now.AddMinutes(1), "approved recovery"));

            await verifier.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updated = incident.Update("Rechecking evidence", "A newer CI attempt arrived",
                new string('b', 40), 1235, 2, Now.AddSeconds(30));
            var updateTask = Task.Run(() =>
            {
                updateStarted.TrySetResult();
                return incidents.Update(updated);
            });
            await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(updateTask.IsCompleted);

            verifier.AllowVerification.Set();
            Assert.Equal(WorkTaskStatus.Investigating, (await recoveryTask).Status);
            Assert.Equal(2, (await updateTask).Sequence);
        }
        finally
        {
            verifier.AllowVerification.Set();
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(incidentPath)) File.Delete(incidentPath);
        }
    }

    [Fact]
    public void IncidentSequenceAdvancesOnEveryDurableUpdateAndClear()
    {
        var incident = CreateIncident();
        var updated = incident.Update("Investigating", "New evidence", new string('b', 40),
            null, null, Now.AddMinutes(1));
        Assert.Equal(2, updated.Sequence);
        var cleared = updated.Clear(Now.AddMinutes(2));
        Assert.Equal(3, cleared.Sequence);
    }

    private static (Guid Lease, CodeRedIncident Incident, CodeRedRecoveryApprovalReceipt Receipt)
        CreateCodeRedTask(WorkTaskCoordinator coordinator)
    {
        coordinator.Submit(Request());
        var lease = coordinator.Claim("task-recovery", "agent-recovery",
            TimeSpan.FromMinutes(30), Now).Lease!.LeaseId;
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Validating, Now, "validate");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.CodeRed, Now, "required check failed");
        var incident = CreateIncident();
        var unsignedReceipt = new CodeRedRecoveryApprovalReceipt(1, "receipt-recovery-1", "trusted-host",
            "human-reviewer", "task-recovery", incident.IncidentId, 5, incident.Sequence,
            CodeRedRecoveryAction.ResumeInvestigation, Now, Now.AddMinutes(5), "pending-signature");
        var receipt = unsignedReceipt with
        {
            Signature = Convert.ToHexString(SHA256.HashData(unsignedReceipt.GetSigningPayload()))
        };
        return (lease, incident, receipt);
    }

    private static (Guid Lease, CodeRedIncident Incident, CodeRedRecoveryApprovalReceipt Receipt)
        CreateCodeRedTask(PersistentWorkTaskCoordinator coordinator)
    {
        coordinator.Submit(Request());
        var lease = coordinator.Claim("task-recovery", "agent-recovery",
            TimeSpan.FromMinutes(30), Now).Lease!.LeaseId;
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Investigating, Now, "inspect");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Implementing, Now, "implement");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.Validating, Now, "validate");
        coordinator.Transition("task-recovery", lease, WorkTaskStatus.CodeRed, Now, "required check failed");
        var incident = CreateIncident();
        var unsignedReceipt = new CodeRedRecoveryApprovalReceipt(1, "receipt-recovery-1", "trusted-host",
            "human-reviewer", "task-recovery", incident.IncidentId, 5, incident.Sequence,
            CodeRedRecoveryAction.ResumeInvestigation, Now, Now.AddMinutes(5), "pending-signature");
        var receipt = unsignedReceipt with
        {
            Signature = Convert.ToHexString(SHA256.HashData(unsignedReceipt.GetSigningPayload()))
        };
        return (lease, incident, receipt);
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

    private sealed class BlockingTestVerifier : ICodeRedRecoveryReceiptVerifier, IDisposable
    {
        public TaskCompletionSource VerificationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowVerification { get; } = new();

        public bool IsTrustedIssuer(string issuer) => issuer == "trusted-host";

        public bool VerifySignature(string issuer, ReadOnlyMemory<byte> signingPayload, string signature)
        {
            VerificationEntered.TrySetResult();
            if (!AllowVerification.Wait(TimeSpan.FromSeconds(10)))
                return false;
            return issuer == "trusted-host" &&
                signature == Convert.ToHexString(SHA256.HashData(signingPayload.Span)) &&
                !signingPayload.IsEmpty;
        }

        public void Dispose()
        {
            
            AllowVerification.Dispose();
        }
    }

    private sealed class TestVerifier(bool acceptSignature = true) : ICodeRedRecoveryReceiptVerifier
    {
        public bool IsTrustedIssuer(string issuer) => issuer == "trusted-host";
        public bool VerifySignature(string issuer, ReadOnlyMemory<byte> signingPayload, string signature) =>
            acceptSignature && issuer == "trusted-host" &&
            signature == Convert.ToHexString(SHA256.HashData(signingPayload.Span)) &&
            !signingPayload.IsEmpty;
    }
}
