<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Domain

**Scope:** This document describes the implementation on the current repository-operation feature branch. A capability marked *implemented here* is not thereby part of `development` or a released package. The [README](../README.md) and [coverage ledger](../DOCUMENTATION_INDEX.md) distinguish branch and verification status.

## 1. What Foundry is

Foundry is the Workshop's **execution and governed-work control plane**. It turns explicit, bounded requests into validated execution, coordinates the lifecycle that owns those requests, enforces host-issued authority at the point of side effect, preserves provenance, and reports outcomes that another system can review.

Foundry is not an AI model, a CI policy engine, a composition engine, a UI, a GitHub credential provider, or an unrestricted automation runtime. It is the domain boundary that coordinates those systems without taking over their responsibilities.

The central invariant is:

> **Intent describes requested work; Foundry validates and coordinates its lifecycle; the host grants narrowly scoped authority; adapters perform external effects; durable evidence records what actually happened.**

Neither a workflow definition, task envelope, issue body, model response, status label, nor successful CI run grants permission by itself.

## 2. The two lifecycle domains

Foundry currently contains two deliberately separate lifecycle models.

### A. Workflow execution

The workflow domain expresses a finite, ordered sequence of registered operations.

| Concept | Responsibility |
|---|---|
| `WorkflowDefinition` / `WorkflowStep` | Typed, versioned, in-memory description of ordered handler keys |
| `WorkflowValidator` | Validates the complete definition and handler availability before side effects |
| `WorkflowHandlerRegistry` | Host-owned allowlist of callable handlers |
| `WorkflowExecutionContext` | Bounded, run-scoped JSON input/output data; never an authority channel |
| `WorkflowRunner` | The one sequential step executor; maps handler outcomes to step/workflow results |
| `WorkflowExecutionCoordinator` | Accept/start-once/query/cancel/dispose lifecycle around the runner |

The workflow runner remains the only sequential executor. The coordinator does not become a scheduler, retry engine, durable queue, or second runner. This lifecycle is currently in-process and in-memory. Cancellation and timeout are cooperative; a handler that ignores its token cannot be forcibly terminated.

### B. Governed repository work

The governed-work domain represents a repository task with identity, provenance, an assigned agent lease, bounded attempts, authorized candidate scope, and a reviewable terminal handoff.

| Concept | Responsibility |
|---|---|
| `WorkTaskRequest` | Versioned task envelope bound to task/correlation IDs, canonical repository, exact issue URL/number, base SHA, allowed paths, and maximum attempts |
| `WorkTaskCoordinator` | Validates and coordinates task lifecycle, leases, transition history, attempt budget, recovery, and authoritative task snapshots |
| `WorkTaskStatus` / `WorkTaskEvent` | Lifecycle vocabulary and sequenced audit facts |
| `WorkTaskLease` | Agent identity, unique fencing lease ID, and expiry |
| `PersistentWorkTaskCoordinator` / `JsonlWorkTaskStore` | Local append-only persistence and replay; fail-closed validation and recovery |
| `WorkTaskCapabilityRegistry` | Host-issued short-lived authority bound to task, repository, agent/lease, branch, base/candidate SHA, paths, actions, and expiry |
| `WorkTaskRepairDecisionPolicy` | Deterministic bounded decision from exact CI-attempt evidence; derives repair-required/review-ready decisions rather than trusting agent claims |
| `WorkTaskRepairAttemptJournal` | Durable local record of repair-attempt provenance and evidence |
| `CodeRedIncident` and recovery contracts | Incident identity, status, notice projection, signed single-use host approval, and guarded recovery |

The task lifecycle is not the workflow lifecycle. A task may invoke a registered workflow handler, but the two status machines must not be merged. Task meaning, lease ownership, authorization, attempts, and audit remain in the governed-work domain; `WorkflowRunner` still executes sequential workflow steps.

The reference coordinators and JSONL stores provide deterministic local persistence and single-host coordination. They are not a distributed lease service, GitHub issue queue, or production multi-host database.

## 3. Governed-work lifecycle

The task lifecycle covers these states:

`Queued → Claimed → Investigating → Implementing → Validating`

Validation can lead to `RepairRequired`, `ReviewReady`, or a blocked/escalated outcome according to the exact evidence and bounded attempt policy. `CodeRed` suspends mutation and requires explicit host-approved recovery. `HandedOff`, `Stopped`, and `Failed` are terminal outcomes.

Important rules:

- A task request is validated before acceptance: canonical `owner/repository`, repository-matching HTTPS issue URL, full 40-character base SHA, safe repository-relative path scope, and bounded attempt count.
- Claims create expiring leases with unique lease IDs. Every transition and capability is fenced by the active lease.
- A stale, expired, or superseded lease cannot act. Reclaiming an expired non-queued task requires a defined recovery path rather than silently taking ownership.
- Attempt budgets are enforced by the coordinator. Repair readiness is not an agent-selected transition.
- Task history is sequenced and retained. Persistent restore replays and validates history rather than trusting a snapshot's status in isolation.
- CODE RED is operational urgency, never authority escalation. Mutation capabilities are suspended; recovery requires a trusted, signed, short-lived, single-use approval receipt bound to the active incident and exact task-event sequence.
- Model output is untrusted proposal material. It cannot issue or broaden capabilities, change repository scope, authorize recovery, or claim that external effects succeeded.

