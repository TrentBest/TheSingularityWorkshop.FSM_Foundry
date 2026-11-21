<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# WorkTaskCoordinator UML

**View type:** source-grounded class neighborhood and lifecycle sequence.  
**Source:** `src/Foundry.Workflow/WorkTaskCoordinator.cs`, `PersistentWorkTaskCoordinator.cs`.  
**Scope:** deterministic in-process task state authority; persistence and host trust are shown as separate collaborators.

## Class neighborhood

```mermaid
classDiagram
    class WorkTaskRequest {
      +int SchemaVersion
      +string TaskId
      +string CorrelationId
      +string Repository
      +int IssueNumber
      +string BaseSha
      +IReadOnlyList AllowedPaths
      +int MaximumAttempts
      +Validate()
    }
    class WorkTaskCoordinator {
      -Dictionary tasks
      -ICodeRedRecoveryReceiptVerifier recoveryReceiptVerifier
      +Submit(request)
      +Claim(taskId, agentId, leaseDuration, now)
      +Transition(taskId, leaseId, next, now, reason)
      +ApplyRepairDecision(...)
      +RecoverFromCodeRed(...)
      +Restore(snapshot)
      +GetSnapshot(taskId)
    }
    class WorkTaskSnapshot
    class WorkTaskLease
    class WorkTaskEvent
    class WorkTaskRepairDecisionPolicy
    class ICodeRedRecoveryReceiptVerifier
    class CodeRedIncident
    class CodeRedRecoveryApprovalReceipt
    class PersistentWorkTaskCoordinator
    class IWorkTaskStore
    class IWorkTaskRepairAttemptJournal
    WorkTaskCoordinator ..> WorkTaskRequest : validates
    WorkTaskCoordinator ..> WorkTaskSnapshot : returns/restores
    WorkTaskCoordinator ..> WorkTaskLease : creates/checks
    WorkTaskCoordinator ..> WorkTaskEvent : appends
    WorkTaskCoordinator ..> WorkTaskRepairDecisionPolicy : evaluates exact CI attempt
    WorkTaskCoordinator ..> ICodeRedRecoveryReceiptVerifier : verifies trust/signature
    WorkTaskCoordinator ..> CodeRedIncident : binds recovery
    WorkTaskCoordinator ..> CodeRedRecoveryApprovalReceipt : consumes once
    PersistentWorkTaskCoordinator *-- WorkTaskCoordinator : composes
    PersistentWorkTaskCoordinator ..> IWorkTaskStore : restores/appends snapshots
    PersistentWorkTaskCoordinator ..> IWorkTaskRepairAttemptJournal : supplies exact history
```

## Evidence-derived validation sequence

```mermaid
sequenceDiagram
    participant Host
    participant Persistent as PersistentWorkTaskCoordinator
    participant Journal as RepairAttemptJournal
    participant Coordinator as WorkTaskCoordinator
    participant Policy as WorkTaskRepairDecisionPolicy
    Host->>Persistent: ApplyRepairDecision(task, lease, currentAttempt, expectation, ...)
    Persistent->>Journal: ExecuteWithAttempts(taskId, callback)
    Journal->>Persistent: exact durable attempt history
    Persistent->>Persistent: verify currentAttempt is latest journal record
    Persistent->>Coordinator: ApplyRepairDecision(previous, current, expectation, ...)
    Coordinator->>Coordinator: verify lease, task, attempt and expectation identity
    Coordinator->>Policy: Evaluate(history, current, expectation, maxAttempts, maxDuration, now)
    Policy-->>Coordinator: bounded decision + reason
    Coordinator->>Coordinator: validate transition and append event
    Coordinator-->>Persistent: immutable task snapshot
    Persistent->>Persistent: append snapshot; fail closed if persistence fails
    Persistent-->>Host: persisted snapshot
```

## CODE RED recovery sequence

```mermaid
sequenceDiagram
    participant Host as Authenticated host
    participant Coordinator as WorkTaskCoordinator
    participant Verifier as Receipt verifier
    Host->>Coordinator: RecoverFromCodeRed(task, lease, incident, receipt, now, reason)
    Coordinator->>Coordinator: require CodeRed status and active lease
    Coordinator->>Coordinator: bind incident to task, repository, correlation and agent
    Coordinator->>Coordinator: bind receipt to incident/task event sequence and action
    Coordinator->>Verifier: trusted issuer + signature verification
    Verifier-->>Coordinator: trusted / rejected
    Coordinator->>Coordinator: reject consumed receipt; transition to Investigating
    Coordinator-->>Host: snapshot with approval provenance event
```

## Interpretation and limits

- The coordinator's lock serializes operations within one process. It does not establish cross-process or distributed lease exclusivity.
- The persistent wrapper serializes local operations and journals snapshots; a failed append makes that wrapper fail-closed. This is not a distributed transaction.
- The host remains responsible for authenticating the human and issuing a receipt. The verifier only establishes that the receipt's issuer and signature are trusted.
- A CI attempt cannot be converted to `ReviewReady` by setting a status. It must pass the evidence policy with task/run/candidate provenance.
- This view describes implemented types and call paths, not the still-incomplete CODE RED-to-draft-PR product loop.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
