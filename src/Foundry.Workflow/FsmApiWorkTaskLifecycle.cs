using TheSingularityWorkshop.FSM_API;
using FsmEngine = TheSingularityWorkshop.FSM_API.FSM_API;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Owns the FSM_API runtime handles for work-task lifecycle. Foundry owns task data,
/// leases, evidence and authorization; FSM_API alone owns each task's operational state.
/// </summary>
internal sealed class FsmApiWorkTaskLifecycle : IDisposable
{
    private static readonly object RegistryGate = new();
    private readonly string _processingGroup = $"Foundry.WorkTask.{Guid.NewGuid():N}";
    private readonly string _definitionName = $"Foundry.WorkTask.Lifecycle.{Guid.NewGuid():N}";
    private readonly WorkTaskStatus _initialState;
    private readonly Dictionary<string, (FSMHandle Handle, WorkTaskLifecycleContext Context)> _tasks =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public FsmApiWorkTaskLifecycle() : this(WorkTaskStatus.Queued) { }

    private FsmApiWorkTaskLifecycle(WorkTaskStatus initialState)
    {
        _initialState = initialState;
        lock (RegistryGate)
        {
            var builder = FsmEngine.Create.CreateFiniteStateMachine(_definitionName, processRate: -1,
                    processingGroup: _processingGroup)
                .State(nameof(WorkTaskStatus.Queued), null, null, null)
                .State(nameof(WorkTaskStatus.Claimed), null, null, null)
                .State(nameof(WorkTaskStatus.Investigating), null, null, null)
                .State(nameof(WorkTaskStatus.Implementing), null, null, null)
                .State(nameof(WorkTaskStatus.Validating), null, null, null)
                .State(nameof(WorkTaskStatus.RepairRequired), null, null, null)
                .State(nameof(WorkTaskStatus.Blocked), null, null, null)
                .State(nameof(WorkTaskStatus.CodeRed), null, null, null)
                .State(nameof(WorkTaskStatus.ReviewReady), null, null, null)
                .State(nameof(WorkTaskStatus.HandedOff), null, null, null)
                .State(nameof(WorkTaskStatus.Stopped), null, null, null)
                .State(nameof(WorkTaskStatus.Failed), null, null, null)
                .WithInitialState(initialState.ToString());

            Add(builder, WorkTaskStatus.Queued, WorkTaskStatus.Claimed);
            Add(builder, WorkTaskStatus.Claimed, WorkTaskStatus.Investigating, WorkTaskStatus.Blocked,
                WorkTaskStatus.CodeRed, WorkTaskStatus.HandedOff, WorkTaskStatus.Stopped, WorkTaskStatus.Failed);
            Add(builder, WorkTaskStatus.Investigating, WorkTaskStatus.Implementing, WorkTaskStatus.Blocked,
                WorkTaskStatus.CodeRed, WorkTaskStatus.Stopped, WorkTaskStatus.Failed, WorkTaskStatus.Claimed);
            Add(builder, WorkTaskStatus.Implementing, WorkTaskStatus.Validating, WorkTaskStatus.Blocked,
                WorkTaskStatus.CodeRed, WorkTaskStatus.Stopped, WorkTaskStatus.Failed, WorkTaskStatus.Claimed);
            Add(builder, WorkTaskStatus.Validating, WorkTaskStatus.RepairRequired, WorkTaskStatus.ReviewReady,
                WorkTaskStatus.Blocked, WorkTaskStatus.CodeRed, WorkTaskStatus.Stopped, WorkTaskStatus.Failed,
                WorkTaskStatus.Claimed);
            Add(builder, WorkTaskStatus.RepairRequired, WorkTaskStatus.Implementing, WorkTaskStatus.Blocked,
                WorkTaskStatus.CodeRed, WorkTaskStatus.Stopped, WorkTaskStatus.Failed, WorkTaskStatus.Claimed);
            Add(builder, WorkTaskStatus.Blocked, WorkTaskStatus.Claimed, WorkTaskStatus.Stopped, WorkTaskStatus.Failed);
            Add(builder, WorkTaskStatus.CodeRed, WorkTaskStatus.Blocked, WorkTaskStatus.Stopped, WorkTaskStatus.Failed);
            Add(builder, WorkTaskStatus.ReviewReady, WorkTaskStatus.HandedOff, WorkTaskStatus.Stopped);

            builder.Transition(nameof(WorkTaskStatus.CodeRed), nameof(WorkTaskStatus.Investigating),
                context => context is WorkTaskLifecycleContext lifecycle &&
                    lifecycle.AllowApprovedRecovery &&
                    IsRequested(context, WorkTaskStatus.Investigating));

            builder.BuildDefinition();
        }
    }

