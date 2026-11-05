using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class WorkflowExecutionCoordinatorTests
{
    [Fact]
    public async Task SubmitAcceptsWithoutExecutingUntilStarted()
    {
        var calls = 0;
        var registry = new WorkflowHandlerRegistry();
        registry.Register("work", _ => { calls++; return ValueTask.CompletedTask; });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);

        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "work")));

        Assert.Equal(WorkflowRunStatus.Accepted, accepted.Status);
        Assert.Null(accepted.StartedAt);
        Assert.Null(accepted.Result);
        Assert.Equal(0, calls);

        var result = await coordinator.StartAsync(accepted.RunId);

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        var completed = coordinator.GetSnapshot(accepted.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.StartedAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Same(result, completed.Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunningStatusCanBeObservedAndCancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowHandlerRegistry();
        registry.Register("wait", async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "wait")));

        var completion = coordinator.StartAsync(accepted.RunId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowRunStatus.Running,
            coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.True(coordinator.Cancel(accepted.RunId));

        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Equal(WorkflowRunStatus.Cancelled,
            coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.False(coordinator.Cancel(accepted.RunId));
    }

    [Fact]
    public async Task WorkflowTimeoutProducesTimedOutOutcomeDistinctFromCallerCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowHandlerRegistry();
        registry.Register("wait", async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "wait")));

        var completion = coordinator.StartAsync(accepted.RunId, TimeSpan.FromMilliseconds(100));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowStatus.TimedOut, result.Status);
        Assert.Equal(StepStatus.Cancelled, Assert.Single(result.Steps).Status);
        Assert.Equal(WorkflowRunStatus.TimedOut,
            coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.False(coordinator.Cancel(accepted.RunId));
    }

    [Fact]
    public async Task CallerCancellationWinsWhenBothCancellationSignalsAreRequested()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowHandlerRegistry();
        registry.Register("wait", async token =>
        {
            entered.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "wait")));

        var completion = coordinator.StartAsync(accepted.RunId, TimeSpan.FromMilliseconds(100));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // Ensure the workflow timeout has fired.
        Assert.True(coordinator.Cancel(accepted.RunId));
        release.TrySetResult();

        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Equal(WorkflowRunStatus.Cancelled,
            coordinator.GetSnapshot(accepted.RunId).Status);
    }

    [Fact]
    public async Task TimeoutDoesNotForceStopAHandlerThatIgnoresCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowHandlerRegistry();
        registry.Register("wait", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "wait")));

        var completion = coordinator.StartAsync(accepted.RunId, TimeSpan.FromMilliseconds(100));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // Allow the timeout request to fire.

        Assert.False(completion.IsCompleted);
        Assert.Equal(WorkflowRunStatus.Running,
            coordinator.GetSnapshot(accepted.RunId).Status);

        release.TrySetResult();
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowStatus.TimedOut, result.Status);
        Assert.Equal(WorkflowRunStatus.TimedOut,
            coordinator.GetSnapshot(accepted.RunId).Status);
    }

    [Fact]
    public async Task StartRejectsInvalidTimeoutWithoutChangingAcceptedRun()
    {
        var registry = new WorkflowHandlerRegistry();
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "missing")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.StartAsync(accepted.RunId, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.StartAsync(accepted.RunId, TimeSpan.MaxValue));

        Assert.Equal(WorkflowRunStatus.Accepted,
            coordinator.GetSnapshot(accepted.RunId).Status);
    }

    [Fact]
    public async Task InfiniteTimeoutAllowsWorkflowToCompleteNormally()
    {
        var registry = new WorkflowHandlerRegistry();
        registry.Register("work", _ => ValueTask.CompletedTask);
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "work")));

        var result = await coordinator.StartAsync(accepted.RunId, Timeout.InfiniteTimeSpan);

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Equal(WorkflowRunStatus.Succeeded,
            coordinator.GetSnapshot(accepted.RunId).Status);
    }

    [Fact]
    public async Task AcceptedRunCanBeCancelledWithoutInvokingHandlers()
    {
        var calls = 0;
        var registry = new WorkflowHandlerRegistry();
        registry.Register("work", _ => { calls++; return ValueTask.CompletedTask; });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "work")));

        Assert.True(coordinator.Cancel(accepted.RunId));

        var snapshot = coordinator.GetSnapshot(accepted.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, snapshot.Status);
        Assert.Equal(WorkflowStatus.Cancelled, snapshot.Result!.Status);
        Assert.Null(snapshot.StartedAt);
        Assert.NotNull(snapshot.CompletedAt);
        Assert.Equal(0, calls);
        Assert.False(coordinator.Cancel(accepted.RunId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(accepted.RunId));
    }

    [Fact]
    public async Task InvalidWorkflowIsAcceptedThenReportsInvalidOnStart()
    {
        var registry = new WorkflowHandlerRegistry();
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "not-registered")));

        var result = await coordinator.StartAsync(accepted.RunId);

        Assert.Equal(WorkflowStatus.Invalid, result.Status);
        Assert.Equal(WorkflowRunStatus.Invalid,
            coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.Contains(result.Diagnostics, item => item.Code == "WF009");
    }

    [Fact]
    public async Task CoordinatorRejectsStartingSameRunTwice()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowHandlerRegistry();
        registry.Register("wait", async token => await release.Task.WaitAsync(token));
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "wait")));

        var first = coordinator.StartAsync(accepted.RunId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(accepted.RunId));
        release.TrySetResult();
        Assert.Equal(WorkflowStatus.Succeeded, (await first).Status);
    }


    [Fact]
    public async Task SubmitCarriesContextIntoTheAcceptedRun()
    {
        using var document = System.Text.Json.JsonDocument.Parse("\"request-7\"");
        var context = new WorkflowExecutionContext(
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["requestId"] = document.RootElement
            });
        string? observed = null;
        var registry = new WorkflowHandlerRegistry();
        registry.Register("capture", (execution, _) =>
        {
            observed = execution.GetInput<string>("requestId");
            execution.SetOutput("completed", true);
            return ValueTask.CompletedTask;
        });
        await using var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(
            Definition(new WorkflowStep("step-1", "capture")), context);

        Assert.Equal(WorkflowRunStatus.Accepted, accepted.Status);
        Assert.Null(observed);

        var result = await coordinator.StartAsync(accepted.RunId);

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Equal("request-7", observed);
        Assert.True(context.GetOutput<bool>("completed"));
    }

    [Fact]
    public async Task HostFacingInterfaceExposesTheExistingWorkflowLifecycle()
    {
        var calls = 0;
        var registry = new WorkflowHandlerRegistry();
        registry.Register("work", _ => { calls++; return ValueTask.CompletedTask; });
        await using IWorkflowExecutionCoordinator coordinator =
            new WorkflowExecutionCoordinator(registry);

        var accepted = coordinator.Submit(Definition(new WorkflowStep("step-1", "work")));
        Assert.Equal(WorkflowRunStatus.Accepted, accepted.Status);
        Assert.Equal(0, calls);

        var result = await coordinator.StartAsync(accepted.RunId);
        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Equal(WorkflowRunStatus.Succeeded,
            coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.Equal(1, calls);
        Assert.False(coordinator.Cancel(accepted.RunId));
    }

    private static WorkflowDefinition Definition(params WorkflowStep[] steps) =>
        new("test.workflow.v1", "Test workflow", 1, steps);
}
