<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Architecture

**Status:** the FSM_API integration probe, deterministic fox/sheepdog scenario, sequential workflow runner, and retained in-process lifecycle coordinator passed [development CI run #84](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/40546957980). Broader Hub migration and release readiness remain unverified.

## Purpose

Foundry is the Workshop Hub's host-neutral workflow execution and lifecycle domain. It accepts explicit workflow definitions, validates them before handler side effects, coordinates the existing sequential runner, retains in-process run state, and reports structured outcomes. It does not own Hub presentation or FSM_COS composition. The source-backed [Hub execution ownership and host contract](HUB_EXECUTION_OWNERSHIP.md) distinguishes this implemented workflow seam from the still-unimplemented RuntimeAssembly scheduling handoff and legacy WebPage process-group behavior.

## Responsibility boundaries

| System | Intended responsibility | Current evidence |
|---|---|---|
| Foundry | Workflow definition/validation, run lifecycle, execution coordination, result reporting | Version-1 data-only definition, preflight validator, host-registered sequential runner, in-process lifecycle coordinator, immutable snapshots, and host-facing `IWorkflowExecutionCoordinator`; no RuntimeAssembly scheduler or remote transport |
| The / Architect | Hub intent and human-facing coordination | Cross-repository ownership contract; Hub presentation is optional |
| GUI | Optional Hub presentation and explicit user intents | GUI issue #53 tracks presentation contract; no execution authority |
| FSM_API | FSM mechanics when directly consumed | Published 1.0.13 package restored, built, and exercised by the prior passing probe; no reusable adapter yet |
| FSM_COS | Runtime composition when supported by its implementation | Source confirms composition boundary; Foundry integration not implemented |
| FSM_Serialization | Binary representation boundary for domain-owned values | Existing package contract inspected; no Foundry format selected |
| ProtocolAi | Potential protocol/contract interoperability | Deterministic identity vocabulary; Foundry integration is future work |
| GrammarAi | Potential language grammar/representation capabilities | Deterministic grammar structure; Foundry integration is future work |
| AnyApp / WebPage / headless CI | Potential execution hosts and adapters | WebPage is the current Hub behavior reference; migration is not yet proven |

## Workflow execution and retained lifecycle

The existing `WorkflowRunner` remains the only sequential workflow executor. It validates the entire definition against the host-owned handler registry, runs steps in declared order, and returns structured step/workflow outcomes.

`WorkflowExecutionCoordinator` adds the Hub-facing lifecycle around that runner:

- Accept a definition without executing it.
- Start an accepted run exactly once.
- Query an immutable snapshot containing run identity, status, timestamps, and a terminal result when available.
- Request cooperative cancellation before or during execution.
- Dispose by cancelling accepted/running work and awaiting completion.

This is an in-process, in-memory lifecycle. It is not a durable queue, scheduler, persisted run history, or cross-process transport. The coordinator does not reinterpret `WorkflowResult`, add retries, or turn arbitrary handlers into safe transactional operations.

## Keep neighboring concepts distinct

WebPage's Hub/process-group contracts include `WorkDescriptor`, `CompletionToken`, `IExecutionProvider.Execute`, and named process-group updates. FSM_COS returns a `RuntimeAssembly`. These contracts are not interchangeable with Foundry's `WorkflowDefinition` and `WorkflowResult`. Trace actual callers and tests before deciding whether they share any lower-level lifecycle or observation primitive.

GUI consumes presentation-facing snapshots and sends explicit commands through an adapter. It does not own the authoritative loop or runtime state. WebPage remains a behavior/reference fixture while any migration is proven through tests. FSM_COS remains the composition authority.

## Verified first state-execution slice

The `Foundry.FsmApiProbe` project uses the published FSM_API package to create a named FSM, assert its initial state, execute the public update path, assert the transition, and perform scoped cleanup. The `Foundry.FoxSheepdog` project exercises the fox/dog state sequence with scripted ticks, asserts the cross-FSM transition, and emits a TSV trace and SVG. The existing CI workflow compares repeat-run outputs byte-for-byte.

These are deterministic state-execution demonstrations, not automatically treated as Foundry workflows. The coordinator tests and deterministic state-evidence checks passed in [CI run #84](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/40546957980).

## Safety boundaries

- Workflow input is data, not executable code.
- Do not evaluate arbitrary scripts or expressions from an untrusted manifest.
- Use least-privilege CI permissions and explicit timeouts.
- Cancellation is cooperative; a handler that ignores its token can delay shutdown.
- Do not expose raw exception messages that might contain secrets.
- Do not publish repository content, packages, or deployments automatically.
- Publication and deployment require explicit user approval.

## Cross-language direction

C#, Java, Rust, and other languages may eventually be supported through explicit language adapters and stable data contracts. A shared definition alone does not make runtimes interoperable: each execution boundary needs a defined transport/call mechanism, data representation, error model, cancellation semantics, and security model. This is a future direction, not a current feature.

## Architectural decision rule

Prefer reusing Workshop technologies where their real interfaces fit. Introduce a new reusable runtime abstraction only when a working test demonstrates a capability that cannot reasonably live in a thin host/adapter. Preserve the distinction between workflow execution, host-scheduled work, legacy process-group ticks, and composed runtime assemblies.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