## 4. Authority and repository-operation domain

The repository-operation API is intentionally closed and typed. The current operation vocabulary is:

| Operation | Effect |
|---|---|
| Read repository file | Reads a bounded path at an immutable candidate SHA |
| Create candidate branch | Creates an isolated candidate ref at the authorized base SHA |
| Write files | Stages bounded content as immutable blobs; does not move the branch |
| Create commit | Creates an explicit commit and advances only the authorized candidate ref |
| Request CI run | Dispatches only a host-allowlisted workflow against the exact candidate SHA |

The optional draft-PR capability is represented in the capability vocabulary, but the complete draft-PR handoff is not yet implemented.

A capability is an expiring authorization grant, not a credential. It is bound to task, repository, agent and lease, candidate branch, base SHA, candidate SHA, allowed paths, allowed actions, task event sequence, and lifetime. The dispatcher re-reads task state and re-demands authority immediately before transport. CODE RED invalidates mutation authority. A subsequent candidate SHA requires a new capability.

The host adapter—not the model or workflow input—owns repository allowlists, credentials, path containment, branch namespace, GitHub API transport, commit ancestry checks, workflow allowlists, and external network policy. Foundry's typed boundary validates operation intent and provenance but does not claim to be a sandbox.

The local operation journal records a flushed `Pending` intent before transport and a terminal outcome after result validation. A crash after an external side effect but before the terminal record is indeterminate. It must be reconciled against the repository authority; it must never be blindly replayed.

## 5. Agent and CI coordination

Foundry coordinates external specialists without absorbing their domains.

### HeadlessAi

- HeadlessAi owns provider-neutral model invocation and exchange contracts.
- Foundry's `HeadlessAiTurnBridge` gates a turn on the task lease, maps immutable requests/results, validates result provenance, and journals turn reservation/outcome for at-most-once behavior.
- The concrete `Foundry.HeadlessAiHost` source adapter and manual console composition root exist on feature branches; compilation is not proof of a live provider call.
- Foundry retains task lifecycle, capabilities, authorization, repository operations, and retry decisions. HeadlessAi does not.

### CiAi

- CiAi owns deterministic CI policy, exact-run evidence semantics, and CI verdicts.
- Foundry records dispatch intent, reconciles request IDs to GitHub workflow runs, and verifies exact run-attempt identity and required jobs through its host adapter.
- A dispatch acknowledgment is not a CI result. A successful workflow run is not yet a durable Foundry repair decision until its candidate identity and evidence are ingested and validated.
- The versioned artifact callback/ready-notice contract is documented separately. A complete production HTTPS receiver and remote transport are not implied by the contract.

### GitHub

GitHub remains the durable authority for issues, refs, commits, workflow runs, artifacts, reviews, and pull requests. The current `Foundry.GitHubHost` implementation provides a typed, allowlisted GitHub REST adapter, local staging/audit journals, candidate branch/read/stage/commit operations, and allowlisted Actions dispatch/reconciliation. Fake-HTTP tests validate the adapter contract; they do not prove production credentials, live API behavior, distributed storage, or the complete issue-to-draft-PR loop.

## 6. Domain boundaries

| Neighbor | Owns | Foundry relationship |
|---|---|---|
| FSM_API | FSM definitions, handles, transitions, and runtime mechanics | Foundry may consume it for lifecycle state; it must not reimplement FSM mechanics |
| FSM_COS | MicroBundle composition and `RuntimeAssembly` | Composition is upstream/separate; Foundry executes work and must not recompose an assembly |
| HeadlessAi | Model/provider invocation and exchange contracts | Invoked through a narrow adapter; no lifecycle or authorization transfer |
| CiAi | CI policy, verdicts, exact-run evidence | Foundry requests/reconciles runs and consumes validated evidence; it does not invent CI truth |
| GitHub | Repository and review authority | Host adapter performs allowlisted operations with scoped credentials |
| GUI | Presentation and explicit user intent | May display snapshots and issue commands; never owns authoritative state |
| Hosts | Credentials, transport, environment, user authentication, capabilities | Compose trusted adapters and decide which operations are available |
| The / Hub | Human-facing coordination and experience | Selects intent and presents status; Foundry supplies execution/control contracts |

`WorkflowDefinition`, FSM_COS `RuntimeManifest`/`RuntimeAssembly`, and legacy WebPage `WorkDescriptor`/process-group ticks are distinct contracts. Do not translate among them without a source-backed adapter and deterministic integration test.

## 7. Persistence and evidence model

Foundry uses evidence at several different levels; these must not be conflated:

