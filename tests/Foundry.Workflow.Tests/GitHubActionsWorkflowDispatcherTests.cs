using System.Net;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubActionsWorkflowDispatcherTests
{
    private const string Repository = "TrentBest/Sample";
    private const string Branch = "agents/task-123";
    private const string CandidateSha = "0123456789abcdef0123456789abcdef01234567";
    private const string WorkflowPath = ".github/workflows/foundry-fsm-api-probe.yml";

    [Fact]
    public async Task DispatchUsesOnlyAllowlistedWorkflowAndExactCandidateRef()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/" + Branch,
                    @object = new { type = "commit", sha = CandidateSha }
                }));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith(
                "/actions/workflows/foundry-fsm-api-probe.yml/dispatches", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler);

        var receipt = await dispatcher.DispatchAsync(Repository, "proof", Branch, CandidateSha);

        Assert.Equal(Repository, receipt.Repository);
        Assert.Equal(Branch, receipt.Branch);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Equal("foundry-fsm-api-probe.yml", receipt.WorkflowFile);
        Assert.Equal(WorkflowPath, receipt.WorkflowPath);
        Assert.Equal(32, receipt.RequestId.Length);
        var post = Assert.Single(handler.Requests.Where(item => item.Method == HttpMethod.Post));
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(Branch, body.RootElement.GetProperty("ref").GetString());
        var inputs = body.RootElement.GetProperty("inputs");
        Assert.Equal(receipt.RequestId, inputs.GetProperty("foundry_request_id").GetString());
        Assert.Equal(CandidateSha, inputs.GetProperty("candidate_sha").GetString());
    }

    [Fact]
    public async Task DispatchRejectsBranchThatNoLongerPointsAtAuthorizedSha()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"ref":"refs/heads/agents/task-123","object":{"type":"commit","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """));
        var dispatcher = CreateDispatcher(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync(Repository, "proof", Branch, CandidateSha));
        Assert.DoesNotContain(handler.Requests, item => item.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CorrelationRequiresRequestIdWorkflowEventBranchAndExactSha()
    {
        var receipt = new GitHubActionsDispatchReceipt(
            new string('a', 32), Repository, "foundry-fsm-api-probe.yml",
            WorkflowPath, Branch, CandidateSha, DateTimeOffset.UtcNow);
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    workflow_runs = new[]
                    {
                        new
                        {
                            id = 123456L,
                            run_attempt = 2,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/123456",
                            status = "completed",
                            conclusion = "success",
                            head_branch = Branch,
                            head_sha = CandidateSha,
                            path = WorkflowPath,
                            @event = "workflow_dispatch",
                            display_title = "Foundry CI " + receipt.RequestId
                        },
                        new
                        {
                            id = 123455L,
                            run_attempt = 1,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/123455",
                            status = "completed",
                            conclusion = "success",
                            head_branch = Branch,
                            head_sha = CandidateSha,
                            path = WorkflowPath,
                            @event = "workflow_dispatch",
                            display_title = "Foundry CI unrelated-request"
                        }
                    }
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler);

        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        Assert.NotNull(correlation);
        Assert.Equal(123456L, correlation.RunId);
        Assert.Equal(2, correlation.RunAttempt);
        Assert.Equal(CandidateSha, correlation.HeadSha);
        Assert.Equal(Branch, correlation.HeadBranch);
        Assert.Equal(WorkflowPath, correlation.WorkflowPath);
        Assert.Equal("success", correlation.Conclusion);
        Assert.Contains("head_sha=" + CandidateSha, handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task CorrelationReturnsNullWhenOnlyWrongShaOrRequestIdRunsExist()
    {
        var receipt = new GitHubActionsDispatchReceipt(
            new string('b', 32), Repository, "foundry-fsm-api-probe.yml",
            WorkflowPath, Branch, CandidateSha, DateTimeOffset.UtcNow);
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"workflow_runs":[
              {"id":1,"run_attempt":1,"html_url":"https://github.com/TrentBest/Sample/actions/runs/1",
               "status":"completed","conclusion":"success","head_branch":"agents/task-123",
               "head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
               "path":".github/workflows/foundry-fsm-api-probe.yml","event":"workflow_dispatch",
               "display_title":"Foundry CI unrelated"}
            ]}
            """));
        var dispatcher = CreateDispatcher(handler);

        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        Assert.Null(correlation);
    }

    [Fact]
    public async Task UnknownWorkflowKeyIsRejectedBeforeAnyGitHubRequest()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var dispatcher = CreateDispatcher(handler);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await dispatcher.DispatchAsync(Repository, "arbitrary-workflow", Branch, CandidateSha));
        Assert.Empty(handler.Requests);
    }

    private static GitHubActionsWorkflowDispatcher CreateDispatcher(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var repositories = new GitHubRepositoryOperationPolicy([Repository]);
        var workflows = new GitHubActionsWorkflowAllowlist([
            new GitHubActionsWorkflowDefinition("proof", "foundry-fsm-api-probe.yml", WorkflowPath)
        ]);
        return new GitHubActionsWorkflowDispatcher(client, new TestTokenProvider(), repositories, workflows);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class TestTokenProvider : IGitHubAccessTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult("test-token");
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(request.Method, request.RequestUri!.AbsoluteUri, body));
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(HttpMethod Method, string Uri, string? Body);
}
