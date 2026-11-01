<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 03 — Tasks, Leases, and Lifecycle Ownership

**Chapter type:** domain theory and implementation orientation  
**Evidence scope:** Foundry `development` task coordinator, persistence adapters, and lifecycle documentation. An architecture reconciliation is called out where the current implementation differs from the recorded host-approval decision.

## 01 — What: a task is owned work with a history

A workflow describes an ordered execution. A work task describes a bounded unit of responsibility that may span investigation, implementation, validation, repair, and handoff. The task has a stable identity, repository provenance, a declared scope, an attempt budget, and a lifecycle.

A task is not merely a status string. It is a history of accepted transitions made by an identified worker under a time-bounded lease. That history is what allows Foundry to reject stale workers, explain how the task reached its current state, and evaluate whether a proposed repair is within policy.

## 02 — Why: prevent stale ownership and unverifiable progress

Without explicit ownership, two agents can act on the same task, an expired agent can continue to submit transitions, or a status can be asserted without the evidence that should justify it.

A lease bounds who may advance the task. A fencing token distinguishes the current claim from every prior claim. A monotonic event sequence makes history auditable. Provenance binds the task to the repository, base commit, issue, and allowed paths. These controls do not stop an external process by force; they make stale or out-of-scope requests rejectable at the Foundry boundary.

## 03 — How: task identity, lease, and transition

### The task envelope

The version-1 `WorkTaskRequest` binds a task to:
- a stable task ID and correlation ID;
- canonical owner/repository identity;
- a GitHub issue number and exact HTTPS issue URL;
- a full 40-character base commit SHA;
- a non-empty repository-relative allowed-path scope;
- a bounded maximum attempt count.

The validator constrains lengths and rejects control characters, malformed repository identity, mismatched issue URLs, non-hexadecimal or abbreviated base SHAs, unsafe path segments, and attempt budgets outside the supported range. The issue is an untrusted source of work description. A valid envelope establishes validated provenance; it does not authorize repository mutation.

### Lifecycle states

The task coordinator validates transitions through an explicit state model. The principal path is:

```text
Queued -> Claimed -> Investigating -> Implementing -> Validating
                                      ^                    |
                                      |                    +-> ReviewReady -> HandedOff
                                      +-- RepairRequired <-+
```

Other states make exceptional outcomes explicit: `Blocked`, `CodeRed`, `Stopped`, and `Failed`. Not every state can transition to every other state. For example, CODE RED recovery must not jump directly into implementation or validation; the task must return through investigation. Exhausting the configured validation-attempt budget blocks further repair rather than allowing an unbounded loop.

`ReviewReady` and `RepairRequired` are not ordinary status assertions. They are outcomes of an evidence-based repair decision. A caller cannot obtain `ReviewReady` by submitting a transition reason that says checks passed.

### Claims, expiry, and fencing

A claim associates the task with an agent identity, a unique lease ID, and an expiry time. The coordinator rejects a second live claim. When a lease expires, a new agent may claim the task and receives a new lease ID.

Every transition must present the current lease ID and occur before the lease expires. Once a replacement claim is issued, the previous lease ID is stale and cannot advance the task. The lease ID is the fencing identity: expiry alone is not enough, because a delayed request from a previous worker could arrive after a new worker has taken ownership.

Lease arbitration is atomic only inside one `WorkTaskCoordinator` instance. It is not a cross-process lock or distributed lease service.

### Events and audit sequence

Each accepted transition appends an event with:
- monotonically increasing sequence number;
- previous and current status;
- agent and lease identity;
- timestamp and bounded reason;
- exact CI run/attempt and evidence digest when a repair decision supplies them.

The task snapshot exposes an immutable event collection. The event sequence is not decorative logging: it is part of the state history and is validated during restore. The journal rejects discontinuous sequences, illegal lifecycle edges, inconsistent attempt counts, invalid timestamps, and mismatched latest lease identity.

### Exact evidence for repair decisions

The repair path binds a decision to the task and repository identity, isolated branch, workflow path, candidate SHA, CI run ID and attempt, evidence digest, and bounded attempt history. Incomplete evidence blocks. A successful exact result can move the task to review. Actionable failures may permit another attempt within the configured limits. Repeated candidate SHAs, repeated diagnostic fingerprints, non-actionable failures, or exhausted budgets stop the loop rather than encouraging unbounded retries.

