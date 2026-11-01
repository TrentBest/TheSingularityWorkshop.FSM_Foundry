namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Owns accepted workflow runs and their observable lifecycle while delegating step execution
/// to the existing <see cref="WorkflowRunner"/>.
/// </summary>
public sealed class WorkflowExecutionCoordinator : IWorkflowExecutionCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = new();
    private readonly WorkflowRunner _runner;
    private bool _disposed;

    public WorkflowExecutionCoordinator(WorkflowHandlerRegistry handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _runner = new WorkflowRunner(handlers);
    }

    /// <summary>Accepts a workflow without executing it.</summary>
    public WorkflowRunSnapshot Submit(WorkflowDefinition definition) =>
        Submit(definition, new WorkflowExecutionContext());

    /// <summary>Accepts a workflow and its bounded per-run data without executing it.</summary>
    public WorkflowRunSnapshot Submit(
        WorkflowDefinition definition, WorkflowExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            ThrowIfDisposed();
            var state = new RunState(Guid.NewGuid(), definition, context, DateTimeOffset.UtcNow);
            _runs.Add(state.RunId, state);
            return state.ToSnapshot();
        }
    }

    /// <summary>Starts an accepted run once, without a workflow-level timeout.</summary>
    public Task<WorkflowResult> StartAsync(Guid runId) => StartAsync(runId, timeout: null);

    /// <summary>
    /// Starts an accepted run once. A finite timeout requests cooperative cancellation and
    /// produces a TimedOut outcome, distinct from caller-requested cancellation.
    /// </summary>
    /// <remarks>A timeout cannot forcibly stop a handler that ignores its cancellation token.</remarks>
    public Task<WorkflowResult> StartAsync(Guid runId, TimeSpan? timeout)
    {
        if (timeout is { } duration &&
            ((duration <= TimeSpan.Zero && duration != Timeout.InfiniteTimeSpan) ||
             duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1d)))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");

        RunState state;
        lock (_gate)
        {
            ThrowIfDisposed();
            state = GetRun(runId);
            if (state.Status != WorkflowRunStatus.Accepted)
                throw new InvalidOperationException(
                    $"Run '{runId}' cannot start from status '{state.Status}'.");
            state.Status = WorkflowRunStatus.Running;
            state.StartedAt = DateTimeOffset.UtcNow;
            state.Timeout = timeout is { } limit
                ? new CancellationTokenSource(limit)
                : new CancellationTokenSource();
            state.ExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                state.Cancellation.Token, state.Timeout.Token);
        }

        _ = ExecuteAsync(state);
        return state.Completion.Task;
    }

    /// <summary>
    /// Requests cancellation of a running run, or cancels an accepted run before it starts.
    /// Returns false when the run is already terminal or the coordinator is disposing/disposed.
    /// </summary>
    public bool Cancel(Guid runId)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            var state = GetRun(runId);
            if (state.Status == WorkflowRunStatus.Accepted)
            {
                state.Result = new WorkflowResult(state.Definition.Id, WorkflowStatus.Cancelled,
                    Array.Empty<StepResult>(), Array.Empty<WorkflowDiagnostic>());
                state.Status = WorkflowRunStatus.Cancelled;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.Completion.TrySetResult(state.Result);
                return true;
            }

            if (state.Status != WorkflowRunStatus.Running)
                return false;

            // CancelAsync requests cancellation without running arbitrary callbacks under _gate.
            _ = state.Cancellation.CancelAsync();
            return true;
        }
    }

    /// <summary>Returns an immutable snapshot of the current lifecycle and latest result.</summary>
    public WorkflowRunSnapshot GetSnapshot(Guid runId)
    {
        lock (_gate)
            return GetRun(runId).ToSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        Task<WorkflowResult>[] completions;
        CancellationTokenSource[] cancellations;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var state in _runs.Values)
            {
                if (state.Status == WorkflowRunStatus.Accepted)
                {
                    state.Result = new WorkflowResult(state.Definition.Id, WorkflowStatus.Cancelled,
                        Array.Empty<StepResult>(), Array.Empty<WorkflowDiagnostic>());
                    state.Status = WorkflowRunStatus.Cancelled;
                    state.CompletedAt = DateTimeOffset.UtcNow;
                    state.Completion.TrySetResult(state.Result);
                }
            }

            cancellations = _runs.Values
                .Where(state => state.Status == WorkflowRunStatus.Running)
                .Select(state => state.Cancellation)
                .ToArray();
            completions = _runs.Values.Select(state => state.Completion.Task).ToArray();
        }

        var cancellationTasks = cancellations.Select(cancellation => cancellation.CancelAsync()).ToArray();
        try
        {
            // Wait for both cancellation callbacks and cooperative handlers before releasing resources.
            await Task.WhenAll(Task.WhenAll(cancellationTasks), Task.WhenAll(completions))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var state in _runs.Values)
                    state.Cancellation.Dispose();
            }
        }
    }

    private async Task ExecuteAsync(RunState state)
    {
        WorkflowResult result;
        try
        {
            result = await _runner.RunWithContextAsync(state.Definition, state.Context, state.ExecutionCancellation!.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Do not expose arbitrary exception messages, which may contain secrets.
            result = new WorkflowResult(state.Definition.Id, WorkflowStatus.Failed,
                Array.Empty<StepResult>(),
                new[] { new WorkflowDiagnostic("WF999",
                    $"Workflow execution failed unexpectedly with {exception.GetType().Name}.") });
        }

        lock (_gate)
        {
            // Evaluate both signals at the terminal-state boundary. If the caller has
            // requested cancellation by this point, it wins even when timeout also fired.
            if (result.Status != WorkflowStatus.Invalid &&
                state.Timeout!.IsCancellationRequested &&
                !state.Cancellation.IsCancellationRequested)
            {
                result = new WorkflowResult(result.WorkflowId, WorkflowStatus.TimedOut,
                    result.Steps, result.Diagnostics);
            }

            state.Result = result;
            state.Status = result.Status switch
            {
                WorkflowStatus.Succeeded => WorkflowRunStatus.Succeeded,
                WorkflowStatus.Failed => WorkflowRunStatus.Failed,
                WorkflowStatus.Cancelled => WorkflowRunStatus.Cancelled,
                WorkflowStatus.TimedOut => WorkflowRunStatus.TimedOut,
                WorkflowStatus.Invalid => WorkflowRunStatus.Invalid,
                _ => throw new ArgumentOutOfRangeException(nameof(result.Status))
            };
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.Completion.TrySetResult(result);
        }

        // These sources are no longer observed after the handler has completed. Keep the
        // caller-cancellation source until coordinator disposal, where cancellation races
        // with DisposeAsync are serialized under _gate.
        state.ExecutionCancellation?.Dispose();
        state.Timeout?.Dispose();
    }

    private RunState GetRun(Guid runId) =>
        _runs.TryGetValue(runId, out var state)
            ? state
            : throw new KeyNotFoundException($"No workflow run exists with ID '{runId}'.");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class RunState(
        Guid runId, WorkflowDefinition definition, WorkflowExecutionContext context,
        DateTimeOffset submittedAt)
    {
        public Guid RunId { get; } = runId;
        public WorkflowDefinition Definition { get; } = definition;
        public WorkflowExecutionContext Context { get; } = context;
        public DateTimeOffset SubmittedAt { get; } = submittedAt;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Accepted;
        public WorkflowResult? Result { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationTokenSource? Timeout { get; set; }
        public CancellationTokenSource? ExecutionCancellation { get; set; }
        public TaskCompletionSource<WorkflowResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkflowRunSnapshot ToSnapshot() => new(
            RunId, Definition.Id, Definition.Name, Status, SubmittedAt, StartedAt, CompletedAt, Result);
    }
}

/// <summary>Lifecycle status for a retained Foundry workflow run.</summary>
public enum WorkflowRunStatus
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Invalid
}

/// <summary>Immutable, presentation-neutral snapshot of a workflow run.</summary>
public sealed record WorkflowRunSnapshot(
    Guid RunId,
    string WorkflowId,
    string WorkflowName,
    WorkflowRunStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    WorkflowResult? Result);
