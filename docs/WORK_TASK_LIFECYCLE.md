<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Governed Work Task Lifecycle

## Scope and status

The task coordinator and CODE RED incident value contract are in-process reference components. CODE RED incident identifiers and text are bounded, control characters are rejected, and branch validation excludes canonical branches plus malformed or Git-ref-invalid path components before data reaches the local journal. `JsonlWorkTaskStore` adds an append-only, fsync-backed local journal of complete task snapshots with strict replay validation. `PersistentWorkTaskCoordinator` restores journaled tasks at startup and persists each accepted submit/claim/transition before returning it to the caller. Replay validates event continuity, legal lifecycle edges, attempt counts, timestamps, and the latest lease identity; restore uses the same validator. `WorkTaskCoordinator.Restore` rehydrates validated snapshots while preserving event sequence and lease identity so an expired lease can be reclaimed with a new fencing token. This is durable across process restarts on the same host, but is not a distributed store, GitHub-backed store, or connected live GitHub/HeadlessAi/CiAi integration.

## Task envelope

`WorkTaskRequest` is schema-versioned and binds a task to:
- a stable task ID and correlation ID;
- an owner/repository identity and GitHub issue reference;
- the full 40-character base commit SHA;
- a non-empty, repository-relative allowed-path scope;
- a bounded attempt count.

The current envelope validator also bounds task and correlation IDs to 128 characters, repository identity to 256 characters, issue URLs to 512 characters, and path scopes to at most 128 entries of at most 512 characters each. Task and correlation IDs reject control characters. Lease agent IDs are limited to 128 control-free characters; transition reasons are limited to 2,048 control-free characters. These limits keep caller-controlled metadata bounded before it reaches lifecycle snapshots or durable journals.

The issue is untrusted input. A valid task envelope is provenance and validation, not authorization to mutate a repository.

## Lifecycle

`WorkTaskCoordinator` currently validates these transitions:

```text
Queued -> Claimed -> Investigating -> Implementing -> Validating
                                      ^                    |
                                      |                    +-> ReviewReady -> HandedOff
                                      +-- RepairRequired <-+
```

`Blocked`, `CodeRed`, `Stopped`, and `Failed` are explicit states with constrained recovery/terminal transitions. An exhausted attempt budget converts a requested repair decision into `Blocked`, and a later CODE RED recovery cannot bypass the validation-attempt ceiling. Recovery from `CodeRed` must return through `Investigating`; direct jumps to `Implementing` or `Validating` are rejected. The host authenticates the human and obtains explicit approval. Recovery then requires a short-lived, host-issued signed receipt bound to the task ID, active incident ID and sequence, current task-event sequence, recovery action, issuer, principal, and validity window. Foundry verifies the issuer against its configured host trust policy and verifies the signature before atomically consuming the receipt in the `CodeRed` → `Investigating` event. Ordinary `Transition` calls cannot perform this recovery. Receipt IDs cannot be reused, and the event journal records the receipt ID, issuer, principal, incident sequence, and task sequence. The complete signed receipt is retained in the append-only task event so its signature can be independently checked against the host's trusted public key. Foundry does not implement accounts, OAuth/JWT authentication, or human identity management; the host remains responsible for authenticating the person and sourcing the current active incident snapshot. `ReviewReady` and `RepairRequired` are not ordinary status transitions: `ApplyRepairDecision` re-evaluates the exact candidate/workflow/run evidence against the task identity, contiguous attempt history, and configured attempt/time budgets before applying the resulting state. A caller cannot assert `ReviewReady` by supplying a transition reason such as “checks passed.”

## Claims and audit

Claims have an agent identity, unique lease fencing token, and bounded expiry. A live lease prevents a second claim. After expiry, another agent can claim; the prior lease token is rejected for all subsequent transitions. Every accepted transition appends a monotonically sequenced event containing prior/current status, agent, lease ID, timestamp, and reason. Evidence-derived decisions additionally retain the exact CI run ID, run attempt, and evidence-payload SHA-256 digest in that event. Journal replay requires these provenance fields and the corresponding policy decision reason for `ReviewReady` and `RepairRequired`; a plain lifecycle event cannot recreate either state. Snapshots expose immutable event collections.


## Host-issued action capabilities

