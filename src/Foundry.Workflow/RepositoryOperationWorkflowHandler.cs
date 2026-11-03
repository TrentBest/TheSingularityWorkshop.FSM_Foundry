using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Data-only request shape consumed by the host-registered repository workflow handler.
/// Capability identifiers are intentionally absent; the host resolves them from its own state.
/// </summary>
public sealed record RepositoryOperationWorkflowRequest(
    string Operation,
    string TaskId,
    string Repository,
    string Branch,
    string CandidateSha,
    string? Path = null,
    string? Content = null,
    string? Message = null,
    string? WorkflowKey = null);

/// <summary>
/// Registers the narrow repository-operation bridge in the existing WorkflowHandlerRegistry.
/// The workflow supplies a request, while the host supplies capability resolution and time.
/// </summary>
public static class RepositoryOperationWorkflowHandler
{
    public const string HandlerKey = "repository.operation";
    public const string InputKey = "repositoryOperation";
    public const string OutputKey = "repositoryOperationResult";

    public static void Register(
        WorkflowHandlerRegistry handlers,
        AuthorizedRepositoryOperationDispatcher dispatcher,
        Func<string, Guid> capabilityResolver,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        clock ??= static () => DateTimeOffset.UtcNow;

        handlers.Register(HandlerKey, async (context, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            var request = context.GetInput<RepositoryOperationWorkflowRequest>(InputKey)
                ?? throw new InvalidOperationException("Repository operation workflow input is required.");
            var operation = CreateOperation(request);
            var capabilityId = capabilityResolver(operation.TaskId);
            if (capabilityId == Guid.Empty)
                throw new UnauthorizedAccessException("The host did not resolve an active repository capability.");

            var result = await dispatcher.ExecuteAsync(capabilityId, operation, clock(), cancellationToken)
                .ConfigureAwait(false);
            context.SetOutput(OutputKey, result);
        });
    }

    private static RepositoryOperation CreateOperation(RepositoryOperationWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Operation switch
        {
            "read-file" when request.Path is not null =>
                new ReadRepositoryFileOperation(request.TaskId, request.Repository, request.Branch,
                    request.CandidateSha, request.Path),
            "write-file" when request.Path is not null && request.Content is not null =>
                new WriteRepositoryFileOperation(request.TaskId, request.Repository, request.Branch,
                    request.CandidateSha, request.Path, request.Content),
            "create-commit" when request.Message is not null =>
                new CreateRepositoryCommitOperation(request.TaskId, request.Repository, request.Branch,
                    request.CandidateSha, request.Message),
            "request-ci" when request.WorkflowKey is not null =>
                new RequestRepositoryCiOperation(request.TaskId, request.Repository, request.Branch,
                    request.CandidateSha, request.WorkflowKey),
            _ => throw new ArgumentException("Repository operation request is not a recognized, complete operation.", nameof(request))
        };
    }
}
