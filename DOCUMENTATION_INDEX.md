<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Documentation Index

**Repository:** TheSingularityWorkshop.FSM_Foundry  
**Purpose:** Navigate the domain theory, implementation contracts, verification evidence, and remaining integration gates.

## Entry points

- [README](README.md) — product orientation and branch-aware status.
- [Domain](docs/DOMAIN.md) — complete Foundry domain: workflow execution, governed repository work, authority, evidence, ownership, and limits.
- [Architecture](docs/ARCHITECTURE.md) — component map, authority flow, adapters, persistence, and recovery.
- [Agent onboarding](AGENTS.md) — contributor policy, safety invariants, branch/release guardrails, and evidence rules.
- [Workflow semantics](docs/WORKFLOW_SEMANTICS.md) — sequential execution, validation, outcomes, and bounded context.
- [Workflow execution context](docs/WORKFLOW_EXECUTION_CONTEXT.md) — JSON data channel, bounds, and ownership.
- [Governed work task lifecycle](docs/WORK_TASK_LIFECYCLE.md) — task envelope, lease fencing, state transitions, journals, CODE RED, and exact-CI decisions.
- [CODE RED recovery receipt](src/Foundry.Workflow/CodeRedRecoveryApprovalReceipt.md) — signed recovery scope, canonical payload, verifier boundary, and single-use audit.
- [CODE RED recovery UML](docs/UML/CodeRedRecoveryApprovalReceiptUML.md) — source/test-derived recovery neighborhood and sequence.
- [Repository capability registry](src/Foundry.Workflow/WorkTaskCapabilityRegistry.md) — lease-bound grant scope, invalidation, and limits.
- [Repository operation dispatcher](src/Foundry.Workflow/AuthorizedRepositoryOperationDispatcher.md) — typed operation boundary, revalidation, audit, and workflow integration.
- [Repository operation authorization UML](docs/UML/RepositoryOperationAuthorizationUML.md) — capability/dispatcher class neighborhood and transport sequence.
- [Repair decision policy UML](docs/UML/WorkTaskRepairDecisionPolicyUML.md) — exact CI provenance checks, bounded retry decisions, CODE RED escalation, and attempt-journal semantics.
- [GitHub repository adapter](src/Foundry.GitHubHost/GitHubRepositoryOperationAdapter.md) — concrete GitHub repository operations, token boundary, staging, and commit verification.
- [GitHub Actions dispatcher](src/Foundry.GitHubHost/GitHubActionsWorkflowDispatcher.md) — trusted workflow source, durable dispatch correlation, and exact-attempt verification.
- [GitHub host adapter UML](docs/UML/GitHubHostAdapterUML.md) — repository operation and CI dispatch sequences.
- [HeadlessAi turn bridge UML](docs/UML/HeadlessAiTurnBridgeUML.md) — lease gate, immutable request snapshot, durable at-most-once invocation, provenance validation, and stale-lease recovery.
- [Repository operation boundary](docs/REPOSITORY_OPERATION_ADAPTER.md) — typed operations, host capabilities, GitHub adapter, dispatch reconciliation, and candidate-checkout provenance.
- [HeadlessAi turn bridge](docs/HEADLESSAI_TURN_BRIDGE.md) — lease-gated turns, provenance, duplicate suppression, and recovery.
- [HeadlessAi host adapter](docs/HEADLESSAI_HOST_ADAPTER.md) — source-pinned adapter and manual host composition.
- [CiAi artifact callback contract](docs/CIAI_ARTIFACT_CALLBACK_CONTRACT.md) — intended ready-notice/authenticated-ingress/immutable-pull/receipt protocol.
- [Hub integration boundary](docs/HUB_INTEGRATION_BOUNDARY.md) — why legacy host work descriptors and process-group ticks are not workflow definitions.
- [Contract inspection ledger](docs/CONTRACT_INSPECTION.md) — external Workshop contracts and unresolved integration decisions.
- [Roadmap](docs/ROADMAP.md) — milestones and readiness gates.
- [UML policy and inventory](docs/UML/README.md) — source provenance and diagram coverage.
- [Gallery](Gallery.md) — deterministic demonstrations and recipes.

