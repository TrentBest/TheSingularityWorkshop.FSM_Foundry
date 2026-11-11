<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# WorkflowExecutionCoordinator — UML Neighborhood

**Status:** source-grounded view of the current `development` implementation at `dc1324f9bfcc61e6de32f7c52d7686025976f00b`. The timeout outcome and cancellation-precedence behavior are implemented and tested on the canonical development branch; exact-head CI [run #41987903337](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41987903337) passed.

## Neighborhood

```mermaid
classDiagram
    direction TB
    class WorkflowExecutionCoordinator {
        +Submit(WorkflowDefinition) WorkflowRunSnapshot
        +StartAsync(Guid) Task~WorkflowResult~
        +StartAsync(Guid, TimeSpan?) Task~WorkflowResult~
        +Cancel(Guid) bool
        +GetSnapshot(Guid) WorkflowRunSnapshot
        +DisposeAsync() ValueTask
    }
    class WorkflowRunSnapshot {
        +Guid RunId
        +string WorkflowId
        +string WorkflowName
        +WorkflowRunStatus Status
        +DateTimeOffset SubmittedAt
        +DateTimeOffset StartedAt
        +DateTimeOffset CompletedAt
        +WorkflowResult Result
    }
    class WorkflowRunner {
        +RunAsync(WorkflowDefinition, CancellationToken) ValueTask~WorkflowResult~
    }
    class WorkflowDefinition
    class WorkflowHandlerRegistry
    class WorkflowResult
    class WorkflowStatus {
        Succeeded
        Failed
        Cancelled
        TimedOut
        Invalid
    }
    class WorkflowRunStatus {
        Accepted
        Running
        Succeeded
        Failed
        Cancelled
        TimedOut
        Invalid
    }
    WorkflowExecutionCoordinator --> WorkflowRunner : delegates execution
    WorkflowExecutionCoordinator --> WorkflowHandlerRegistry : through runner
    WorkflowExecutionCoordinator --> WorkflowDefinition : retains accepted definition
    WorkflowExecutionCoordinator --> WorkflowRunSnapshot : returns immutable observation
    WorkflowRunner --> WorkflowResult : produces structured outcome
```

## Lifecycle sequence

```mermaid
sequenceDiagram
    participant Host
    participant Coordinator as WorkflowExecutionCoordinator
    participant Runner as WorkflowRunner
    participant Handler as Registered handler
    Host->>Coordinator: Submit(definition)
    Coordinator-->>Host: Accepted snapshot + run ID
    Host->>Coordinator: StartAsync(run ID, optional timeout)
    Coordinator->>Runner: RunAsync(definition, cancellation token)
    Runner->>Runner: Validate complete definition
    alt Invalid definition
        Runner-->>Coordinator: Invalid WorkflowResult
    else Valid definition
        loop Declared step order
            Runner->>Handler: Invoke registered handler
            Handler-->>Runner: Complete / fail / cancel
        end
        Runner-->>Coordinator: Terminal WorkflowResult
    end
    Host->>Coordinator: GetSnapshot(run ID)
    Coordinator-->>Host: Current status and result when available
    opt Workflow timeout elapses
        Coordinator->>Runner: Request linked-token cancellation
        Note over Coordinator,Handler: Cooperative only; a non-cooperative handler can continue running
    end
    Host->>Coordinator: Cancel(run ID) [optional]
    Note over Coordinator: Caller cancellation wins if both signals are requested before terminal commit
```

## Responsibility rules

- The coordinator owns retained run identity and lifecycle state for its lifetime.
- The existing runner remains the sole sequential step executor and definition validator.
- `WorkflowResult` remains the workflow outcome; `WorkflowRunStatus` adds lifecycle states such as `Accepted` and `Running`.
- The snapshot is observational and immutable. A presentation layer must send commands through the coordinator rather than mutate state.
- The supplied `WorkflowExecutionContext` is retained by reference; the coordinator does not freeze or clone it on submission. The host must stop mutating/sharing it after acceptance. This is a convention, not an enforced ownership-transfer boundary.
- FSM_COS `RuntimeAssembly` and WebPage process-group updates are separate contracts; this diagram does not claim they are workflow definitions or results.

## Source and tests

- [Coordinator source](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.cs)
- [Source companion](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.md)
- [Lifecycle tests](../../tests/Foundry.Workflow.Tests/WorkflowExecutionCoordinatorTests.cs)
- [Workflow semantics](../WORKFLOW_SEMANTICS.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
