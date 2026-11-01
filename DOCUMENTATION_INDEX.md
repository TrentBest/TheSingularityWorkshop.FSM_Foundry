<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Documentation Index

**Repository:** TheSingularityWorkshop.Foundry  
**Purpose:** Navigate product theory, implementation evidence, diagrams, demonstrations, and readiness.

## Entry points

- [README](README.md) — product intent and five-section overview.
- [Agent onboarding](AGENTS.md) — repository-specific contribution, safety, branch, release, and documentation rules.
- [Gallery](Gallery.md) — demonstrations and recipes.
- [Foundry theory learning path](docs/THEORY/README.md) — modular textbook chapters, domain vocabulary, implementation mapping, and evidence limits.
- [Domain and ownership](docs/DOMAIN.md) — proposed Foundry domain, ownership boundaries, safety invariants, and first release slice.
- [Workflow semantics](docs/WORKFLOW_SEMANTICS.md) — version-1 execution model, bounded per-run context, validation, outcomes, and explicit limitations.
- [Workflow execution context](docs/WORKFLOW_EXECUTION_CONTEXT.md) — typed JSON inputs/outputs, enforced size limits, host ownership, and data-only safety boundary.
- [Governed work task lifecycle](docs/WORK_TASK_LIFECYCLE.md) — task envelopes, fenced leases, local journals, capabilities, CODE RED notices, and exact-CI repair decisions.
- [CiAi artifact callback contract](docs/CIAI_ARTIFACT_CALLBACK_CONTRACT.md) — Foundry-owned v1 HTTPS ingress, authentication, durable idempotency receipts, immutable artifact pull, and provider acknowledgement.
- [Foundry–HeadlessAi turn bridge](docs/HEADLESSAI_TURN_BRIDGE.md) — task-lease-gated invocation, durable at-most-once journal, host adapter mapping, and remaining live-integration gates.
- [Concrete HeadlessAi host adapter](docs/HEADLESSAI_HOST_ADAPTER.md) — pinned source integration, immutable request mapping, normalized result mapping, credential boundaries, and manual provider smoke runner.
- [Architecture](docs/ARCHITECTURE.md) — system boundaries and evidence labels.
- [Contract inspection ledger](docs/CONTRACT_INSPECTION.md) — source-backed findings from the Workshop stack and unresolved decisions.
- [Roadmap and readiness](docs/ROADMAP.md) — milestones and verification gates.
- [UML policy and inventory](docs/UML/README.md) — diagrams, source provenance, and coverage.

## Coverage ledger

| Area | Current status | Evidence / next action |
|---|---|---|
| Product vision and boundaries | Domain and ownership documented against the implemented version-1 runtime | [Domain and ownership](docs/DOMAIN.md); validate proposed concepts against a working contract and tests |
| Source contract exploration | First pass recorded | [Contract inspection ledger](docs/CONTRACT_INSPECTION.md); deeper tests and WebApp source remain |
| Runnable workflow definition | Version-1 in-memory sequential contract with bounded per-run JSON context implemented on PR #12 | [Definition](src/Foundry.Workflow/WorkflowDefinition.cs), [validator](src/Foundry.Workflow/WorkflowValidator.cs), [runner](src/Foundry.Workflow/WorkflowRunner.cs), [context](docs/WORKFLOW_EXECUTION_CONTEXT.md); persisted workflow-definition format remains open |
| FSM_API integration | Basic real-package probe passes; expanded state scenario added | [Probe source](src/Foundry.FsmApiProbe/Program.cs), [fox/sheepdog scenario](src/Foundry.FoxSheepdog/Program.cs), [CI workflow](.github/workflows/foundry-fsm-api-probe.yml), and [successful expanded CI run](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/40025709384) |
| FSM_COS integration | Boundary inspected; Foundry integration not implemented | Only use for composition if a concrete workflow requires it |
| FSM_Serialization integration | Responsibility inspected; format not selected | Keep domain semantics in the owning contract; do not assume arbitrary-object serialization |
| Governed work lifecycle | Implemented on development; local task/incident/repair journals are single-process and not connected to live repository services | [Work-task lifecycle](docs/WORK_TASK_LIFECYCLE.md); local task/incident/repair journals are single-process, not GitHub-backed or distributed |
| HeadlessAi turn bridge and host adapter | Foundry bridge, concrete source adapter, and manual console composition root are present on development; live provider invocation and host credential provisioning remain unverified | [Turn bridge](docs/HEADLESSAI_TURN_BRIDGE.md), [host adapter](docs/HEADLESSAI_HOST_ADAPTER.md); host credential provisioning and a real provider invocation remain |
| CiAi artifact ingress | Host-owned v1 callback contract documented; ingress and report-consumer implementations remain on draft PR #14/#15 branches, not development | [CiAi artifact callback contract](docs/CIAI_ARTIFACT_CALLBACK_CONTRACT.md); ready notice + immutable pull + durable receipt boundary |
| CI execution | PR #10 latest governed-work head and PR #11 combined stack both passed the full proof workflow, including the durable turn journal | [PR #10 run #304](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41172981012) and [combined-stack PR #11 run #319](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41174088415); verified exact HeadlessAi host-adapter head `d410afd4e23aaf459bdf2ce526faf964d3d36274` in [run #337](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41221916732), then console composition build in [run #352](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41222737607) |
| Source-derived UML | Manual source-grounded views exist for selected types; repository-wide generation, complete type/use-case coverage, and stale-view detection are not implemented |
| Living Gallery | Basic probe and trace-derived SVG scenario verified | [Gallery](Gallery.md) and [successful expanded CI run](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/40025709384) |
| Optional article publishing | Design documented; not implemented | Build source-grounded article preview and excerpt images; keep external publishing opt-in |
| Tests and abuse cases | Workflow and governed-task tests cover ordering, invalid input, failure, cancellation, bounded task/lease/audit metadata, strict journal replay, lease fencing, and repair-policy provenance | CI run #337 passed the current PR #11 head; add real provider and trusted cross-service integration tests next |
| Release/publication | Not in scope | Requires explicit approval |

## Theory and learning path

The domain textbook begins at [docs/THEORY/README.md](docs/THEORY/README.md). Chapter 01 introduces explicit work coordination; subsequent chapters are individually tracked as planned until their content is written and source-checked.

## Update rule

Every claimed capability must point to implementation, tests, a workflow run, or a reproducible artifact. Keep proposed architecture separate from implemented behavior. Update this ledger when evidence changes.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
