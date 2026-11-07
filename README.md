<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# TheSingularityWorkshop.FSM_Foundry

[![CI](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/workflows/foundry-fsm-api-probe.yml/badge.svg?branch=development)](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/workflows/foundry-fsm-api-probe.yml)
[![License: MIT](https://img.shields.io/github/license/TrentBest/TheSingularityWorkshop.FSM_Foundry)](LICENSE)
[![Sith Policy](assets/sith-policy.svg)](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/workflows/sith-policy.yml)

**Foundry is the Workshop's execution and governed-work control plane.** It validates explicit work, coordinates the lifecycle that owns it, enforces host-issued authority at side-effect boundaries, preserves provenance, and reports outcomes for review. It connects workflow execution with governed repository work without turning model output into permission or making a CI dispatch acknowledgment look like a successful repair.

> **Implementation status — branch-aware:** the base `development` branch contains the sequential workflow runtime, bounded JSON execution context, deterministic FSM_API probes, and foundational governed-work/HeadlessAi/CiAi contracts already merged there. The active [PR #26](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/pull/26) feature branch adds the typed repository-operation boundary, local operation/staging journals, concrete GitHub branch/read/stage/commit adapter, allowlisted GitHub Actions dispatch, durable dispatch reconciliation, exact run-attempt/required-job verification, and a trusted-workflow candidate-checkout provenance artifact. The code at PR #26 head `9e55326f2bc075019b2810c9f58221832f144a9b` passed exact-head push and pull-request CI runs [#42423025239](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/42423025239) and [#42423029278](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/42423029278). Those runs exercised ordinary push/PR paths; they did **not** execute the workflow-dispatch-only candidate checkout or artifact steps. Documentation-only commits now follow that code head and still need their own CI confirmation.

## 01 — What Foundry owns

Foundry owns two related but distinct lifecycle domains:

- **Workflow execution:** typed workflow definitions, complete preflight validation, host-registered handlers, sequential execution, bounded run-scoped JSON context, structured outcomes, and retained in-process run lifecycle. `WorkflowRunner` is the only sequential step executor; `WorkflowExecutionCoordinator` owns the workflow-run lifecycle.
- **Governed repository work:** task identity and provenance, leases and fencing, bounded attempts, sequenced audit history, CODE RED incident/recovery semantics, host-issued scoped capabilities, agent-turn coordination, repository-operation authorization, CI dispatch reconciliation, and review handoff state. `WorkTaskCoordinator` owns this separate task lifecycle.

The task lifecycle is not a workflow run. A workflow handler may expose a governed operation, but the two state models must not be collapsed into one runner or one status enum.

## 02 — Ownership boundaries

- **Foundry coordinates:** work/task lifecycles, bounded orchestration, authorization checks, correlation, audit, evidence decisions, and authoritative outcomes.
- **FSM_API owns FSM mechanics.** Foundry consumes it where integrated; it does not reimplement the transition engine.
- **FSM_COS composes MicroBundles** into a `RuntimeAssembly`. Foundry executes work and does not recompose an assembly.
- **HeadlessAi invokes models.** Foundry's bridge gates turns and validates provenance; HeadlessAi does not own task lifecycle, repository access, or retries.
- **CiAi owns deterministic CI policy and exact-run evidence.** Foundry requests and reconciles runs but does not invent CI truth.
- **GitHub owns repository truth:** issues, refs, commits, workflow runs, artifacts, reviews, and pull requests.
- **Hosts own credentials and concrete capabilities.** They authenticate, configure allowlists, provide secure re-authentication, and enforce environmental policy.
- **GUI presents.** It may display authoritative snapshots and send explicit commands; it does not own execution state.

See [Domain](docs/DOMAIN.md) and [Architecture](docs/ARCHITECTURE.md) for the complete model.

## 03 — See it in a minute

For sequential workflow execution, start with [WorkflowDefinition](src/Foundry.Workflow/WorkflowDefinition.cs), [WorkflowRunner](src/Foundry.Workflow/WorkflowRunner.cs), and [WorkflowExecutionCoordinator](src/Foundry.Workflow/WorkflowExecutionCoordinator.cs).

For governed work, follow [WorkTaskCoordinator](src/Foundry.Workflow/WorkTaskCoordinator.cs), [WorkTaskCapabilityRegistry](src/Foundry.Workflow/WorkTaskCapabilityRegistry.cs), and [AuthorizedRepositoryOperationDispatcher](src/Foundry.Workflow/AuthorizedRepositoryOperationDispatcher.cs). The GitHub host implementation lives in `src/Foundry.GitHubHost`.

A host integration follows this pattern:
1. Validate a task envelope bound to one repository, issue, base SHA, path scope, and attempt budget.
2. Claim it under a unique expiring agent lease.
3. Grant a short-lived capability for explicit actions, paths, branch, and candidate SHA.
4. Let the agent propose a typed operation—not a shell command, endpoint, credential, or arbitrary GitHub API call.
5. Re-read task/lease state and revalidate capability immediately before the host adapter performs the operation.
6. Persist intent and reconcile ambiguous external outcomes instead of blindly retrying.
7. Bind CI evidence to the exact candidate and run attempt before allowing repair/review state to advance.

The separate [FSM_API probe](src/Foundry.FsmApiProbe/Program.cs) and [Fox/Sheepdog scenario](src/Foundry.FoxSheepdog/Program.cs) remain deterministic FSM demonstrations, not automatic Hub workflows.

## 04 — Contracts, tests, and boundaries

- [Domain](docs/DOMAIN.md) — complete Foundry domain, lifecycle concepts, ownership, safety invariants, and remaining product gaps.
- [Architecture](docs/ARCHITECTURE.md) — component relationships, authority flow, persistence, and recovery.
- [Workflow semantics](docs/WORKFLOW_SEMANTICS.md) — sequential workflow execution and bounded context.
- [Governed work lifecycle](docs/WORK_TASK_LIFECYCLE.md) — task envelopes, leases, event history, recovery, and repair decisions.
- [Repository operation boundary](docs/REPOSITORY_OPERATION_ADAPTER.md) — typed operations, capability checks, GitHub adapter, dispatch, and candidate provenance.
- [HeadlessAi turn bridge](docs/HEADLESSAI_TURN_BRIDGE.md) and [host adapter](docs/HEADLESSAI_HOST_ADAPTER.md) — lease-gated model invocation and host composition.
- [CiAi artifact callback contract](docs/CIAI_ARTIFACT_CALLBACK_CONTRACT.md) — versioned notice, authenticated ingress, immutable artifact pull, and receipt protocol.
- [Contract inspection](docs/CONTRACT_INSPECTION.md) — external Workshop contract findings.
- [Roadmap](docs/ROADMAP.md) and [documentation index](DOCUMENTATION_INDEX.md).
- [Agent onboarding](AGENTS.md) — branch/release rules, authority boundaries, and evidence requirements.

Tests use deterministic fakes and local stores to cover lifecycle transitions, lease fencing, capability scope, signed CODE RED recovery, durable replay, at-most-once model-turn behavior, repository operation authorization, staged Git content, commit/ref rules, dispatch correlation, and exact run-attempt job verification. The CI workflow also builds projects, runs the separate-process FSM_API probe, repeats the Fox/Sheepdog scenario, compares evidence, and uploads artifacts.

## 05 — What remains before the complete autonomous loop

The repository-operation branch is substantial implementation, but it is not yet the complete autonomous development system. Remaining integration gates include:

- Live GitHub issue discovery, selection, and atomic claim.
- Production GitHub App credential provisioning, installation-permission checks, and secure re-authentication.
- Live GitHub network proof for branch/ref/commit/Actions behavior.
- A real workflow-dispatch run proving the trusted workflow source, exact candidate checkout, and provenance artifact end to end.
- Host retrieval and bounded validation of that artifact (digest, size, schema, request/repository/workflow/candidate/run/attempt identity).
- Conversion of verified CiAi/GitHub evidence into durable repair-attempt evidence and authoritative `ReviewReady` state.
- Draft-PR creation and human review handoff.
- The complete bounded red → investigate → repair → exact candidate CI → evidence decision → draft PR cycle, then the MyVR pilot.

The current JSONL journals are local durable stores, not distributed databases or multi-host lease services. Fake HTTP tests are not live GitHub integration. A compiling HeadlessAi adapter is not proof of a real model invocation. A documented CiAi callback contract is not a deployed HTTPS receiver. A candidate-checkout artifact upload step is not host-side artifact validation.

No automatic merge, release, deployment, branch deletion, or NuGet publication is part of Foundry's authority. Those actions remain explicitly gated by human approval.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
