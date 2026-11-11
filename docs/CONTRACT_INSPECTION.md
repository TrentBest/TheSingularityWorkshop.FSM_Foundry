<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Contract Inspection Ledger

**Original inspection date:** 2026-10-23  
**Original branch:** `development`  
**Purpose:** Historical evidence ledger recording what had been read before the first Foundry contracts were selected. It is not a current implementation inventory. See the current [Domain](DOMAIN.md), [Architecture](ARCHITECTURE.md), [Documentation Index](../DOCUMENTATION_INDEX.md), and [Roadmap](ROADMAP.md) for the expanded implementation and branch-aware status.

## Findings from source and project files

| Repository / file | Verified evidence | Implication for Foundry |
|---|---|---|
| [FSM_API `FSMBuilder.cs`](https://github.com/TrentBest/FSM_API/blob/master/FSM_API/FSMBuilder.cs) | Fluent `State`, `WithInitialState`, `Transition`, and `BuildDefinition` methods exist. | Use the actual builder API if a prototype demonstrates it fits; do not invent a second FSM syntax first. |
| [FSM_API `IStateContext.cs`](https://github.com/TrentBest/FSM_API/blob/master/FSM_API/IStateContext.cs) | Contexts implement `IContext` and provide mutable `IsValid`. | A Foundry execution context must satisfy the actual context contract if bound to FSM_API. |
| [FSM_API `FSMHandle.cs`](https://github.com/TrentBest/FSM_API/blob/master/FSM_API/FSMHandle.cs) | `FSMHandle.Update(...)` steps the instance; `CurrentState` is observable. `TransitionTo` forces transitions. | The executor must deliberately choose and test its stepping semantics. Do not assume the group tick is the only way to advance a handle. |
| [FSM_API `FSM_API.Create.cs`](https://github.com/TrentBest/FSM_API/blob/master/FSM_API/FSM_API.Create.cs) | Definitions and instances are registered through static `FSM_API.Create` methods; creation binds an `IStateContext` and processing group. | Static registry lifetime, naming collisions, cleanup, and repeat-run isolation need explicit prototype tests. |
| [FSM_API project file](https://github.com/TrentBest/FSM_API/blob/master/FSM_API/FSM_API.csproj) | Multi-targets .NET; package ID is `TheSingularityWorkshop.FSM_API`, version shown in source is 1.0.13, with no explicit package references in the project file. | Prefer the published package for a small consumer proof unless a specific test requires source linkage. |
| [FSM_API CI workflow](https://github.com/TrentBest/FSM_API/blob/master/.github/workflows/dotnet.yml) | Builds/tests the solution and packs artifacts; publishing job is gated and currently has an additional false condition. | Foundry CI should be independently verifiable; never infer that a green package workflow means Foundry is tested. |
| [FSM_COS `FsmCos.cs`](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/src/FSM_COS/FsmCos.cs) | `Execute(RuntimeManifest, ...)` resolves versioned MicroBundles, loads them, arbitrates up to a configured limit (default 10), and returns `RuntimeAssembly`; missing bundles, conflicts, cycles, and non-convergence fail. | FSM_COS is a composition subsystem, not a general step runner. Add it only when a workflow actually needs MicroBundle composition. |
| [FSM_COS `RuntimeManifest.cs`](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/src/FSM_COS/RuntimeManifest.cs) | Manifest names root MicroBundles and versions, with optional context, load-plan/schedule metadata, and semantic intent. Configuration and artifact location are separate concerns. | Do not overload this manifest with Foundry workflow-step semantics. A Foundry workflow definition is a distinct contract unless a proven mapping exists. |
| [FSM_COS `RuntimeAssembly.cs`](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/src/FSM_COS/RuntimeAssembly.cs) | The assembly exposes loaded MicroBundles, runtime ID, arbitration round index, and optional intent. | Assembly is a handoff result, not a workflow success/result object. |
| [FSM_COS runtime boundary](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/docs/RUNTIME_BOUNDARY.md) | Explicitly excludes stepping FSM_API groups, host lifecycle, presentation, networking, and Experience execution. | Preserve this boundary; do not make Foundry's responsibilities depend on a false reading of FSM_COS. |
| [FSM_Serialization README](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Serialization/blob/master/README.md) and [theory](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Serialization/blob/master/docs/THEORY.md) | Domain types own semantic representation; package provides binary pack/unpack and stream boundaries, not a universal schema or general object-graph serializer. | Do not assume it serializes arbitrary Foundry workflows. Inspect its actual interfaces and define domain-owned representation only after the workflow contract is selected. |
| [ProtocolAi README](https://github.com/TrentBest/TheSingularityWorkshop.ProtocolAi/blob/master/README.md) | Provider-neutral deterministic vocabulary/identity; no model inference, provider SDK, or transport. | Potentially useful for identity resolution, but not a workflow executor or publishing client. |
| [GrammarAi README](https://github.com/TrentBest/TheSingularityWorkshop.GrammarAi/blob/master/README.md) | Defines deterministic grammar structure over externally owned protocol identities. | Potentially useful for structural contracts; not a parser/execution engine by assumption. |
| [AnyApp project](https://github.com/TrentBest/AnyApp/blob/master/AnyApp.csproj) and [startup source](https://github.com/TrentBest/AnyApp/blob/master/MainWindow.xaml.cs) | Windows WPF host references FSM_COS, GUI.WPF, FSM_UserIO, ProtocolAi, and GrammarAi. Startup selects a manifest, composes through FSM_COS, then manifests the result. | AnyApp is a later host candidate, not a suitable first CI dependency because it is Windows/WPF-specific. |
| [AnyApp README](https://github.com/TrentBest/AnyApp/blob/master/README.md) | Host owns Experience selection, execution, presentation, and semantic intent meaning. | Keep Foundry's core separate from desktop UI. |
| [WebApp README](https://github.com/TrentBest/TheSingularityWorkshop.WebApp/blob/master/README.md) | Documentation entry point was inspected; implementation source/project and its concrete manifest/host contracts have not yet been verified in this pass. | No claims of Foundry/WebApp compatibility until source and tests are read. |

## Important caveats

- The repository README is not proof that every described feature currently runs.
- The public package version and the default-branch source are separate evidence; validate the exact package API in the consuming prototype.
- FSM_API documentation contains different descriptions of process-rate values in different places. The prototype should use an explicitly selected stepping path and tests, rather than rely on a comment alone.
- GitHub Actions is a host/orchestrator boundary; Foundry's workflow outcome must be mapped deliberately to process exit status and artifacts.
- **Historical finding as of 2026-10-23 only:** no Foundry runtime, manifest contract, tests, or CI execution had been implemented or verified during that initial inspection. This statement was superseded by the workflow runtime, governed-work lifecycle, HeadlessAi bridge, CiAi dispatch/reconciliation contracts, and GitHub host-adapter work documented in the current domain and coverage ledger.

## Decisions still open from the original inspection

1. What is the smallest useful workflow contract, and how is it represented without confusing it with the FSM_COS RuntimeManifest?
2. Does the first proof need only an in-process typed contract, or a persisted manifest from day one?
3. What step types are safe and useful without executing arbitrary scripts?
4. What are cancellation, timeout, retry, idempotency, and result-retention semantics?
5. How are FSM_API static definition/instance registries cleaned up between runs?
6. Does the first proof use FSM_API directly, and what tests demonstrate the adapter is a thin wrapper rather than a parallel runtime?
7. Which source-rendering strategy creates readable excerpt images without making an optional article feature a required dependency?
8. Which external publishing destination, if any, should be supported? No provider is selected.

## Historical next action — superseded

The initial prototype recommendation was completed in subsequent work: the repository now contains typed workflow contracts, a sequential runner/coordinator, FSM_API probes, governed task lifecycle, HeadlessAi bridge, CI dispatch/reconciliation cores, and a host repository-operation adapter on feature branches. The current next evidence-producing actions are listed in [Roadmap](ROADMAP.md); do not restart the original prototype as though the runtime were still absent.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
