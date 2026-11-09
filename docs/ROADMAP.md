<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Roadmap and Readiness

**Readiness:** Foundry has multiple implemented and deterministically tested slices, but the complete autonomous issue-to-draft-PR loop is not yet proven. No percentage is asserted. Status below describes the active repository-operation feature branch; it must not be mistaken for merged `development` or a released package.

## Completed implementation slices

### Workflow execution
- [x] Typed version-1 workflow definitions and full preflight validation.
- [x] Host-owned handler registry and one sequential `WorkflowRunner`.
- [x] Structured step/workflow outcomes, failure handling, cancellation, and skipped-step semantics.
- [x] Retained in-process lifecycle through `WorkflowExecutionCoordinator`.
- [x] Bounded JSON run context with clone, duplicate-write, entry-count, value-size, aggregate-size, and depth checks.
- [x] Cooperative workflow timeout with a distinct `TimedOut` result and cancellation precedence tests on its feature branch.

### Governed work and authorization
- [x] Versioned task envelope bound to repository, issue, base SHA, path scope, and bounded attempt count.
- [x] Task lifecycle with sequenced events, expiring leases, fencing, attempt enforcement, and guarded terminal transitions.
- [x] Local persistent task journal with validated replay and fail-closed restore.
- [x] CODE RED incident lifecycle, status projections/notices, mutation suspension, and signed single-use recovery approval receipts bound to the current task/incident sequence.
- [x] Host-issued short-lived capabilities scoped to task, lease, branch, candidate SHA, paths, and explicit actions.
- [x] Deterministic exact-CI repair decision policy and local repair-attempt journal.
- [x] HeadlessAi lease-gated turn bridge, provenance checks, durable at-most-once journal, and source-pinned host adapter/console composition.

### Repository and CI host boundary — active PR #26
- [x] Closed typed repository-operation set and workflow-handler integration.
- [x] Revalidation of task state, lease, capability, path, action, branch, and candidate SHA before adapter side effects.
- [x] Local durable operation audit with flushed pending intent and terminal result; indeterminate operations require reconciliation.
- [x] GitHub host adapter for isolated candidate branch creation, immutable-SHA reads, staged blob writes, explicit commits, and non-force candidate-ref advancement.
- [x] Local staging store and dispatch journal with replay validation and cooperating-process file locks.
- [x] Host-allowlisted GitHub Actions workflow dispatch with trusted `development` workflow source pinned independently from candidate branch/SHA.
- [x] Durable dispatch intent, restart reconciliation without blind resend, request-ID run correlation, exact run-attempt metadata re-fetch, and required-job verification.
- [x] Trusted workflow candidate-checkout provenance artifact emitted only after exact detached checkout verification.
- [ ] Exercise the workflow_dispatch path through the authorized host adapter and validate the emitted artifact from the host.

## Remaining integration milestones

### I1 — Live GitHub boundary
- [ ] Provision a production GitHub App/token provider with least-privilege installation permissions.
- [ ] Verify secure credential refresh/re-authentication and failure pause/resume.
- [ ] Run the host adapter against GitHub in an explicitly authorized test repository and verify branch/ref/commit/Actions behavior.
- [ ] Prove live workflow_dispatch correlation and exact run attempt.
- [ ] Retrieve candidate-checkout provenance artifact with bounded download, digest/size/schema checks, and exact receipt/run binding.

### I2 — Exact evidence to task decision
- [ ] Connect CiAi's trusted exact-run verdict/evidence packet through the host adapter.
- [ ] Convert verified candidate and CI provenance into a durable `WorkTaskRepairAttempt`.
- [ ] Prove duplicate, stale, wrong-repository, wrong-candidate, wrong-run, wrong-attempt, missing-job, skipped-job, and incomplete evidence fail closed.
- [ ] Advance to `RepairRequired` or `ReviewReady` only through the deterministic decision policy.

### I3 — Human-reviewed handoff
- [ ] Implement capability-gated draft-PR creation against the exact verified candidate SHA.
- [ ] Persist PR identity and evidence links in task history.
- [ ] Present review-ready state and handoff to the human; never auto-merge.
- [ ] Prove recovery after process restart and ambiguous PR creation without duplicates.

### I4 — First end-to-end pilot
- [ ] Complete the bounded red → investigation → repair → exact CI → evidence → draft PR flow with deterministic fake-provider/fake-GitHub tests.
- [ ] Run the first live pilot in MyVR under host-approved repository scope.
- [ ] Validate cancellation, exhausted budgets, CODE RED escalation/recovery, re-authentication, and restart recovery.
- [ ] Only then consider multi-agent fan-out and cross-repository orchestration.

## Cross-cutting requirements

- Preserve the boundary between workflow-run lifecycle and governed-task lifecycle.
- Keep `WorkflowRunner` as the only sequential executor.
- Keep FSM_API responsible for FSM mechanics; do not add a parallel mutable transition graph.
- Keep FSM_COS composition separate from Foundry execution.
- Keep HeadlessAi invocation separate from task lifecycle and repository authority.
- Keep CiAi policy separate from host credential/permission management.
- Preserve every attempt and evidence reference; bound retries, time, concurrency, path scope, and provider budgets.
- Fail closed on ambiguity, stale leases, corrupted journals, missing credentials, incomplete evidence, or indeterminate side effects.
- Never merge, publish, deploy, delete branches, or release packages without explicit owner authorization.

## Evidence and branch hygiene

PR #26 latest head `3ebf6e0b097e208255190ba496dac04959802260` passed exact-head push CI run [#42467586819](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/42467586819). The run completed successfully and exercised the ordinary push build/test path, including the workflow tests, builds, separate-process FSM_API probe, repeated Fox/Sheepdog execution/comparison, and evidence upload. Because the event was `push`, the `workflow_dispatch`-only trusted-source/candidate-checkout/provenance path was skipped. This run therefore does not prove live dispatch, candidate artifact retrieval, or host-side artifact validation.

PR #26 remains open and draft. Its recorded base SHA is `8bd3487a96af2cd7d667f62c4566a499bafe30dc`, while the current `development` branch tip is `dc1324f9bfcc61e6de32f7c52d7686025976f00b`. The PR is reported as not mergeable/dirty and needs non-destructive branch reconciliation before review can proceed. No canonical branch was modified as part of this documentation work.

A green CI run is evidence, not permission to merge, release, deploy, or publish.

---

<!-- The Singularity Workshop footer -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
