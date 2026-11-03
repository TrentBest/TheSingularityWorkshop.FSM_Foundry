<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Workflow Semantics — Version 1

**Status:** implementation-backed theory for the first Foundry runtime slice. The sequential runner and timeout semantics have prior CI evidence; the FSM_API-backed production lifecycle and integration tests passed exact-head CI run [#41713120380](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41713120380) on head `8041e76e088d8d7f8965a3443a685ddcc7c0a3f3`.

## The execution model

A workflow definition is data. It identifies the workflow, declares schema version 1, and lists ordered steps. Each step names a handler key; it does not contain code. The host registers the operations it is willing to expose through a WorkflowHandlerRegistry.

The existing `WorkflowRunner` remains the single authority for validating and executing sequential steps. `WorkflowExecutionCoordinator` adds the retained Hub-facing lifecycle around that runner: accept a definition, start it once, query an immutable status snapshot, request cancellation, and dispose the coordinator. Its lifecycle states are backed by real FSM_API definitions and handles through `FsmApiWorkflowLifecycle`; it does not implement a parallel lifecycle FSM, another step runner, or another scheduler.

```mermaid
flowchart TD
    A[Submit definition] --> B[Accepted run snapshot]
    B --> C[Explicit Start]
    C --> D[Validate whole definition]
    D -->|Invalid| X[Invalid result; no handlers invoked]
    D -->|Valid| E[Execute steps in declared order]
    E --> F{Step outcome}
    F -->|Succeeded| G{More steps?}
    G -->|Yes| E
    G -->|No| S[Workflow succeeded]
    F -->|Failed| H[Record failure; skip remaining steps]
    H --> R[Workflow failed]
    F -->|Cancelled| I[Record cancellation; skip remaining steps]
    I --> N[Workflow cancelled]
    C -. cancellation command .-> I
    B -. cancel before start .-> N
```

## Contract rules

1. **Validate before side effects.** Foundry checks required workflow identity/name, supported schema version, at least one step, unique step IDs, non-empty handler keys, and registration of every referenced handler before execution starts.
2. **The host owns capabilities.** A definition can name only a handler that the host registered. Registering a handler is an explicit host decision; the workflow does not grant itself filesystem, network, process, or publishing permissions.
3. **Order is semantic.** Version 1 executes steps sequentially in the declared order. It does not infer dependencies or run steps in parallel.
4. **Failure stops the sequence.** The failing step is recorded as failed and later steps are recorded as skipped. Foundry does not claim to roll back effects already performed by a handler.
5. **Cancellation is not failure.** A cancellation requested before or during a handler produces a cancelled outcome; remaining steps are skipped. Cancellation is cooperative.
6. **Invalid is not a run failure.** An invalid definition is rejected before any handler invocation and returns validation diagnostics without step results.
7. **Success is explicit.** The workflow result is successful only when every step succeeds.
8. **Errors are conservative by default.** The runtime records the exception type, not the exception message, because handler messages can accidentally contain secrets. A host may separately provide sanitized diagnostics under a deliberate policy.

## Retained execution lifecycle

`WorkflowExecutionCoordinator` exposes a host-neutral lifecycle without moving step execution out of `WorkflowRunner`. FSM_API owns the `Accepted`, `Running`, and terminal-state transitions; Foundry owns higher-level workflow meaning and asynchronous execution:

- `Submit(definition)` accepts a definition but does not execute it. The returned snapshot starts in `Accepted`.
- `StartAsync(runId)` starts an accepted run exactly once without a workflow-level timeout.
- `StartAsync(runId, timeout)` starts an accepted run with an optional positive finite timeout or `Timeout.InfiniteTimeSpan`. A finite timeout requests cooperative cancellation and produces a distinct `TimedOut` result.
- `GetSnapshot(runId)` returns an immutable snapshot with run identity, workflow identity/name, lifecycle status, timestamps, and the latest structured result when available.
- `Cancel(runId)` requests cooperative cancellation for a running run, or marks an accepted-but-not-started run cancelled. It returns false for an already terminal run.
- `DisposeAsync()` cancels accepted/running work and waits for running handlers to finish. Handlers must honor cancellation for disposal to finish promptly.

Lifecycle statuses are `Accepted`, `Running`, `Succeeded`, `Failed`, `Cancelled`, `TimedOut`, and `Invalid`. The FSM_API handle's `CurrentState` is the authoritative lifecycle state read by snapshots. The final workflow status remains the existing `WorkflowStatus`; it does not replace or reinterpret step/workflow outcomes. Foundry validates legal transition requests and FSM_API evaluates the registered transition guards. Snapshots are observations, not mutable control objects. A presentation layer may display snapshots and send commands, but it does not own the authoritative state.

This is an in-process coordinator, not a durable queue or cross-process service. Run snapshots are retained only for the coordinator's lifetime. Each coordinator owns a unique FSM_API processing group and definition; each run owns a distinct FSM_API handle/context. The adapter serializes access to FSM_API's static registry and explicitly destroys only its own handles and definition. See [FSM_API Runtime Integration](FSM_API_RUNTIME_INTEGRATION.md) for the transition graph, context ownership, source dependency, and local build setup.

## Bounded per-run execution context

The existing runner accepts an optional `WorkflowExecutionContext`; the coordinator accepts the same context when a host submits a run. When supplied, the same run-scoped context is passed to each registered handler in declared order. Existing cancellation-token-only handlers remain supported through the legacy registration overload.

The context carries JSON data only:

- Inputs are cloned when the context is constructed and can be read through `GetInput<T>` / `TryGetInput<T>`.
- Handlers can write outputs once with `SetOutput<T>`; later handlers read them through `GetOutput<T>` / `TryGetOutput<T>`.
- Inputs cannot be replaced by outputs, and duplicate output keys are rejected.
- The combined context is limited to 128 entries, 64 KiB per serialized JSON value, 1 MiB aggregate serialized value bytes, and JSON depth 32. Keys are non-empty, control-free, and at most 128 characters.
- A context is intended for one accepted run. Hosts should create a fresh context for each run; outputs are mutable run state.
- Context values do not select handlers or grant filesystem, network, process, GitHub, CI, or publishing authority. The host-owned registry remains the allowlist of operations.

Context is in-memory only and is not part of durable task or workflow journals. It is not restored after process loss and does not change sequential execution, retries, compensation, or lifecycle semantics. See the [Workflow Execution Context guide](WORKFLOW_EXECUTION_CONTEXT.md) for the API and examples.

## Outcome vocabulary

| Workflow status | Meaning |
|---|---|
| Succeeded | All declared steps completed successfully. |
| Failed | A handler failed; later steps were skipped. |
| Cancelled | Caller cancellation was requested; the current/remaining execution did not complete. |
| TimedOut | The workflow timeout elapsed and cooperative cancellation ended execution. |
| Invalid | Definition validation failed before execution. |

| Step status | Meaning |
|---|---|
| Succeeded | The registered handler returned normally. |
| Failed | The handler threw an exception not classified as requested cancellation. |
| Cancelled | The cancellation token was requested while the step was being considered/executed. |
| Skipped | The step was not invoked because an earlier step failed or execution was cancelled. |

## Deliberate limitations

- The definition is currently a typed in-memory object; there is no JSON/YAML or other persisted schema.
- The coordinator retains runs in memory only. It has no durable history, recovery after process loss, or cross-process query/command transport.
- The runtime has no retry, compensation, recovery, parallelism, or cross-process orchestration. Workflow timeouts are cooperative cancellation requests, not hard execution limits.
- A handler may perform external side effects. Validation prevents invocation for an invalid workflow but cannot make successful prior side effects transactional.
- Cancellation and timeout are cooperative: a handler must observe and honor its cancellation token. Foundry cannot forcibly stop a handler that ignores it. A timed-out run therefore remains Running until that handler returns.
- When caller cancellation and timeout are both requested before the coordinator commits the terminal state, caller cancellation takes precedence.
- Determinism is limited to the runner's declared order and outcome mapping; handler behavior and external systems may be nondeterministic.

## Source and verification

- [Definition contract](../src/Foundry.Workflow/WorkflowDefinition.cs)
- [Validation rules](../src/Foundry.Workflow/WorkflowValidator.cs)
- [Host capability registry](../src/Foundry.Workflow/WorkflowHandlerRegistry.cs)
- [Bounded execution context](../src/Foundry.Workflow/WorkflowExecutionContext.cs)
- [Context bounds and cloning tests](../tests/Foundry.Workflow.Tests/WorkflowExecutionContextTests.cs)
- [Sequential runner and outcome mapping](../src/Foundry.Workflow/WorkflowRunner.cs)
- [Retained lifecycle coordinator](../src/Foundry.Workflow/WorkflowExecutionCoordinator.cs)
- [FSM_API lifecycle adapter](../src/Foundry.Workflow/FsmApiWorkflowLifecycle.cs)
- [FSM_API runtime integration theory](FSM_API_RUNTIME_INTEGRATION.md)
- [Runner tests](../tests/Foundry.Workflow.Tests/WorkflowRunnerTests.cs)
- [Lifecycle tests](../tests/Foundry.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs)

Prior CI runs verified the original runner and timeout semantics. The direct FSM_API source dependency and production lifecycle passed exact-head [CI run #41713120380](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41713120380), including the full workflow test suite, project builds, separate-process FSM_API probe, repeatable Fox/Sheepdog scenario, and evidence artifact upload.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