## Coverage and evidence ledger

Status labels in this table are scoped to the active feature branch unless explicitly marked otherwise. **Implemented** means source exists; **tested** means a named deterministic test/CI proof covers it; **live** means an actual external service invocation has been verified. These are different evidence levels.

| Domain area | Current implementation/evidence | Remaining gate |
|---|---|---|
| Sequential workflow execution | Typed definitions, complete preflight validation, host handler registry, sequential runner, bounded JSON context, retained in-process lifecycle | Durable workflow-run history, cross-process transport, parallelism, retry/compensation remain outside v1 |
| FSM_API | Separate-process package probe and deterministic multi-state Fox/Sheepdog scenario in CI | No claim that those demonstrations are Hub workflow definitions |
| Governed task lifecycle | Versioned task envelope, validated states, expiring fenced leases, attempt budget, sequenced events, persistent local replay | Distributed/multi-host task store and live GitHub issue claim |
| CODE RED | Incident records/notices, mutation suspension, signed single-use recovery receipt, sequence-bound validation, persistence/replay tests | Production human-authentication/receipt issuer integration and external notification transport |
| Repair policy/evidence | Bounded deterministic decision policy, exact CI run/attempt identity checks, repair-attempt journal | End-to-end ingestion of trusted CiAi verdict/artifact into task state |
| HeadlessAi | Lease-gated bridge, immutable request/result mapping, provenance validation, at-most-once journal; source host adapter and console composition | Provisioned host credentials and a verified live provider turn |
| Repository authorization | Typed closed operations, host-issued task/lease/branch/SHA/path/action capabilities, second authorization demand after audit reservation, secret-safe pending/completion/failure journal | Production host capability/credential composition; distributed authorization state; explicit reconciliation of indeterminate pending operations |
| GitHub repository adapter | Candidate branch creation, immutable reads, staged blob writes, explicit commit/ref advance, local audit/staging journals | Live GitHub API proof and production GitHub App permissions |
| GitHub Actions | Host-allowlisted dispatch, durable dispatch intent/reconciliation, trusted workflow-source pinning, request-ID run correlation, exact run-attempt/required-job verification | Real authorized dispatch; host-side candidate-provenance artifact retrieval/validation and task-evidence ingestion |
| CiAi callback | Versioned contract for authenticated notice, immutable artifact pull, durable idempotency receipt | HTTPS receiver, transport adapter, live CiAi integration |
| Draft PR handoff | Capability vocabulary and target lifecycle semantics | Actual draft PR creation, durable evidence conversion, human review handoff |
| End-to-end autonomy | Bounded components and deterministic adapter proofs | Full issue → claim → model → repair → exact CI → evidence → draft PR cycle; MyVR pilot |
| Release/publication | Human approval boundary documented; privileged operations absent from current repository-operation API | Not automated; explicit owner authorization required |

## Verification notes

PR #26 documentation head `17b18128c7624c09fa417c3b66f745a1109242c3` passed exact-head push CI run [#42557642333](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/42557642333). Current feature head `371c9a284d289b1c52585ed00f9509e6aa6670b0` is a reconciliation merge commit containing current `development` at `dc1324f9bfcc61e6de32f7c52d7686025976f00b` plus the active feature work. The run completed successfully and validates the ordinary build/test path, separate-process FSM_API probe, repeated Fox/Sheepdog execution/comparison, and evidence upload. It was a `push` event, so the `workflow_dispatch`-only candidate checkout/provenance steps were skipped. A live authorized dispatch and host-side artifact retrieval/validation remain unproven. Push/PR validation for the reconciliation head is running; the Sith Policy check correctly flags the temporary feature branch until it is merged and the temporary branch is removed under owner authorization.

## Update rule

Every implementation claim must point to source, tests, exact-head CI, or a reproducible external artifact. A contract document is not proof of a deployed transport; a fake-adapter test is not live-service proof; a dispatch acknowledgment is not CI evidence; and a green CI run is not merge/release authorization. Update this ledger whenever code or evidence changes.

---

<!-- The Singularity Workshop footer -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
