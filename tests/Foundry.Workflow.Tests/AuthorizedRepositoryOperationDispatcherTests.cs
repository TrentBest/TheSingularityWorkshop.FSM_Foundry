using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class AuthorizedRepositoryOperationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);
    private static readonly string BaseSha = new('a', 40);
    private static readonly string CandidateSha = new('b', 40);
    private const string Repository = "TrentBest/TheSingularityWorkshop.FSM_Foundry";

    [Fact]
    public async Task AuthorizedWriteReachesHostAdapterWithExactProvenance()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.WriteFiles, WorkTaskAction.ReadRepository);
        var operation = new WriteRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/Example.cs", "namespace Example;");

        var result = await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, operation,
            Now.AddMinutes(1));

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(CandidateSha, result.ResultingCandidateSha);
        Assert.Equal(1, fixture.Adapter.Calls);
        Assert.Same(operation, fixture.Adapter.LastOperation);
    }

    [Fact]
    public async Task MutationMayAdvanceCandidateWhilePreservingAuthorizedInputProvenance()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.WriteFiles);
        var nextCandidateSha = new string('d', 40);
        fixture.Adapter.ResultingCandidateSha = nextCandidateSha;
        var operation = new WriteRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/Example.cs", "namespace Example;");

        var result = await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, operation,
            Now.AddMinutes(1));

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(nextCandidateSha, result.ResultingCandidateSha);
    }

    [Fact]
    public async Task UnauthorizedActionAndOutOfScopePathNeverReachAdapter()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.ReadRepository);

        var write = new WriteRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/Example.cs", "content");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, write, Now.AddMinutes(1)));

        var readOutsideScope = new ReadRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "docs/secret.md");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, readOutsideScope, Now.AddMinutes(1)));

        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task WrongTaskRepositoryBranchAndCandidateAreRejectedBeforeTransport()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.ReadRepository);

        var wrongTask = new ReadRepositoryFileOperation("other-task", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/file.cs");
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, wrongTask, Now.AddMinutes(1)));

        var wrongRepository = new ReadRepositoryFileOperation("task-8", "Other/Repository", "work/task-8",
            CandidateSha, "src/Foundry.Workflow/file.cs");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, wrongRepository, Now.AddMinutes(1)));

        var wrongBranch = new ReadRepositoryFileOperation("task-8", Repository, "work/other",
            CandidateSha, "src/Foundry.Workflow/file.cs");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, wrongBranch, Now.AddMinutes(1)));

        var wrongCandidate = new ReadRepositoryFileOperation("task-8", Repository, "work/task-8",
            new string('c', 40), "src/Foundry.Workflow/file.cs");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, wrongCandidate, Now.AddMinutes(1)));

        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task CodeRedInvalidatesPreviouslyGrantedMutation()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.WriteFiles);
        fixture.Tasks.Transition("task-8", fixture.Task.Lease!.LeaseId,
            WorkTaskStatus.CodeRed, Now.AddMinutes(1), "Human review required.");

        var operation = new WriteRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/file.cs", "content");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(1)));
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task ExpiredLeaseInvalidatesPreviouslyGrantedMutation()
    {
        using var fixture = new Fixture(leaseDuration: TimeSpan.FromMinutes(2));
        var capability = fixture.Grant(WorkTaskAction.WriteFiles);
        var operation = new WriteRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/file.cs", "content");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(2)));
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    [Fact]
    public async Task AdapterCannotChangeCandidateProvenanceInItsResult()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.ReadRepository);
        fixture.Adapter.ResultingCandidateSha = new string('c', 40);
        var operation = new ReadRepositoryFileOperation("task-8", Repository, "work/task-8",
            CandidateSha, "src/Foundry.Workflow/file.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(1)));
        Assert.Equal(1, fixture.Adapter.Calls);
    }


    [Fact]
    public async Task ExistingWorkflowRunnerDispatchesOnlyHostResolvedRepositoryCapabilities()
    {
        using var fixture = new Fixture();
        var capability = fixture.Grant(WorkTaskAction.WriteFiles);
        var handlers = new WorkflowHandlerRegistry();
        RepositoryOperationWorkflowHandler.Register(handlers, fixture.Dispatcher,
            taskId => taskId == "task-8" ? capability.CapabilityId : Guid.Empty,
            () => Now.AddMinutes(1));
        var runner = new WorkflowRunner(handlers);
        var context = Context(new RepositoryOperationWorkflowRequest(
            "write-file", "task-8", Repository, "work/task-8", CandidateSha,
            "src/Foundry.Workflow/WorkflowExample.cs", "namespace WorkflowExample;"));

        var result = await runner.RunWithContextAsync(Definition(), context);

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Equal(1, fixture.Adapter.Calls);
        Assert.True(context.TryGetOutput<RepositoryOperationResult>(
            RepositoryOperationWorkflowHandler.OutputKey, out var operationResult));
        Assert.Equal(CandidateSha, operationResult!.RequestedCandidateSha);
        Assert.Equal(CandidateSha, operationResult.ResultingCandidateSha);
    }

    [Fact]
    public async Task WorkflowDataCannotSelectArbitraryToolsOrSupplyItsOwnCapability()
    {
        using var fixture = new Fixture();
        var handlers = new WorkflowHandlerRegistry();
        var capabilityResolverCalls = 0;
        RepositoryOperationWorkflowHandler.Register(handlers, fixture.Dispatcher,
            _ => { capabilityResolverCalls++; return Guid.NewGuid(); },
            () => Now.AddMinutes(1));
        var runner = new WorkflowRunner(handlers);
        var context = Context(new RepositoryOperationWorkflowRequest(
            "run-shell", "task-8", Repository, "work/task-8", CandidateSha,
            Message: "echo unsafe"));

        var result = await runner.RunWithContextAsync(Definition(), context);

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(0, capabilityResolverCalls);
        Assert.Equal(0, fixture.Adapter.Calls);
    }

    private static WorkflowDefinition Definition() =>
        new("repository-operation-proof", "Repository operation proof",
            WorkflowDefinition.SupportedSchemaVersion,
            new[] { new WorkflowStep("dispatch-repository-operation", RepositoryOperationWorkflowHandler.HandlerKey) });

    private static WorkflowExecutionContext Context(RepositoryOperationWorkflowRequest request)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        return new WorkflowExecutionContext(new Dictionary<string, JsonElement>
        {
            [RepositoryOperationWorkflowHandler.InputKey] = document.RootElement
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "foundry-operation-tests", Guid.NewGuid().ToString("N"));
        public PersistentWorkTaskCoordinator Tasks { get; }
        public WorkTaskSnapshot Task { get; }
        public WorkTaskCapabilityRegistry Capabilities { get; } = new();
        public FakeAdapter Adapter { get; } = new();
        public AuthorizedRepositoryOperationDispatcher Dispatcher { get; }

        public Fixture(TimeSpan? leaseDuration = null)
        {
            var store = new JsonlWorkTaskStore(Path.Combine(_directory, "tasks.jsonl"));
            Tasks = new PersistentWorkTaskCoordinator(store);
            Tasks.Submit(new WorkTaskRequest(1, "task-8", "correlation-8", Repository, 8,
                "https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/issues/8",
                BaseSha, new[] { "src/Foundry.Workflow" }, 3));
            Task = Tasks.Claim("task-8", "agent-a", leaseDuration ?? TimeSpan.FromMinutes(20), Now);
            Dispatcher = new AuthorizedRepositoryOperationDispatcher(Tasks, Capabilities, Adapter);
        }

        public WorkTaskCapability Grant(params WorkTaskAction[] actions) =>
            Capabilities.Grant(Task, "work/task-8", CandidateSha,
                new[] { "src/Foundry.Workflow" }, actions, Now, TimeSpan.FromMinutes(10));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeAdapter : IRepositoryOperationAdapter
    {
        public int Calls { get; private set; }
        public RepositoryOperation? LastOperation { get; private set; }
        public string? ResultingCandidateSha { get; set; }

        public ValueTask<RepositoryOperationResult> ExecuteAsync(
            RepositoryOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastOperation = operation;
            return ValueTask.FromResult(new RepositoryOperationResult(
                "fake-operation-1", operation.CandidateSha, ResultingCandidateSha ?? operation.CandidateSha));
        }
    }
}
