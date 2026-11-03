using TheSingularityWorkshop.FSM_API;
using FsmEngine = TheSingularityWorkshop.FSM_API.FSM_API;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Uses FSM_API as the authoritative transition engine for retained workflow-run lifecycle.
/// Each coordinator owns a unique processing group and definition; contexts remain Foundry-owned.
/// </summary>
internal sealed class FsmApiWorkflowLifecycle : IDisposable
{
    private static readonly object RegistryGate = new();
    private readonly string _processingGroup = $"Foundry.Workflow.{Guid.NewGuid():N}";
    private readonly string _definitionName = $"Foundry.Workflow.Lifecycle.{Guid.NewGuid():N}";
    private readonly Dictionary<Guid, (FSMHandle Handle, WorkflowRunLifecycleContext Context)> _runs = new();
    private bool _disposed;

    public FsmApiWorkflowLifecycle()
    {
        lock (RegistryGate)
        {
            FsmEngine.Create.CreateFiniteStateMachine(_definitionName, processRate: -1,
                    processingGroup: _processingGroup)
                .State(nameof(WorkflowRunStatus.Accepted), null, null, null)
                .State(nameof(WorkflowRunStatus.Running), null, null, null)
                .State(nameof(WorkflowRunStatus.Succeeded), null, null, null)
                .State(nameof(WorkflowRunStatus.Failed), null, null, null)
                .State(nameof(WorkflowRunStatus.Cancelled), null, null, null)
                .State(nameof(WorkflowRunStatus.TimedOut), null, null, null)
                .State(nameof(WorkflowRunStatus.Invalid), null, null, null)
                .WithInitialState(nameof(WorkflowRunStatus.Accepted))
                .Transition(nameof(WorkflowRunStatus.Accepted), nameof(WorkflowRunStatus.Running),
                    context => IsRequested(context, WorkflowRunStatus.Running))
                .Transition(nameof(WorkflowRunStatus.Accepted), nameof(WorkflowRunStatus.Cancelled),
                    context => IsRequested(context, WorkflowRunStatus.Cancelled))
                .Transition(nameof(WorkflowRunStatus.Running), nameof(WorkflowRunStatus.Succeeded),
                    context => IsRequested(context, WorkflowRunStatus.Succeeded))
                .Transition(nameof(WorkflowRunStatus.Running), nameof(WorkflowRunStatus.Failed),
                    context => IsRequested(context, WorkflowRunStatus.Failed))
                .Transition(nameof(WorkflowRunStatus.Running), nameof(WorkflowRunStatus.Cancelled),
                    context => IsRequested(context, WorkflowRunStatus.Cancelled))
                .Transition(nameof(WorkflowRunStatus.Running), nameof(WorkflowRunStatus.TimedOut),
                    context => IsRequested(context, WorkflowRunStatus.TimedOut))
                .Transition(nameof(WorkflowRunStatus.Running), nameof(WorkflowRunStatus.Invalid),
                    context => IsRequested(context, WorkflowRunStatus.Invalid))
                .BuildDefinition();
        }
    }

    public void CreateRun(Guid runId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runs.ContainsKey(runId))
            throw new InvalidOperationException($"Workflow run '{runId}' already has an FSM_API lifecycle handle.");

        var context = new WorkflowRunLifecycleContext(runId);
        lock (RegistryGate)
        {
            var handle = FsmEngine.Create.CreateInstance(_definitionName, context, _processingGroup);
            if (!StringComparer.Ordinal.Equals(handle.CurrentState, nameof(WorkflowRunStatus.Accepted)))
            {
                context.IsValid = false;
                FsmEngine.Interaction.DestroyInstance(handle);
                throw new InvalidOperationException("FSM_API did not initialize the workflow run in Accepted.");
            }

            _runs.Add(runId, (handle, context));
        }
    }

    public WorkflowRunStatus GetStatus(Guid runId)
    {
        lock (RegistryGate)
        {
            var handle = GetRun(runId).Handle;
            return Enum.TryParse<WorkflowRunStatus>(handle.CurrentState, ignoreCase: false, out var status)
                ? status
                : throw new InvalidOperationException($"FSM_API returned unknown workflow lifecycle state '{handle.CurrentState}'.");
        }
    }

    public void Transition(Guid runId, WorkflowRunStatus target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (handle, context) = GetRun(runId);
        var current = GetStatus(runId);
        if (!IsAllowed(current, target))
            throw new InvalidOperationException($"Workflow run '{runId}' cannot transition from '{current}' to '{target}'.");

        context.RequestedState = target.ToString();
        try
        {
            lock (RegistryGate)
                FsmEngine.Interaction.Update(_processingGroup);
        }
        finally
        {
            context.RequestedState = null;
        }

        if (!StringComparer.Ordinal.Equals(handle.CurrentState, target.ToString()))
            throw new InvalidOperationException(
                $"FSM_API did not apply workflow lifecycle transition '{current}' -> '{target}'.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (RegistryGate)
        {
            foreach (var (handle, context) in _runs.Values)
            {
                // IsValid suspends FSM computation; it does not destroy or own the context.
                context.IsValid = false;
                FsmEngine.Interaction.DestroyInstance(handle);
            }

            if (FsmEngine.Interaction.Exists(_definitionName, _processingGroup))
                FsmEngine.Interaction.DestroyFiniteStateMachine(_definitionName, _processingGroup);
            _disposed = true;
        }
    }

    private (FSMHandle Handle, WorkflowRunLifecycleContext Context) GetRun(Guid runId) =>
        _runs.TryGetValue(runId, out var run)
            ? run
            : throw new KeyNotFoundException($"No FSM_API lifecycle handle exists for workflow run '{runId}'.");

    private static bool IsRequested(IStateContext context, WorkflowRunStatus target) =>
        context is WorkflowRunLifecycleContext lifecycle &&
        StringComparer.Ordinal.Equals(lifecycle.RequestedState, target.ToString());

    private static bool IsAllowed(WorkflowRunStatus current, WorkflowRunStatus target) =>
        (current, target) switch
        {
            (WorkflowRunStatus.Accepted, WorkflowRunStatus.Running or WorkflowRunStatus.Cancelled) => true,
            (WorkflowRunStatus.Running, WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or
                WorkflowRunStatus.Cancelled or WorkflowRunStatus.TimedOut or WorkflowRunStatus.Invalid) => true,
            _ => false
        };
}

internal sealed class WorkflowRunLifecycleContext(Guid runId) : IStateContext
{
    public string Name { get; set; } = $"Foundry workflow run {runId:D}";
    public bool IsValid { get; set; } = true;
    public string? RequestedState { get; set; }
}
