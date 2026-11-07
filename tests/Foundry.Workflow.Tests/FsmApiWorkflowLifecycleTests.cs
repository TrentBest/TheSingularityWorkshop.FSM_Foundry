using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class FsmApiWorkflowLifecycleTests
{
    [Fact]
    public void IllegalTransitionIsRejectedWithoutChangingFsmApiAuthoritativeState()
    {
        using var lifecycle = new FsmApiWorkflowLifecycle();
        var runId = Guid.NewGuid();
        lifecycle.CreateRun(runId);

        Assert.Equal(WorkflowRunStatus.Accepted, lifecycle.GetStatus(runId));

        // Succeeded is not an outgoing edge from Accepted. The FSM_API engine must
        // reject the request and retain Accepted as the authoritative state.
        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Transition(runId, WorkflowRunStatus.Succeeded));

        Assert.Equal(WorkflowRunStatus.Accepted, lifecycle.GetStatus(runId));

        lifecycle.Transition(runId, WorkflowRunStatus.Running);
        Assert.Equal(WorkflowRunStatus.Running, lifecycle.GetStatus(runId));
    }

    [Fact]
    public async Task SeparateCoordinatorsUseIndependentFsmApiProcessingGroups()
    {
        var firstRegistry = new WorkflowHandlerRegistry();
        firstRegistry.Register("work", _ => ValueTask.CompletedTask);
        var secondRegistry = new WorkflowHandlerRegistry();
        secondRegistry.Register("work", _ => ValueTask.CompletedTask);

        await using var first = new WorkflowExecutionCoordinator(firstRegistry);
        await using var second = new WorkflowExecutionCoordinator(secondRegistry);

        var firstRun = first.Submit(Definition("first"));
        var secondRun = second.Submit(Definition("second"));

        Assert.Equal(WorkflowRunStatus.Accepted, firstRun.Status);
        Assert.Equal(WorkflowRunStatus.Accepted, secondRun.Status);

        var results = await Task.WhenAll(
            first.StartAsync(firstRun.RunId),
            second.StartAsync(secondRun.RunId));

        Assert.All(results, result => Assert.Equal(WorkflowStatus.Succeeded, result.Status));
        Assert.Equal(WorkflowRunStatus.Succeeded, first.GetSnapshot(firstRun.RunId).Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, second.GetSnapshot(secondRun.RunId).Status);
    }

    [Fact]
    public async Task DisposingCoordinatorDestroysItsFsmApiHandlesWithoutOwningRunContext()
    {
        var registry = new WorkflowHandlerRegistry();
        registry.Register("work", _ => ValueTask.CompletedTask);
        var coordinator = new WorkflowExecutionCoordinator(registry);
        var accepted = coordinator.Submit(Definition("cleanup"));

        await coordinator.DisposeAsync();

        Assert.Equal(WorkflowRunStatus.Cancelled, coordinator.GetSnapshot(accepted.RunId).Status);
        Assert.Equal(WorkflowStatus.Cancelled, coordinator.GetSnapshot(accepted.RunId).Result!.Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.StartAsync(accepted.RunId));
    }

    private static WorkflowDefinition Definition(string id) =>
        new($"test.workflow.{id}.v1", "FSM_API lifecycle proof", 1,
            new[] { new WorkflowStep("step-1", "work") });
}
