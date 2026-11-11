<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# WorkflowExecutionCoordinator — Source Companion

## Purpose

`WorkflowExecutionCoordinator` retains each accepted workflow run and exposes its lifecycle to a host without replacing the sequential `WorkflowRunner`. It is the first host-neutral seam for the Hub to submit work, start it, observe state, request cancellation, and wait for cleanup.

## Why it exists

The runner is a single awaited invocation. A Hub presentation or headless host needs a run identity and snapshots while a handler is still running. The coordinator owns that lifecycle state; the runner continues to own workflow validation, ordered step invocation, and step/workflow outcomes.

## Public contract

- `Submit(WorkflowDefinition)` accepts work and returns an `Accepted` snapshot without invoking handlers.
- `StartAsync(runId)` starts an accepted run once without a workflow-level timeout.
- `StartAsync(runId, timeout)` optionally applies a positive finite timeout or `Timeout.InfiniteTimeSpan`; timeout is reported as `WorkflowStatus.TimedOut` / `WorkflowRunStatus.TimedOut`.
- `GetSnapshot(runId)` returns a point-in-time immutable snapshot.
- `Cancel(runId)` requests cooperative cancellation, including cancellation before start.
- `DisposeAsync()` cancels outstanding work and waits for its completion.

The coordinator is in-process and in-memory. It does not provide durable run history, a persisted queue, retries, or a cross-process transport. `Submit(definition, context)` retains the supplied context reference without cloning or freezing it; any holder can still call `SetOutput` after acceptance. Hosts must treat submission as an ownership handoff and stop mutating/sharing the context. That handoff is currently a host convention, not an enforced API invariant. Timeouts request cooperative cancellation; they do not forcibly stop handlers that ignore their cancellation token. If caller cancellation and timeout are both requested before the terminal lifecycle transition, caller cancellation takes precedence. Timeout and linked cancellation sources are released after execution; caller-cancellation sources remain until coordinator disposal.

## Verification

See [WorkflowExecutionCoordinatorTests](../../tests/Foundry.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs) for lifecycle, cancellation, finite/infinite timeout, invalid timeout values, caller-cancellation precedence, non-cooperative handlers, invalid workflows, and single-start behavior. The current implementation is present on `development` at `dc1324f9bfcc61e6de32f7c52d7686025976f00b`; exact-head development CI [run #41987903337](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41987903337) passed.

## UML

See the [WorkflowExecutionCoordinator neighborhood](../../docs/UML/WorkflowExecutionCoordinatorUML.md).

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
