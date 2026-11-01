<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 02 — Workflow Definitions and Execution Semantics

**Chapter type:** domain theory and implementation orientation  
**Evidence scope:** Foundry `development` source and tests, with feature-branch extensions identified separately.

## 01 — What: a workflow is a declaration, not a program

A workflow definition describes an intended sequence of named operations. It is data: identity, a schema version, and ordered steps. Each step names a handler key. It does not carry executable code, credentials, or an unrestricted command.

That distinction is foundational. The definition says *what operations are requested and in what order*. The host decides which operations exist by registering handlers. Foundry validates the declaration and coordinates execution through those registered operations.

Version 1 deliberately keeps the model small: one sequential path, explicit handler registration, validation before invocation, and structured outcomes. It does not infer a dependency graph or silently parallelize steps.

## 02 — Why: keep intent separate from authority

If a workflow could contain arbitrary code or select ambient machine privileges, validating its shape would not meaningfully constrain its effects. A named handler key creates an indirection point: the workflow can request an operation only if the host has registered that key.

This is an allowlist boundary, not a complete sandbox. A registered handler may still have powerful capabilities. The host must implement each handler responsibly, scope its credentials, and decide which registrations are appropriate for a particular application or task.

The separation also makes failures explainable. Foundry can distinguish an invalid declaration from a handler failure, caller cancellation, timeout, and success. A consumer does not have to infer the result from logs or a UI spinner.

## 03 — How: the version-1 contract

### The definition

`WorkflowDefinition` contains:
- a workflow ID;
- a human-readable name;
- a schema version, currently `1`;
- an immutable, ordered collection of `WorkflowStep` values.

Each step contains an ID and a handler key. The constructor snapshots the supplied step sequence into a read-only collection and rejects null step entries. Semantic validation is a separate operation.

### Preflight validation

Before invoking any handler, `WorkflowValidator` checks the complete definition:

| Diagnostic | Rejected condition |
|---|---|
| WF001 | Definition is missing |
| WF002 | Workflow ID is empty |
| WF003 | Workflow name is empty |
| WF004 | Schema version is unsupported |
| WF005 | No steps are declared |
| WF006 | A step ID is empty |
| WF007 | A step ID is duplicated |
| WF008 | A handler key is empty |
| WF009 | The host has not registered the named handler |

Validation accumulates diagnostics rather than stopping at the first malformed field. If any diagnostic exists, the runner returns `Invalid` with no step results and invokes no handlers. This is the preflight guarantee: malformed declarations do not partially execute.

### Host registration and execution

The host constructs a `WorkflowHandlerRegistry` and registers the operations it is prepared to expose. The runner resolves each step's handler key through that registry. The definition cannot add registrations to the registry.

The runner then visits steps in their declared order:

1. If execution is already failed or cancelled, record the current step as skipped.
2. If the cancellation token is already requested, record cancellation.
3. Otherwise invoke the registered handler with the run-scoped execution context and cancellation token.
4. Record success if the handler returns normally.
5. Record cancellation only when an `OperationCanceledException` occurs while the supplied token is requested.
6. Record other exceptions as a failed step, exposing the exception type rather than its arbitrary message.
7. After failure or cancellation, skip every remaining step.

The result is `Succeeded` only when all declared steps complete successfully. Foundry does not claim to undo effects already performed by a handler.

### The retained run lifecycle

`WorkflowExecutionCoordinator` adds a retained, host-facing lifecycle around the existing runner. It does not implement a second execution engine.

| Operation | Meaning |
|---|---|
| `Submit(definition)` | Accepts a run and returns an `Accepted` snapshot; no handler runs yet |
| `StartAsync(runId)` | Starts an accepted run once |
| `GetSnapshot(runId)` | Returns an immutable observation of lifecycle state |
| `Cancel(runId)` | Requests cooperative cancellation, or cancels an accepted run before start |
| `DisposeAsync()` | Cancels accepted/running work and waits for cooperative completion |

Lifecycle status and workflow result status are related but distinct contracts. The lifecycle status tells a host where the retained run is; the result explains the execution outcome and its step-level details. Snapshots are observations, not mutable control handles.