`WorkTaskCapabilityRegistry` issues opaque, short-lived capabilities only when the host explicitly calls `Grant`. Each grant requires a live task lease and is bound to the task/repository, owning agent and fencing token, isolated branch, base SHA, exact candidate SHA, a subset of the task's allowed paths, and an explicit action allowlist. File reads/writes require a path inside that scope; commit/CI/branch/draft-PR operations require their own named action. Grants expire at the earlier of their configured lifetime (maximum one hour) or the owning agent lease expiry, and can be revoked immediately.

The registry is an in-process host boundary, not a security sandbox. The host must keep the registry and capability IDs out of model context, pass only the minimum capability to the operation adapter, and reissue after restart only after revalidating task and policy. Merge, release, deployment, package publication, and arbitrary shell/process execution are not grantable actions.

**CODE RED is a mutation-suspension boundary.** While a task is in `CodeRed`, the registry permits only explicitly granted `ReadRepository` access; file writes, branch/commit changes, CI requests, and draft-PR creation are denied. Entering `CodeRed` invalidates every capability issued before that transition, including after the task later resumes. Recovery must first transition through `Investigating`; the host must obtain any required human approval before making that transition. The lifecycle core verifies a host-issued signed receipt at the recovery boundary; the host remains responsible for human authentication and approval policy. A host-approved recovery must issue a fresh capability after the task has left `CodeRed`. Read-only capabilities may be issued during CODE RED for investigation, but cannot authorize mutation.

## CODE RED incident record

`CodeRedIncident` is a versioned, credential-free operational record containing repository/task/issue/correlation identity, agent and role, current action, isolated branch and exact candidate SHA, latest CI run and attempt, severity/reason, retry budget, timestamps, and human-escalation requirement. `JsonlCodeRedIncidentStore` persists every create/update/clear snapshot in an append-only, fsync-backed journal, validates immutable incident identity and monotonic history on replay, and rejects mutation after clear.

Updates preserve the incident start time, increment a monotonic incident sequence, and reject backward timestamps. Clearing is explicit, increments that sequence, records a clear timestamp, and clears the human-escalation requirement. CODE RED recovery receipts bind to the exact active incident sequence as well as the current task-event sequence. A cleared incident cannot be updated again. Canonical branches and partial CI run/attempt identities are rejected.

This is the incident data contract with a local append-only journal and a host notification boundary, not a GitHub-backed or distributed incident service. No credential/token field exists in the record.

`PersistentCodeRedRecoveryCoordinator` is the safe local composition for recovery: it reads the latest active incident from `JsonlCodeRedIncidentStore` and holds that store's in-process gate while the persistent task coordinator verifies and durably appends the recovery event. Local incident update/clear operations therefore cannot advance the incident sequence between snapshot selection and receipt consumption. The host must authenticate the approver and issue the signed receipt before calling this coordinator. This serialization is limited to one store instance in one process; it does not provide cross-process locking or atomicity across the incident and task journals.



## Exact CI repair-attempt history and bounded decisions

`WorkTaskRepairAttempt` is Foundry's compact audit projection for one CI run attempt: task/correlation identity, repository, isolated branch, workflow path, candidate SHA, run ID and attempt, packet digest, evidence completeness/success, actionable-diagnostic fingerprint, and timestamps. It does not replace CiAi's evidence packet or required-workflow policy.

`JsonlWorkTaskRepairAttemptJournal` preserves every attempt in order, treats an identical redelivery as a duplicate, rejects conflicting/out-of-order callbacks, rejects reuse of the same run attempt, and prevents new attempts after a successful result. A torn final journal record fails closed. Persistent evidence decisions require this journal to be injected and accept only a current attempt that exactly equals the latest durable record; prior attempts are loaded from the journal, never supplied by the caller. The journal holds its in-process append lock across history validation, lifecycle evaluation, and task-snapshot persistence, so a concurrent append cannot change the evidence history during a decision. A missing journal, absent record, or altered current attempt is rejected before the task lifecycle can change. The task snapshot journal and attempt journal remain separate files, so a crash between the attempt append and task-snapshot append can leave an orphan attempt record; recovery must reconcile that record rather than treating the two files as one atomic transaction.

`WorkTaskRepairDecisionPolicy` requires an explicit expected task/repository/branch/workflow/candidate-SHA tuple and can additionally bind the expected CI run ID/attempt returned by a rerun dispatcher. It returns only four deterministic dispositions: `ReviewReady`, `RepairRequired`, `Blocked`, or `CodeRed`. Incomplete evidence blocks; successful exact evidence moves to review; actionable failures can retry only within attempt/time budgets; unchanged candidate SHAs, repeated diagnostic fingerprints, or non-actionable failures stop the loop and raise CODE RED. No CI run is dispatched and no agent is invoked by this policy.

