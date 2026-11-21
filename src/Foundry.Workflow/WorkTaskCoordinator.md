<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# WorkTaskCoordinator

## Purpose

`WorkTaskCoordinator` is the deterministic in-process authority for a governed repository-work task's lifecycle. It validates the task envelope, issues and checks agent leases, serializes state transitions, records an ordered event history, enforces bounded validation attempts, applies evidence-derived repair decisions, and gates CODE RED recovery on a trusted single-use approval receipt.

It is not a GitHub issue poller, distributed scheduler, agent executor, or credential provider. The caller/host supplies the task request, agent identity, timestamps, CI attempt evidence, incident, and approval receipt. Repository transport and durable storage are separate host/persistence boundaries.

## Public contract

- `Submit(WorkTaskRequest)` validates schema version, task/correlation identity, canonical owner/repository identity, exact GitHub issue URL, full base SHA, repository-relative allowed paths, and a bounded maximum-attempt count. Duplicate task IDs are rejected.
- `Claim(taskId, agentId, leaseDuration, now)` creates a lease of at most one hour. An unexpired lease conflicts; an expired lease may be replaced. A non-queued task without a lease requires recovery rather than an ordinary reclaim.
- `Transition(...)` requires the current unexpired lease and an allowed transition. Validation attempts are counted at entry to `Validating`. Callers cannot assert `ReviewReady` or `RepairRequired` through this generic method.
- `ApplyRepairDecision(...)` re-evaluates the exact current attempt against the task identity, evidence expectation, prior attempt history, configured attempt budget, and maximum duration. It alone derives `ReviewReady`, `RepairRequired`, `Blocked`, or `CodeRed` from that evidence.
- `RecoverFromCodeRed(...)` requires the task to be in CODE RED, the current lease, a matching active incident, a time- and sequence-bound receipt, a trusted issuer, and a valid signature. Receipt consumption and the transition back to `Investigating` occur under the task lock.
- `Restore(snapshot)` validates a persisted snapshot before rehydrating it; `GetSnapshot(taskId)` returns the current immutable snapshot.

## State and audit model

The coordinator stores each task under a lock. A snapshot contains the validated request, current status, optional lease, attempt count, and ordered events. Each event records sequence, previous/current status, agent and lease identity, timestamp, reason, and optional exact CI run/evidence or CODE RED approval provenance.

Terminal handoff, stop, and failure release the lease. Blocking also releases the lease. A normal transition cannot bypass evidence policy to claim review readiness. The configured attempt budget is bounded to 1–10; exhausted validation/repair work is blocked rather than retried indefinitely.

## Persistence boundary

`PersistentWorkTaskCoordinator` composes this in-process state machine with `IWorkTaskStore` and an optional durable repair-attempt journal. It restores snapshots at construction and serializes operations under its own lock. Persistent repair decisions use the append-only journal's exact history; caller-supplied prior history is not accepted on that path.

If a task-state journal write fails after the in-memory transition, the persistent coordinator records the failure and becomes fail-closed. Further operations are rejected until restart and restore. This is local durable persistence, not a distributed lease or multi-host concurrency guarantee.

## Authority boundaries

- The host authenticates the human and issues the CODE RED approval receipt; the coordinator verifies trust, signature, binding, expiry, and replay.
- CI evidence is data, not a caller-selected status. The coordinator re-evaluates its provenance and policy before advancing lifecycle state.
- A lease is a concurrency/lifecycle token, not a GitHub credential or repository capability.
- Repository writes and CI dispatch are performed by separately authorized host adapters.

## Verification

See [WorkTaskCoordinatorTests.cs](../../tests/Foundry.Workflow.Tests/WorkTaskCoordinatorTests.cs) for task validation, lease conflict/expiry, audited transitions, retry bounds, CODE RED recovery, exact CI evidence, and terminal lease release. Persistence behavior is covered by the persistent coordinator/store tests.

The companion neighborhood view is [WorkTaskCoordinator UML](../../docs/UML/WorkTaskCoordinatorUML.md). The broader separation between workflow-run lifecycle and governed task lifecycle is documented in [Work Task Lifecycle](../../docs/WORK_TASK_LIFECYCLE.md).

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