The coordinator retains snapshots only in memory for its own lifetime. It is not a durable queue, a cross-process API, or a recovery service.

### Execution context: data exchange without new authority

The runner can pass one `WorkflowExecutionContext` through the ordered handlers. It supports JSON data inputs and outputs with bounded entry count, per-value size, aggregate serialized size, and JSON depth. Inputs are cloned; outputs are written once under unique keys.

This allows one handler to produce data consumed by a later handler without turning arbitrary context values into executable instructions. Context data does not select a handler or grant filesystem, network, process, GitHub, CI, or publishing authority. The host registry remains the operation allowlist.

## 04 — See it in a minute: follow the source

Read these files in order:

1. [WorkflowDefinition.cs](../../src/Foundry.Workflow/WorkflowDefinition.cs) — data shape and immutable step collection.
2. [WorkflowValidator.cs](../../src/Foundry.Workflow/WorkflowValidator.cs) — complete preflight validation.
3. [WorkflowHandlerRegistry.cs](../../src/Foundry.Workflow/WorkflowHandlerRegistry.cs) — host-owned handler registration.
4. [WorkflowRunner.cs](../../src/Foundry.Workflow/WorkflowRunner.cs) — sequential invocation and outcome mapping.
5. [WorkflowExecutionCoordinator.cs](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.cs) — retained run lifecycle.
6. [WorkflowExecutionContext.cs](../../src/Foundry.Workflow/WorkflowExecutionContext.cs) — bounded JSON data exchange.

The normative contract and source/test links are collected in [Workflow Semantics — Version 1](../WORKFLOW_SEMANTICS.md). The [runner tests](../../tests/Foundry.Workflow.Tests/WorkflowRunnerTests.cs), [lifecycle tests](../../tests/Foundry.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs), and [context tests](../../tests/Foundry.Workflow.Tests/WorkflowExecutionContextTests.cs) are executable evidence for the behaviors they exercise.

## 05 — Evidence, limits, and practice

### Do not overread the guarantees

The current development implementation establishes a sequential, in-process execution contract. It does not provide:
- persisted workflow definitions or a versioned JSON/YAML manifest format;
- durable workflow-run snapshots or restart recovery;
- distributed scheduling or cross-process lifecycle commands;
- parallel steps or a general dependency-graph scheduler;
- automatic retries, compensation, or rollback;
- hard termination of a handler that ignores cancellation;
- a security sandbox around registered handler code.

An optional workflow timeout exists on a feature branch and has passed its feature-branch proof workflow; it is not part of the development contract described here. It requests cooperative cancellation. A handler that ignores its token can keep the run in the running state until it returns. Do not present that branch-only extension as shipped development behavior.

### Questions for the reader

1. Why does a valid handler key prove registration but not prove that the handler is safe?
2. Why must the runner validate every step before invoking the first one?
3. What is the difference between an invalid workflow and a failed workflow?
4. Why is a cancelled handler not necessarily equivalent to a rolled-back handler?
5. Why are run snapshots observations rather than authority to mutate the run?
6. Which guarantees would need redesign before steps could execute concurrently?

### Exercise

Design a workflow with three steps: inspect a source tree, build a project, and summarize the result.

For each step, write down:
- the handler key the host must register;
- the minimum input and output data exchanged through the execution context;
- the capability the handler needs, and which host component grants it;
- the validation rule that should reject a malformed declaration;
- the outcome recorded if the handler fails or observes cancellation.

Then change the second handler key to an unregistered value. Explain why the expected result is an invalid workflow with no first-step side effect—not a failed workflow after partial execution.

### Further reading

- [Workflow semantics](../WORKFLOW_SEMANTICS.md)
- [Workflow execution context](../WORKFLOW_EXECUTION_CONTEXT.md)
- [Foundry domain and ownership](../DOMAIN.md)
- [Chapter 01 — Work as an explicitly coordinated process](01-work-as-coordinated-process.md)
- [Foundry roadmap and readiness](../ROADMAP.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