## CODE RED status notices

`CodeRedStatusNoticeFactory` creates credential-free active and cleared status messages; `ICodeRedStatusPublisher` and `CodeRedStatusNotifier` provide the host-owned delivery boundary. An active notice is refused unless the incident matches a currently leased task in `CodeRed` state, the agent identity matches, and a next-update time before the lease expiry is supplied. The rendered notice states whether human escalation is required. The message includes repository, issue, candidate SHA, CI run/attempt when known, current action, blocker, and next update. A cleared notice requires an explicit incident clear and does not claim the agent is still engaged.

The notifier propagates delivery failures and does not mark an incident cleared on a failed publish. Actual GitHub/chat transport, destination policy, and delivery idempotency remain host adapter responsibilities.

## Current limitations

- `JsonlWorkTaskStore` and `PersistentWorkTaskCoordinator` persist task snapshots and event history locally. The adapter serializes its own operations and restores journal state before accepting new work. If a journal write fails after an in-memory mutation, it permanently fails closed for that process; restart and restore from the journal before dispatch resumes. This remains single-process only: separate adapter/store instances or processes are not coordinated, and this is not a distributed atomic claim.
- CODE RED incident records have their own append-only local journal; this is single-process storage and is not yet a GitHub-backed or distributed incident service.
- The task coordinator itself does not claim GitHub issues, create branches, inspect CI, call models, or create PRs. Those responsibilities remain host-adapter concerns; the active repository-operation branch separately implements a typed GitHub adapter for candidate branch/read/stage/commit and allowlisted Actions dispatch. Live issue selection/claim and draft-PR creation remain unimplemented.
- Lease arbitration is atomic only within one coordinator instance; it is not a cross-process lock. The local journal does not turn that lease into a distributed claim.
- External CODE RED message transport, secure re-authentication, and automatic status-clear publication remain unimplemented; the validated active/cleared notice projection and publisher interface are implemented.
- The HeadlessAi turn bridge, durable at-most-once turn journal, concrete source host adapter, CiAi dispatch-intent/reconciliation core, GitHub Actions dispatch/correlation/required-job verification, and candidate repository-operation adapter are implemented on feature branches and are documented separately. Their existence does not prove live provider/GitHub/CiAi transport. Remaining integration work includes production credential provisioning and re-authentication, live callback/notice transport, authenticated artifact retrieval, conversion of verified run evidence into a durable repair attempt, cross-journal atomicity, complete bounded repair-cycle orchestration, and draft-PR handoff. The persistent repair-decision path binds decisions to the latest exact attempt in the injected local attempt journal, but the attempt and task journals are not cross-file atomic. Local journals remain local stores, not distributed task/lease services.

## Verification

See [WorkTaskCoordinatorTests](../tests/Foundry.Workflow.Tests/WorkTaskCoordinatorTests.cs), [JsonlWorkTaskStoreTests](../tests/Foundry.Workflow.Tests/JsonlWorkTaskStoreTests.cs) (including persistent-adapter restart and fail-closed tests), [CodeRedIncidentTests](../tests/Foundry.Workflow.Tests/CodeRedIncidentTests.cs), and [JsonlCodeRedIncidentStoreTests](../tests/Foundry.Workflow.Tests/JsonlCodeRedIncidentStoreTests.cs), [WorkTaskCapabilityRegistryTests](../tests/Foundry.Workflow.Tests/WorkTaskCapabilityRegistryTests.cs), [CodeRedStatusNoticeTests](../tests/Foundry.Workflow.Tests/CodeRedStatusNoticeTests.cs), [CodeRedStatusNotifierTests](../tests/Foundry.Workflow.Tests/CodeRedStatusNotifierTests.cs), [JsonlWorkTaskRepairAttemptJournalTests](../tests/Foundry.Workflow.Tests/JsonlWorkTaskRepairAttemptJournalTests.cs), and [WorkTaskRepairDecisionPolicyTests](../tests/Foundry.Workflow.Tests/WorkTaskRepairDecisionPolicyTests.cs) for lifecycle, fencing, retry, incident provenance, CI identity, timestamp, clear behavior, and capability scope/lease/expiry/revocation. CI status is linked from the pull request implementing these slices.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
