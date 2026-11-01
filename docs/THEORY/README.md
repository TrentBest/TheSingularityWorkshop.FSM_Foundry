<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Theory — Learning Path

**Purpose:** teach the Foundry domain as a modular, source-grounded course. This path explains why the domain exists, how to reason about its boundaries, how the implementation realizes those ideas, and what evidence is still missing.

**Evidence scope:** this learning path is being established against the repository's `development` branch. Feature-branch proposals are not treated as part of the development implementation unless explicitly identified as such.

## How to use this path

Read the chapters in order for a first encounter. Experienced readers can open an individual chapter and follow its source, tests, and further-reading links. Each chapter is intended to stand alone and can be reordered or expanded by instructors without requiring edits to one monolithic textbook.

The chapters follow a common teaching sequence: familiar problem, vocabulary, reasoning model, visual, implementation mapping, worked example, evidence and limits, practice, and further reading. API references remain in the contract documents; UML remains the structural map; recipes remain the runnable instructions.

## Chapter map

| Order | Chapter | Domain question | Status |
|---|---|---|---|
| 01 | [Work as an explicitly coordinated process](01-work-as-coordinated-process.md) | Why does work need a definition, an owner, a lifecycle, and an observable outcome? | **Available** |
| 02 | [Workflow definitions and execution semantics](02-workflow-definitions-and-execution.md) | How is a declared workflow validated and executed without granting it ambient authority? | **Available** |
| 03 | [Tasks, leases, and lifecycle ownership](03-tasks-leases-and-lifecycle.md) | How do we assign bounded work, prevent stale agents from acting, and preserve its history? | **Available**; CODE RED approval ownership discrepancy explicitly tracked |
| 04 | [Capabilities, authority, and human approval](04-capabilities-and-authority.md) | How do hosts grant narrowly scoped power without trusting definitions or model output? | **Available**; CODE RED approval ownership discrepancy explicitly tracked |
| 05 | [Evidence, verdicts, and repair decisions](05-evidence-and-repair.md) | What makes a result trustworthy enough to drive a bounded next action? | **Available**; CiAi integration proposals are tracked separately |
| 06 | [Persistence, replay, and recovery](06-persistence-and-recovery.md) | How can state survive process restart without pretending a local journal is a distributed system? | **Available** |
| 07 | [Hosts, adapters, and external systems](07-hosts-and-adapters.md) | How do desktop, web, CI, and headless hosts participate without owning Foundry semantics? | **Available**; live GitHub/CiAi integration remains unimplemented |
| 08 | [Scheduling, dependencies, and concurrency](08-scheduling-and-concurrency.md) | When can work run concurrently, and what must remain sequential? | **Available**; no general dependency-graph scheduler is claimed |
| 09 | [Observability, audit, and reproducibility](09-observability-and-evidence.md) | How can an operator reconstruct what happened and distinguish proof from presentation? | Planned |
| 10 | [Security model and failure containment](10-security-and-failure-containment.md) | How does the system fail safely when identity, evidence, persistence, or external services are uncertain? | Planned |

## Domain map

Foundry is the coordinating authority for explicit work. Its domain is larger than a workflow runner but smaller than an operating system or a universal automation platform.

- **Definition:** what work is requested, its identity, constraints, and declared steps.
- **Validation:** whether the request is structurally and semantically acceptable before side effects.
- **Execution:** the single workflow runner invokes host-registered handlers according to the supported execution semantics.
- **Lifecycle:** run and task state, claims, leases, cancellation, and terminal outcomes.
- **Authority:** host-controlled capabilities and human approval boundaries.
- **Evidence:** provenance-bound observations used to explain outcomes and evaluate bounded decisions.
- **Persistence:** durable records and replay rules, with guarantees stated per store and host.
- **Integration:** adapters to CI, model invocation, repositories, user interfaces, and other external systems.
- **Operations:** status, audit, diagnostics, recovery, and reproducibility.

These are related concerns, not interchangeable abstractions. A workflow run is not a work task; a task lease is not a capability; a CI verdict is not a model instruction; a model proposal is not authorization; and a user-interface snapshot is not authoritative state.

## Evidence labels

- **Implemented on development:** present in the inspected development source and tests.
- **Feature branch:** implemented or proposed on a named, unmerged branch; not part of development.
- **Source-checked:** declarations or behavior were inspected, but no execution claim is made.
- **Conceptual:** a teaching model or target design, not a claim about current runtime behavior.
- **Planned:** a known gap with no implementation claim.

A passing test proves only the behavior and conditions it exercises. A local append-only journal does not prove distributed consistency; a fake adapter does not prove a live provider; and a green CI run does not grant permission to merge, publish, deploy, or mutate another repository.

## Further reading

- [Domain and ownership](../DOMAIN.md) — authoritative responsibility boundary.
- [Architecture](../ARCHITECTURE.md) — current component and dependency boundaries.
- [Documentation index](../../DOCUMENTATION_INDEX.md) — all guides, contracts, diagrams, and evidence.
- [Roadmap and readiness](../ROADMAP.md) — implementation gates and known gaps.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