The persistent coordinator requires the current attempt to equal the latest durable attempt-journal record. A caller cannot substitute a fabricated history. The attempt journal and task snapshot journal are separate files, however; a crash between writes can leave an orphan attempt record that must be reconciled. The two files are not one atomic transaction.

## 04 — See it in a minute: follow the implementation

1. [WorkTaskCoordinator.cs](../../src/Foundry.Workflow/WorkTaskCoordinator.cs) — request validation, claims, leases, transitions, and repair decisions.
2. [PersistentWorkTaskCoordinator.cs](../../src/Foundry.Workflow/PersistentWorkTaskCoordinator.cs) — couples lifecycle mutations to durable snapshot writes.
3. [JsonlWorkTaskStore.cs](../../src/Foundry.Workflow/JsonlWorkTaskStore.cs) — append-only snapshot journal and replay validation.
4. [Work Task Lifecycle contract](../WORK_TASK_LIFECYCLE.md) — complete lifecycle, capability, CODE RED, evidence, and limitation details.
5. [Task coordinator tests](../../tests/Foundry.Workflow.Tests/WorkTaskCoordinatorTests.cs) and [persistent coordinator tests](../../tests/Foundry.Workflow.Tests/PersistentWorkTaskCoordinatorTests.cs) — executable contract evidence.

### Local persistence and recovery

`JsonlWorkTaskStore` appends complete task snapshots as JSON Lines and flushes each record to disk. On construction, it replays the journal and validates continuity. `PersistentWorkTaskCoordinator` restores the latest snapshot for each task before accepting new work, then persists each accepted mutation before returning it.

This gives restart recovery on the same host when the journal is intact. It does not give distributed consensus, cross-process locking, GitHub-backed state, or atomic transactions across separate journals. If persistence fails after an in-memory mutation, the persistent coordinator fails closed for the remainder of that process; restart and restore from the durable journal before dispatch resumes.

## 05 — Evidence, authority, and practice

### Architecture reconciliation: CODE RED approval ownership

The current development implementation includes a signed, single-use CODE RED recovery receipt verified by the Foundry task lifecycle. The recorded architecture decision for this workstream was different: **human-approval enforcement remains in the host adapter**, rather than becoming a verifiable host-issued receipt inside Foundry's recovery path.

Those are not interchangeable designs. The current source and tests describe what is implemented; the recorded decision describes the intended ownership boundary. This chapter does not silently resolve the discrepancy or claim that the current receipt mechanism is the approved target. The runtime and contract should be reconciled in a dedicated, reviewed change before the domain is described as aligned.

In either design, the host is responsible for authenticating the human and applying its approval policy. A model response, issue comment, or task transition reason is not human authorization.

### Deliberate limits

The current task lifecycle and persistence components do not:
- claim GitHub issues or create repository branches;
- execute model turns or dispatch CI themselves;
- provide distributed leases or cross-process atomicity;
- make the task and attempt journals a single transaction;
- forcibly terminate an agent that continues running after lease expiry;
- authorize merge, release, deployment, package publication, or arbitrary shell execution.

Host-issued capabilities are a separate boundary from task state. A task lease identifies who currently owns the task; a capability identifies which specific action the host has granted. Neither should be confused with the other.

### Questions for the reader

1. Why is a stable task ID not enough to establish current ownership?
2. What race does a fencing token prevent after a lease expires and a new worker claims the task?
3. Why should the issue URL and base SHA be validated as part of the task envelope?
4. Why must `ReviewReady` be derived from exact CI evidence instead of an ordinary transition?
5. What failure can occur between appending a repair attempt and persisting the resulting task snapshot?
6. Which parts of a lease guarantee would need redesign for multiple Foundry processes?

### Exercise

Trace a task through claim, investigation, implementation, validation, and a repair-required outcome. Then let the lease expire and have a second agent claim it.

For each event, record the event sequence, agent identity, lease ID, timestamp, and expected acceptance or rejection. Attempt one transition using the first agent's old lease after the second claim. Explain why it must fail even if the first agent has not stopped executing locally.

### Further reading

- [Governed work task lifecycle](../WORK_TASK_LIFECYCLE.md)
- [Chapter 01 — Work as an explicitly coordinated process](01-work-as-coordinated-process.md)
- [Chapter 02 — Workflow definitions and execution semantics](02-workflow-definitions-and-execution.md)
- [Foundry theory learning path](README.md)
- [Foundry roadmap and readiness](../ROADMAP.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
