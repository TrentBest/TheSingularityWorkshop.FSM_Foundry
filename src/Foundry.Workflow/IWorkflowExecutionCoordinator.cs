namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Host-facing command/query contract for accepting and observing Foundry workflow runs.
/// Implementations own the authoritative lifecycle; callers interact only through this contract.
/// </summary>
public interface IWorkflowExecutionCoordinator : IAsyncDisposable
{
    /// <summary>Accepts a workflow without executing it.</summary>
    WorkflowRunSnapshot Submit(WorkflowDefinition definition);

    /// <summary>Accepts a workflow and its bounded per-run data without executing it.</summary>
    WorkflowRunSnapshot Submit(WorkflowDefinition definition, WorkflowExecutionContext context);

    /// <summary>Starts an accepted run once, without a workflow-level timeout.</summary>
    Task<WorkflowResult> StartAsync(Guid runId);

    /// <summary>Starts an accepted run once with an optional cooperative timeout.</summary>
    Task<WorkflowResult> StartAsync(Guid runId, TimeSpan? timeout);

    /// <summary>Requests cancellation of an accepted or running run.</summary>
    bool Cancel(Guid runId);

    /// <summary>Returns an immutable point-in-time snapshot of a run.</summary>
    WorkflowRunSnapshot GetSnapshot(Guid runId);
}
