<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry UML and Source Documentation

**Status:** manual, source-grounded views exist for the FSM_API probe, Fox/Sheepdog scenario, workflow execution coordinator, governed task coordinator, and CODE RED recovery authorization. The architecture guide contains a component/authority-flow diagram for the expanded governed-work domain. Dedicated neighborhoods for the capability/dispatcher boundary now exist; the HeadlessAi bridge and GitHub operations remain to be added. Automated generation and stale-view detection are not implemented.

## Principles

1. A diagram must represent source or runtime evidence, or be visibly labelled **Conceptual / Target**.
2. Each new C# source file should have a companion Markdown document explaining its purpose, contract, and relevant UML view.
3. Each significant type should have a separate `[TypeName]UML.md` neighborhood page in this directory: focus type centered, its users above, dependencies below, and relationships explicitly labelled.
4. Use cases describe user goals and are kept distinct from source structure.
5. Generated evidence should preserve stable symbol identity, source spans, provenance, and generation inputs.
6. Do not invent types, methods, dependencies, or runtime behavior to make a diagram look complete.

## Planned structure

- `docs/UML/README.md` — policy, coverage, and navigation.
- `docs/UML/INVENTORY.md` — source areas and view coverage.
- `docs/UML/Standard/` — reusable diagram and evidence conventions as they mature.
- `docs/UML/[TypeName]UML.md` — type neighborhood diagrams grounded in actual declarations.

## View types

- Class/type neighborhoods and explicit dependency relationships.
- Sequence diagrams for real interaction paths, including failure and cancellation.
- Activity/control-flow diagrams for decision paths.
- State lifecycle diagrams for stateful objects.
- Use-case diagrams for user-visible goals.
- Architecture/component and deployment diagrams.
- Data models/lineage and requirement/test traceability.

Only add a view when there is sufficient evidence. Mark unimplemented flows as target designs.

## Current views

- [WorkflowExecutionCoordinator neighborhood](WorkflowExecutionCoordinatorUML.md) — retained run lifecycle and its relationship to the existing runner.
- [WorkTaskCoordinator neighborhood](WorkTaskCoordinatorUML.md) — governed task lifecycle and exact CI evidence decision.
- [CODE RED recovery authorization neighborhood and sequence](CodeRedRecoveryApprovalReceiptUML.md) — signed receipt scope, host trust boundary, persistent recovery path, and fail-closed branches.\n- [Repository operation authorization neighborhood and sequence](RepositoryOperationAuthorizationUML.md) — host capability scope, double-demand dispatch boundary, audit intent, and adapter invocation.
- [Coordinator source companion](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.md) — purpose, public commands, limits, and tests.
- [WorkTaskCoordinator source companion](../../src/Foundry.Workflow/WorkTaskCoordinator.md) — task envelope, leases, evidence-gated transitions, recovery, and persistence boundary.
- [CODE RED recovery receipt source companion](../../src/Foundry.Workflow/CodeRedRecoveryApprovalReceipt.md) — canonical signing payload, receipt scope, verification, replay, and host responsibilities.\n- [WorkTaskCapabilityRegistry source companion](../../src/Foundry.Workflow/WorkTaskCapabilityRegistry.md) — lease-bound grants, exact operation scope, CODE RED invalidation, and limits.\n- [AuthorizedRepositoryOperationDispatcher source companion](../../src/Foundry.Workflow/AuthorizedRepositoryOperationDispatcher.md) — typed operations, pre-transport recheck, audit boundary, and workflow bridge.
- [FSM_API probe companion](../../src/Foundry.FsmApiProbe/Program.md) and [probe neighborhood](FoundryFsmApiProbeUML.md) — state-execution proof.
- [Fox/Sheepdog neighborhood](FoundryFoxSheepdogUML.md) — deterministic multi-state demonstration.
- [Architecture diagram](../ARCHITECTURE.md#1-system-shape) — current component and authority flow; it is an authored architecture view grounded in the source ownership boundaries, not generated UML.

The expanded source inventory is in [INVENTORY.md](INVENTORY.md). The task coordinator, CODE RED recovery authorization, and repository-operation authorization now have dedicated neighborhoods and tested sequences. The agent bridge and GitHub operations still need dedicated views.

## Validation expectations

A future CI check should detect missing required companion documents, stale generated views, broken internal links, and diagrams whose referenced source identity no longer exists. A generator or Roslyn integration is not present merely because this policy describes it.

---

<!-- The Singularity Workshop footer -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