    public void CreateTask(string taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(taskId) || _tasks.ContainsKey(taskId))
            throw new InvalidOperationException("A unique task identity is required for an FSM_API lifecycle handle.");

        var context = new WorkTaskLifecycleContext(taskId);
        lock (RegistryGate)
        {
            var handle = FsmEngine.Create.CreateInstance(_definitionName, context, _processingGroup);
            if (!StringComparer.Ordinal.Equals(handle.CurrentState, _initialState.ToString()))
            {
                context.IsValid = false;
                FsmEngine.Interaction.DestroyInstance(handle);
                throw new InvalidOperationException("FSM_API did not initialize the task in its configured initial state.");
            }

            _tasks.Add(taskId, (handle, context));
        }
    }

    public WorkTaskStatus GetStatus(string taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (RegistryGate)
        {
            var handle = GetTask(taskId).Handle;
            return Enum.TryParse<WorkTaskStatus>(handle.CurrentState, ignoreCase: false, out var status) &&
                Enum.IsDefined(status)
                ? status
                : throw new InvalidOperationException($"FSM_API returned unknown work-task state '{handle.CurrentState}'.");
        }
    }

    public void Transition(string taskId, WorkTaskStatus target, bool allowApprovedRecovery = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (handle, context) = GetTask(taskId);
        var current = GetStatus(taskId);
        if (current == target)
            return;

        context.RequestedState = target.ToString();
        context.AllowApprovedRecovery = allowApprovedRecovery;
        try
        {
            lock (RegistryGate)
                FsmEngine.Interaction.Update(_processingGroup);
        }
        finally
        {
            context.RequestedState = null;
            context.AllowApprovedRecovery = false;
        }

        if (!StringComparer.Ordinal.Equals(handle.CurrentState, target.ToString()))
            throw new InvalidOperationException(
                $"FSM_API rejected work-task transition '{current}' -> '{target}'.");
    }

    public void DestroyTask(string taskId)
    {
        if (!_tasks.Remove(taskId, out var task))
            return;
        lock (RegistryGate)
        {
            task.Context.IsValid = false;
            FsmEngine.Interaction.DestroyInstance(task.Handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (RegistryGate)
        {
            foreach (var (handle, context) in _tasks.Values)
            {
                context.IsValid = false;
                FsmEngine.Interaction.DestroyInstance(handle);
            }

            _tasks.Clear();
            if (FsmEngine.Interaction.Exists(_definitionName, _processingGroup))
                FsmEngine.Interaction.DestroyFiniteStateMachine(_definitionName, _processingGroup);
            _disposed = true;
        }
    }

    /// <summary>
    /// Tests one edge by constructing an FSM_API definition whose initial state is the
    /// proposed source state. This keeps durable-history validation on the same transition
    /// engine as live task execution instead of maintaining a second transition table.
    /// </summary>
    public static bool IsTransitionAllowed(WorkTaskStatus previous, WorkTaskStatus next)
    {
        if (!Enum.IsDefined(previous) || !Enum.IsDefined(next) || previous == next)
            return false;

        using var lifecycle = new FsmApiWorkTaskLifecycle(previous);
        var taskId = $"transition-probe-{Guid.NewGuid():N}";
        lifecycle.CreateTask(taskId);
        try
        {
            lifecycle.Transition(taskId, next);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private (FSMHandle Handle, WorkTaskLifecycleContext Context) GetTask(string taskId) =>
        _tasks.TryGetValue(taskId, out var task)
            ? task
            : throw new KeyNotFoundException($"No FSM_API lifecycle handle exists for task '{taskId}'.");

    private static bool IsRequested(IStateContext context, WorkTaskStatus target) =>
        context is WorkTaskLifecycleContext lifecycle &&
        StringComparer.Ordinal.Equals(lifecycle.RequestedState, target.ToString());

    private static void Add(dynamic builder, WorkTaskStatus source, params WorkTaskStatus[] targets)
    {
        foreach (var target in targets)
        {
            var captured = target;
            builder.Transition(source.ToString(), captured.ToString(),
                context => IsRequested(context, captured));
        }
    }
}

internal sealed class WorkTaskLifecycleContext(string taskId) : IStateContext
{
    public string Name { get; set; } = $"Foundry work task {taskId}";
    public bool IsValid { get; set; } = true;
    public string? RequestedState { get; set; }
    public bool AllowApprovedRecovery { get; set; }
}
