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
    private const string TrustedWorkflowRef = "development";
    private const string TrustedWorkflowSha = "abcdef0123456789abcdef0123456789abcdef01";
    private const string WorkflowPath = ".github/workflows/foundry-fsm-api-probe.yml";
    private const string RequiredJob = "Build, execute, and capture state evidence";

    [Fact]
    public async Task DispatchPersistsIntentAndAcknowledgmentBeforeReturningReceipt()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith(
                "/actions/workflows/foundry-fsm-api-probe.yml/dispatches", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var journalPath = NewJournalPath();
        var dispatcher = CreateDispatcher(handler, journalPath);

        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());

        Assert.Equal("task-123", receipt.TaskId);
        Assert.Equal(Repository, receipt.Repository);
        Assert.Equal(Branch, receipt.Branch);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Equal(TrustedWorkflowRef, receipt.TrustedWorkflowRef);
        Assert.Equal(TrustedWorkflowSha, receipt.TrustedWorkflowSha);
        Assert.Equal("foundry-fsm-api-probe.yml", receipt.WorkflowFile);
        Assert.Equal(WorkflowPath, receipt.WorkflowPath);
        Assert.Equal(32, receipt.RequestId.Length);
        var post = Assert.Single(handler.Requests.Where(item => item.Method == HttpMethod.Post));
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(TrustedWorkflowRef, body.RootElement.GetProperty("ref").GetString());
        var inputs = body.RootElement.GetProperty("inputs");
        Assert.Equal(receipt.RequestId, inputs.GetProperty("request_id").GetString());
        Assert.Equal(new string('1', 32), inputs.GetProperty("operation_id").GetString());
        Assert.Equal(new string('2', 32), inputs.GetProperty("idempotency_key").GetString());
        Assert.Equal("task-123", inputs.GetProperty("task_id").GetString());
        Assert.Equal("correlation-123", inputs.GetProperty("correlation_id").GetString());
        Assert.NotEqual(receipt.RequestId, inputs.GetProperty("operation_id").GetString());
        Assert.Equal(Branch, inputs.GetProperty("candidate_ref").GetString());
        Assert.Equal(CandidateSha, inputs.GetProperty("candidate_sha").GetString());
        Assert.Equal(TrustedWorkflowSha, inputs.GetProperty("trusted_workflow_sha").GetString());

        var reopened = new JsonlGitHubActionsDispatchJournal(journalPath);
        var durable = reopened.Get(receipt.RequestId);
        Assert.Equal(GitHubActionsDispatchState.Acknowledged, durable.State);
        Assert.Equal(new string('1', 32), durable.OperationId);
        Assert.Equal(new string('2', 32), durable.IdempotencyKey);
        Assert.Equal("correlation-123", durable.CorrelationId);
        var recoveredReceipt = CreateDispatcher(new StubHandler((_, _) => Json(HttpStatusCode.NotFound, "{}")), journalPath)
            .GetDispatchReceipt(receipt.RequestId);
        Assert.Equal(receipt.RequestId, recoveredReceipt.RequestId);
    }

    [Fact]
    public async Task AmbiguousDispatchResponseRecoversByRequestIdWithoutResending()
    {
        var journalPath = NewJournalPath();
        var capturedRequestId = string.Empty;
        var firstHandler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && path.EndsWith("/dispatches", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                capturedRequestId = body.RootElement.GetProperty("inputs").GetProperty("request_id").GetString()!;
                // Simulate GitHub accepting the dispatch while the response is lost in transit.
                return Json(HttpStatusCode.InternalServerError, "{}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var firstDispatcher = CreateDispatcher(firstHandler, journalPath);

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await firstDispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context()));
        Assert.False(string.IsNullOrWhiteSpace(capturedRequestId));
        Assert.Equal(GitHubActionsDispatchState.Dispatching,
            new JsonlGitHubActionsDispatchJournal(journalPath).Get(capturedRequestId).State);

        var recoveryHandler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 1,
                    workflow_runs = new[] { new
                    {
                        id = 765432L, run_attempt = 1,
                        html_url = "https://github.com/TrentBest/Sample/actions/runs/765432",
                        status = "completed", conclusion = "success",
                        head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                        @event = "workflow_dispatch", display_title = "Foundry CI " + capturedRequestId
                    }}
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var recoveryDispatcher = CreateDispatcher(recoveryHandler, journalPath);
        var recoveredReceipt = recoveryDispatcher.GetDispatchReceipt(capturedRequestId);
        var recoveredByOperation = recoveryDispatcher.GetDispatchReceiptForOperation(new string('1', 32));
        Assert.Equal(capturedRequestId, recoveredByOperation.RequestId);

        var correlation = await recoveryDispatcher.FindCorrelatedRunAsync(recoveredReceipt);

        Assert.NotNull(correlation);
        Assert.Equal(765432L, correlation.RunId);
        Assert.Equal(GitHubActionsDispatchState.Correlated,
            new JsonlGitHubActionsDispatchJournal(journalPath).Get(capturedRequestId).State);
        Assert.DoesNotContain(recoveryHandler.Requests, item => item.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DispatchWithoutPersistedExecutionContextFailsClosedBeforeNetwork()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var dispatcher = CreateDispatcher(handler, NewJournalPath());

#pragma warning disable CS0618
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha));
#pragma warning restore CS0618

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DispatchRejectsBranchThatNoLongerPointsAtAuthorizedSha()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """
            {"ref":"refs/heads/agents/task-123","object":{"type":"commit","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """));
        var dispatcher = CreateDispatcher(handler, NewJournalPath());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context()));
        Assert.DoesNotContain(handler.Requests, item => item.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExactRunAttemptAndRequiredJobsAreCorrelatedAndDurablyVerified()
    {
        var journalPath = NewJournalPath();
        var correlationRequestId = new string('0', 32);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && path.EndsWith("/dispatches", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Get && path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 2,
                    workflow_runs = new[]
                    {
                        new
                        {
                            id = 123456L, run_attempt = 2,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/123456",
                            status = "completed", conclusion = "success",
                            head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                            @event = "workflow_dispatch", display_title = "Foundry CI " + correlationRequestId
                        },
                        new
                        {
                            id = 123455L, run_attempt = 1,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/123455",
                            status = "completed", conclusion = "success",
                            head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                            @event = "workflow_dispatch", display_title = "Foundry CI unrelated-request"
                        }
                    }
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 123456L, run_attempt = 2, path = WorkflowPath,
                    @event = "workflow_dispatch", head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha,
                    display_title = "Foundry CI " + correlationRequestId,
                    status = "completed", conclusion = "success"
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/attempts/2/jobs", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 1,
                    jobs = new[]
                    {
                        new { id = 999L, run_id = 123456L, run_attempt = 2, name = RequiredJob,
                            status = "completed", conclusion = "success" }
                    }
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler, journalPath);
        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());
        correlationRequestId = receipt.RequestId;

        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        Assert.NotNull(correlation);
        Assert.Equal(123456L, correlation.RunId);
        Assert.Equal(2, correlation.RunAttempt);
        Assert.Equal(TrustedWorkflowSha, correlation.HeadSha);
        Assert.Equal(TrustedWorkflowRef, correlation.HeadBranch);
        Assert.Equal(WorkflowPath, correlation.WorkflowPath);
        Assert.Equal("success", correlation.Conclusion);
        Assert.Contains("head_sha=" + TrustedWorkflowSha, handler.Requests.Single(item => item.Method == HttpMethod.Get &&
            item.Uri.Contains("/actions/workflows/", StringComparison.Ordinal)).Uri);

        var evidence = await dispatcher.VerifyRequiredJobsAsync(receipt, correlation!, [RequiredJob]);

        Assert.True(evidence.AllRequiredJobsSucceeded);
        Assert.Equal(123456L, evidence.RunId);
        Assert.Equal(2, evidence.RunAttempt);
        Assert.True(Assert.Single(evidence.RequiredJobs).Succeeded);
        Assert.Equal(GitHubActionsDispatchState.Verified,
            new JsonlGitHubActionsDispatchJournal(journalPath).Get(receipt.RequestId).State);
    }

    [Fact]
    public async Task FailedOrMissingRequiredJobDoesNotVerifyDispatch()
    {
        var journalPath = NewJournalPath();
        var correlationRequestId = new string('0', 32);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && path.EndsWith("/dispatches", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Get && path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 1,
                    workflow_runs = new[] { new
                    {
                        id = 123456L, run_attempt = 1,
                        html_url = "https://github.com/TrentBest/Sample/actions/runs/123456",
                        status = "completed", conclusion = "failure",
                        head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                        @event = "workflow_dispatch", display_title = "Foundry CI " + correlationRequestId
                    }}
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/attempts/1", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 123456L, run_attempt = 1, path = WorkflowPath,
                    @event = "workflow_dispatch", head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha,
                    display_title = "Foundry CI " + correlationRequestId,
                    status = "completed", conclusion = "failure"
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/attempts/1/jobs", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """
                    {"total_count":1,"jobs":[{"id":999,"run_id":123456,"run_attempt":1,
                     "name":"Build, execute, and capture state evidence",
                     "status":"completed","conclusion":"failure"}]}
                    """);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler, journalPath);
        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());
        correlationRequestId = receipt.RequestId;
        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        var evidence = await dispatcher.VerifyRequiredJobsAsync(receipt, correlation!, [RequiredJob]);

        Assert.False(evidence.AllRequiredJobsSucceeded);
        Assert.False(Assert.Single(evidence.RequiredJobs).Succeeded);
        Assert.Equal(GitHubActionsDispatchState.Correlated,
            new JsonlGitHubActionsDispatchJournal(journalPath).Get(receipt.RequestId).State);
    }

    [Fact]
    public async Task UnresolvedDispatchCannotBeSilentlyRepeated()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json(HttpStatusCode.OK, """{"total_count":1,"workflow_runs":[]}""");
        });
        var dispatcher = CreateDispatcher(handler, NewJournalPath());
        await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context()));
        Assert.Single(handler.Requests.Where(item => item.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task CorrelationReturnsNullWhenOnlyWrongShaOrRequestIdRunsExist()
    {
        var journalPath = NewJournalPath();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json(HttpStatusCode.OK, """
                {"total_count":1,"workflow_runs":[
                  {"id":1,"run_attempt":1,"html_url":"https://github.com/TrentBest/Sample/actions/runs/1",
                   "status":"completed","conclusion":"success","head_branch":"development",
                   "head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                   "path":".github/workflows/foundry-fsm-api-probe.yml","event":"workflow_dispatch",
                   "display_title":"Foundry CI unrelated"}
                ]}
                """);
        });
        var dispatcher = CreateDispatcher(handler, journalPath);
        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());

        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        Assert.Null(correlation);
    }

    [Fact]
    public async Task CorrelationRejectsCandidateWorkflowRefAndForeignWorkflowPath()
    {
        var requestId = string.Empty;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(path);
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                requestId = body.RootElement.GetProperty("inputs").GetProperty("request_id").GetString()!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 2,
                    workflow_runs = new[]
                    {
                        new
                        {
                            id = 101L, run_attempt = 1,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/101",
                            status = "completed", conclusion = "success",
                            head_branch = Branch, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                            @event = "workflow_dispatch", display_title = "Foundry CI " + requestId
                        },
                        new
                        {
                            id = 102L, run_attempt = 1,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/102",
                            status = "completed", conclusion = "success",
                            head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha,
                            path = ".github/workflows/foreign.yml",
                            @event = "workflow_dispatch", display_title = "Foundry CI " + requestId
                        }
                    }
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler, NewJournalPath());
        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());

        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        Assert.Null(correlation);
    }

    [Fact]
    public async Task ExactAttemptVerificationRejectsWrongTrustedWorkflowAttemptShaAndNumber()
    {
        var requestId = string.Empty;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return GitRef(path);
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                requestId = body.RootElement.GetProperty("inputs").GetProperty("request_id").GetString()!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    total_count = 1,
                    workflow_runs = new[]
                    {
                        new
                        {
                            id = 123L, run_attempt = 1,
                            html_url = "https://github.com/TrentBest/Sample/actions/runs/123",
                            status = "completed", conclusion = "success",
                            head_branch = TrustedWorkflowRef, head_sha = TrustedWorkflowSha, path = WorkflowPath,
                            @event = "workflow_dispatch", display_title = "Foundry CI " + requestId
                        }
                    }
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/attempts/1", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 123L, run_attempt = 2, path = WorkflowPath,
                    @event = "workflow_dispatch", head_branch = TrustedWorkflowRef,
                    head_sha = CandidateSha, display_title = "Foundry CI " + requestId,
                    status = "completed", conclusion = "success"
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var dispatcher = CreateDispatcher(handler, NewJournalPath());
        var receipt = await dispatcher.DispatchAsync("task-123", Repository, "proof", Branch, CandidateSha, Context());
        var correlation = await dispatcher.FindCorrelatedRunAsync(receipt);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await dispatcher.VerifyRequiredJobsAsync(receipt, correlation!, [RequiredJob]));
        Assert.DoesNotContain(handler.Requests, item => item.Uri.Contains("/jobs?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownWorkflowKeyIsRejectedBeforeAnyGitHubRequest()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var dispatcher = CreateDispatcher(handler, NewJournalPath());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await dispatcher.DispatchAsync("task-123", Repository, "arbitrary-workflow", Branch, CandidateSha, Context()));
        Assert.Empty(handler.Requests);
    }

    private static TheSingularityWorkshop.Foundry.RepositoryOperationExecutionContext Context() =>
        new(new string('1', 32), new string('2', 32), "task-123", "correlation-123");

    private static GitHubActionsWorkflowDispatcher CreateDispatcher(StubHandler handler, string journalPath)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var repositories = new GitHubRepositoryOperationPolicy([Repository]);
        var workflows = new GitHubActionsWorkflowAllowlist([
            new GitHubActionsWorkflowDefinition("proof", "foundry-fsm-api-probe.yml", WorkflowPath)
        ]);
        return new GitHubActionsWorkflowDispatcher(client, new TestTokenProvider(), repositories,
            workflows, new JsonlGitHubActionsDispatchJournal(journalPath), TrustedWorkflowSha);
    }

    private static string NewJournalPath() => Path.Combine(Path.GetTempPath(),
        "FoundryActionsTests", Guid.NewGuid().ToString("N") + ".jsonl");

    private static HttpResponseMessage GitRef(string path)
    {
        var trusted = path.Contains("development", StringComparison.Ordinal);
        var branch = trusted ? TrustedWorkflowRef : Branch;
        var sha = trusted ? TrustedWorkflowSha : CandidateSha;
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            @ref = "refs/heads/" + branch,
            @object = new { type = "commit", sha }
        }));
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