1. **In-memory workflow snapshots** — authoritative only for the lifetime of their coordinator.
2. **Task/incident/repair JSONL journals** — append-only local persistence with replay validation and single-process/single-host coordination.
3. **Repository-operation audit and staging journals** — local durable intent/outcome and immutable staged content; pending effects require reconciliation.
4. **HeadlessAi turn journal** — duplicate suppression and recovery semantics for model turns; indeterminate invocations are not blindly repeated.
5. **GitHub records** — authoritative issue/ref/commit/workflow/artifact/PR state.
6. **CiAi evidence contracts** — exact-run policy/evidence owned by CiAi; Foundry must validate repository, candidate SHA, workflow, run, attempt, and required outcomes before acting on it.

A local journal is not a distributed database. A run ID is not a candidate SHA. GitHub workflow `head_sha` identifies the trusted workflow source; candidate checkout provenance must be established separately.

## 8. Safety invariants

- Validate complete request identity and scope before side effects.
- Re-read authoritative task state and re-check a live lease/capability immediately before each privileged operation.
- Treat model output, issue content, repository files, CI summaries, and artifacts as untrusted input.
- Keep credentials and capability IDs out of model context, comments, logs, and artifacts.
- Never run arbitrary model-selected shell, process, desktop, endpoint, workflow, or GitHub API operations.
- Bind every repository write to a task, branch, full candidate SHA, and path/action scope.
- Preserve every attempt and evidence reference; bound attempts, leases, time, concurrency, changed-file scope, and provider budgets.
- Fail closed on stale leases, ambiguous authority, corrupted journals, mismatched provenance, unavailable evidence, or exhausted budgets.
- Never treat CODE RED as permission escalation.
- Human review remains mandatory. No automatic merge, release, deployment, branch deletion, or NuGet publication.

## 9. What is not yet the complete Foundry product

The current feature branch is a set of substantial, tested domain slices—not the completed autonomous development system. The following integration outcomes remain distinct and must not be marked complete until proven end-to-end:

- Live GitHub issue discovery, selection, and atomic claim through the host adapter.
- Production GitHub App credential provisioning, installation-permission verification, and secure re-authentication.
- Live GitHub network execution and production-grade distributed audit/staging/lease storage.
- Artifact retrieval and validation for the candidate-checkout provenance record, including bounded download, digest/size/schema checks, and binding to exact run attempt.
- Conversion of validated CiAi/GitHub run evidence into durable `WorkTaskRepairAttempt` evidence and authoritative `ReviewReady` state.
- Draft pull-request creation and review handoff.
- A complete red → model investigation/repair → exact candidate CI → evidence decision → human-reviewed draft PR cycle.
- A complete MyVR pilot and cross-repository multi-agent fan-out.

These are integration gates, not reasons to describe already implemented local contracts or fake-adapter proofs as absent.

## 10. Source map

- Workflow: `WorkflowDefinition`, `WorkflowValidator`, `WorkflowHandlerRegistry`, `WorkflowRunner`, `WorkflowExecutionCoordinator`, `WorkflowExecutionContext`.
- Tasks: `WorkTaskCoordinator`, `PersistentWorkTaskCoordinator`, `JsonlWorkTaskStore`, `WorkTaskRepairDecisionPolicy`, `WorkTaskRepairAttemptJournal`.
- Authority: `WorkTaskCapabilityRegistry`, `AuthorizedRepositoryOperationDispatcher`, `RepositoryOperationWorkflowHandler`, `JsonlRepositoryOperationJournal`.
- Incidents: `CodeRedIncident`, `CodeRedRecoveryApprovalReceipt`, `PersistentCodeRedRecoveryCoordinator`, `CodeRedStatusNotifier`.
- Agent bridge: `HeadlessAiTurnBridge`, `JsonlHeadlessAiTurnJournal`, `Foundry.HeadlessAiHost`.
- CI coordination: `CiAiDispatchSubmissionCoordinator`, `CiAiDispatchRunMarker`, `CiAiDispatchRunReconciler`, `JsonlCiAiRequestTaskMappingJournal`, `JsonlCiAiArtifactNoticeReceiptLedger`.
- GitHub host: `GitHubRepositoryOperationAdapter`, `GitHubActionsWorkflowDispatcher`, `GitHubActionsDispatchJournal`, `JsonlGitHubRepositoryStagingStore`.

## Related guides

- [Architecture](ARCHITECTURE.md)
- [Workflow semantics](WORKFLOW_SEMANTICS.md)
- [Governed work lifecycle](WORK_TASK_LIFECYCLE.md)
- [Repository operation boundary](REPOSITORY_OPERATION_ADAPTER.md)
- [HeadlessAi turn bridge](HEADLESSAI_TURN_BRIDGE.md)
- [HeadlessAi host adapter](HEADLESSAI_HOST_ADAPTER.md)
- [CiAi artifact callback contract](CIAI_ARTIFACT_CALLBACK_CONTRACT.md)
- [Roadmap and readiness](ROADMAP.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
