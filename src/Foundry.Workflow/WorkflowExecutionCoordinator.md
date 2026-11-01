<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# WorkflowExecutionCoordinator — Source Companion

## Purpose

`WorkflowExecutionCoordinator` retains each accepted workflow run and exposes its lifecycle through `IWorkflowExecutionCoordinator` without replacing the sequential `WorkflowRunner`. The interface is the narrow host-facing command/query seam for submit, start, observe, cancel, and cleanup.

## Why it exists

The runner is a single awaited invocation. A Hub presentation or headless host needs a run identity and snapshots while a handler is still running. The coordinator owns that lifecycle state; the runner continues to own workflow validation, ordered step invocation, and step/workflow outcomes.

## Public contract\n\nHosts should depend on [`IWorkflowExecutionCoordinator`](IWorkflowExecutionCoordinator.cs), implemented by `WorkflowExecutionCoordinator`. The interface deliberately mirrors the tested lifecycle methods; it does not introduce transport, persistence, scheduling, retries, or authorization.

- `Submit(WorkflowDefinition)` accepts work and returns an `Accepted` snapshot without invoking handlers.
- `StartAsync(runId)` starts an accepted run once without a workflow-level timeout.
- `StartAsync(runId, timeout)` optionally applies a positive finite timeout or `Timeout.InfiniteTimeSpan`; timeout is reported as `WorkflowStatus.TimedOut` / `WorkflowRunStatus.TimedOut`.
- `GetSnapshot(runId)` returns a point-in-time immutable snapshot.
- `Cancel(runId)` requests cooperative cancellation, including cancellation before start.
- `DisposeAsync()` cancels outstanding work and waits for its completion.

The coordinator is in-process and in-memory. It does not provide durable run history, a persisted queue, retries, or a cross-process transport. Timeouts request cooperative cancellation; they do not forcibly stop handlers that ignore their cancellation token. If caller cancellation and timeout are both requested before the terminal lifecycle transition, caller cancellation takes precedence. Timeout and linked cancellation sources are released after execution; caller-cancellation sources remain until coordinator disposal.

## Verification

See [WorkflowExecutionCoordinatorTests](../../tests/Foundry.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs) for lifecycle, cancellation, finite/infinite timeout, invalid timeout values, caller-cancellation precedence, non-cooperative handlers, invalid workflows, and single-start behavior. The feature-branch implementation passed [Foundry CI run #276](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41170698768), including the workflow test suite and build.

## UML

See the [WorkflowExecutionCoordinator neighborhood](../../docs/UML/WorkflowExecutionCoordinatorUML.md).

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
