using System.Net;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubRepositoryOperationAdapterTests
{
    private const string CandidateSha = "0123456789abcdef0123456789abcdef01234567";
    private const string Repository = "TrentBest/Sample";

    [Fact]
    public async Task CreateBranchUsesExactBaseShaAndCandidateNamespace()
    {
        var handler = new StubGitHubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.NotFound, "{}");
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/git/commits/" + CandidateSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sha = CandidateSha }));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/git/refs", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { @ref = "refs/heads/agents/task-123", @object = new { sha = CandidateSha, type = "commit" } }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler);
        var operation = new CreateRepositoryBranchOperation(
            "task-123", Repository, "agents/task-123", CandidateSha);

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(CandidateSha, result.ResultingCandidateSha);
        var post = Assert.Single(handler.Requests.Where(request => request.Method == HttpMethod.Post));
        using var payload = JsonDocument.Parse(post.Body!);
        Assert.Equal("refs/heads/agents/task-123", payload.RootElement.GetProperty("ref").GetString());
        Assert.Equal(CandidateSha, payload.RootElement.GetProperty("sha").GetString());
        Assert.Contains("Bearer test-token", post.Authorization);
    }

    [Fact]
    public async Task ReadRequiresExactBranchTipAndReturnsBoundedUtf8Content()
    {
        const string expected = "namespace Sample;\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(expected));
        var handler = new StubGitHubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { @ref = "refs/heads/agents/task-123", @object = new { sha = CandidateSha, type = "commit" } }));
            if (request.RequestUri.AbsolutePath.Contains("/contents/src%2FExample.cs", StringComparison.OrdinalIgnoreCase) ||
                request.RequestUri.AbsolutePath.Contains("/contents/src/Example.cs", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { path = "src/Example.cs", type = "file", encoding = "base64", content = encoded }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler);
        var operation = new ReadRepositoryFileOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, "src/Example.cs");

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(expected, result.Detail);
        Assert.Equal(CandidateSha, result.ResultingCandidateSha);
        Assert.Contains("ref=" + CandidateSha, handler.Requests.Last().Uri);
    }

    [Fact]
    public async Task WriteStagesImmutableBlobAndPersistsOnlyItsDigest()
    {
        const string content = "public sealed class StagedFile {}";
        const string blobSha = "abcdef0123456789abcdef0123456789abcdef01";
        var stagingPath = Path.Combine(Path.GetTempPath(), "FoundryGitHubHostTests",
            Guid.NewGuid().ToString("N") + ".jsonl");
        var store = new JsonlGitHubRepositoryStagingStore(stagingPath);
        var handler = new StubGitHubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { @ref = "refs/heads/agents/task-123", @object = new { sha = CandidateSha, type = "commit" } }));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/git/blobs", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = blobSha }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler, store);
        var operation = new WriteRepositoryFileOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, "src/Staged.cs", content);

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(CandidateSha, result.ResultingCandidateSha);
        var staged = Assert.Single(new JsonlGitHubRepositoryStagingStore(stagingPath)
            .GetStaged(Repository, "agents/task-123", CandidateSha));
        Assert.Equal(blobSha, staged.BlobSha);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            staged.ContentSha256);
        Assert.DoesNotContain(content, File.ReadAllText(stagingPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRejectsBranchWhoseTipDiffersFromAuthorizedCandidate()
    {
        var handler = new StubGitHubHandler((_, _) =>
            Json(HttpStatusCode.OK, """{"ref":"refs/heads/agents/task-123","object":{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","type":"commit"}}"""));
        var adapter = CreateAdapter(handler);
        var operation = new ReadRepositoryFileOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, "README.md");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.ExecuteAsync(operation, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void StagingBatchFreezeSurvivesRestartAndRejectsNewWrites()
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), "FoundryGitHubHostTests",
            Guid.NewGuid().ToString("N") + ".jsonl");
        var store = new JsonlGitHubRepositoryStagingStore(stagingPath);
        var staged = new GitHubStagedBlob(
            Repository, "agents/task-123", CandidateSha, "src/Example.cs",
            "abcdef0123456789abcdef0123456789abcdef01",
            new string('a', 64), DateTimeOffset.UtcNow);
        store.Stage(staged);

        Assert.Single(store.FreezeBatch(Repository, "agents/task-123", CandidateSha));
        Assert.Equal(staged, store.Stage(staged));
        var additional = staged with
        {
            Path = "src/Additional.cs",
            BlobSha = "abcdef0123456789abcdef0123456789abcdef02",
            ContentSha256 = new string('b', 64)
        };
        Assert.Throws<InvalidOperationException>(() => store.Stage(additional));

        var reopened = new JsonlGitHubRepositoryStagingStore(stagingPath);
        Assert.Single(reopened.FreezeBatch(Repository, "agents/task-123", CandidateSha));
        Assert.Throws<InvalidOperationException>(() => reopened.Stage(additional));
        Assert.Contains("\"eventType\":\"freeze\"", File.ReadAllText(stagingPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAdvancesCandidateRefOnlyAfterBuildingSingleParentCommit()
    {
        const string baseTree = "1111111111111111111111111111111111111111";
        const string newTree = "2222222222222222222222222222222222222222";
        const string commitSha = "3333333333333333333333333333333333333333";
        const string message = "Implement staged change";
        var store = new InMemoryStagingStore();
        store.Stage(new GitHubStagedBlob(
            Repository, "agents/task-123", CandidateSha, "src/Example.cs",
            "abcdef0123456789abcdef0123456789abcdef01",
            new string('a', 64), DateTimeOffset.UtcNow));
        var refReads = 0;
        var handler = new StubGitHubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/commits/" + CandidateSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha = CandidateSha,
                    tree = new { sha = baseTree },
                    parents = Array.Empty<object>(),
                    message = "base",
                    author = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" },
                    committer = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" }
                }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = newTree }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = commitSha }));
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/agents/task-123",
                    @object = new { sha = ++refReads == 1 ? CandidateSha : commitSha, type = "commit" }
                }));
            if (request.Method == HttpMethod.Patch && path.Contains("/git/refs/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/agents/task-123",
                    @object = new { sha = commitSha, type = "commit" }
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/" + commitSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha = commitSha,
                    tree = new { sha = newTree },
                    parents = new[] { new { sha = CandidateSha } },
                    message
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler, store);
        var operation = new CreateRepositoryCommitOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, message);

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(commitSha, result.ResultingCandidateSha);
        var patch = Assert.Single(handler.Requests.Where(request => request.Method == HttpMethod.Patch));
        using var patchBody = JsonDocument.Parse(patch.Body!);
        Assert.False(patchBody.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(commitSha, patchBody.RootElement.GetProperty("sha").GetString());
        var commitPost = Assert.Single(handler.Requests.Where(request =>
            request.Method == HttpMethod.Post && request.Path.EndsWith("/git/commits", StringComparison.Ordinal)));
        using var commitBody = JsonDocument.Parse(commitPost.Body!);
        Assert.Equal(CandidateSha, commitBody.RootElement.GetProperty("parents")[0].GetString());
        Assert.Equal(message, commitBody.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CommitRetryRecognizesAlreadyAdvancedDeterministicCandidate()
    {
        const string baseTree = "1111111111111111111111111111111111111111";
        const string newTree = "2222222222222222222222222222222222222222";
        const string commitSha = "3333333333333333333333333333333333333333";
        const string message = "Retry the same commit";
        var store = new InMemoryStagingStore();
        store.Stage(new GitHubStagedBlob(
            Repository, "agents/task-123", CandidateSha, "src/Example.cs",
            "abcdef0123456789abcdef0123456789abcdef01",
            new string('a', 64), DateTimeOffset.UtcNow));
        var handler = new StubGitHubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/" + CandidateSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha = CandidateSha, tree = new { sha = baseTree },
                    author = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" },
                    committer = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" }
                }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = newTree }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = commitSha }));
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/agents/task-123",
                    @object = new { sha = commitSha, type = "commit" }
                }));
            if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/" + commitSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha = commitSha, tree = new { sha = newTree },
                    parents = new[] { new { sha = CandidateSha } }, message
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler, store);
        var operation = new CreateRepositoryCommitOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, message);

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(commitSha, result.ResultingCandidateSha);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task CommitRefusesToAdvanceWhenCandidateBranchMoved()
    {
        const string baseTree = "1111111111111111111111111111111111111111";
        const string newTree = "2222222222222222222222222222222222222222";
        const string commitSha = "3333333333333333333333333333333333333333";
        var otherSha = new string('4', 40);
        var store = new InMemoryStagingStore();
        store.Stage(new GitHubStagedBlob(
            Repository, "agents/task-123", CandidateSha, "src/Example.cs",
            "abcdef0123456789abcdef0123456789abcdef01",
            new string('a', 64), DateTimeOffset.UtcNow));
        var handler = new StubGitHubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/commits/" + CandidateSha, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha = CandidateSha, tree = new { sha = baseTree },
                    author = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" },
                    committer = new { name = "Workshop", email = "workshop@example.test", date = "2026-11-01T00:00:00Z" }
                }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = newTree }));
            if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { sha = commitSha }));
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/agents/task-123",
                    @object = new { sha = otherSha, type = "commit" }
                }));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var adapter = CreateAdapter(handler, store);
        var operation = new CreateRepositoryCommitOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, "Do not overwrite moved branch");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.ExecuteAsync(operation, CancellationToken.None));
        Assert.Empty(handler.Requests.Where(request => request.Method == HttpMethod.Patch));
    }

    [Fact]
    public async Task RequestCiOperationUsesHostAllowlistedDispatcherAndReturnsDurableRequestReference()
    {
        var handler = new StubGitHubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/git/ref/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @ref = "refs/heads/agents/task-123",
                    @object = new { sha = CandidateSha, type = "commit" }
                }));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith(
                "/actions/workflows/foundry-fsm-api-probe.yml/dispatches", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var policy = new GitHubRepositoryOperationPolicy([Repository]);
        var dispatchJournal = new JsonlGitHubActionsDispatchJournal(Path.Combine(
            Path.GetTempPath(), "FoundryGitHubHostTests", Guid.NewGuid().ToString("N") + ".dispatch.jsonl"));
        var allowlist = new GitHubActionsWorkflowAllowlist([
            new GitHubActionsWorkflowDefinition("proof", "foundry-fsm-api-probe.yml",
                ".github/workflows/foundry-fsm-api-probe.yml")
        ]);
        var actions = new GitHubActionsWorkflowDispatcher(client, new TestTokenProvider(),
            policy, allowlist, dispatchJournal);
        var adapter = new GitHubRepositoryOperationAdapter(client, new TestTokenProvider(), policy,
            new JsonlGitHubRepositoryStagingStore(Path.Combine(
                Path.GetTempPath(), "FoundryGitHubHostTests", Guid.NewGuid().ToString("N") + ".stage.jsonl")),
            actionsDispatcher: actions);
        var operation = new RequestRepositoryCiOperation(
            "task-123", Repository, "agents/task-123", CandidateSha, "proof");

        var result = await adapter.ExecuteAsync(operation, CancellationToken.None);

        Assert.Equal(CandidateSha, result.RequestedCandidateSha);
        Assert.Equal(CandidateSha, result.ResultingCandidateSha);
        Assert.StartsWith("github:actions-dispatch:", result.OperationReference, StringComparison.Ordinal);
        var dispatch = Assert.Single(handler.Requests.Where(request => request.Method == HttpMethod.Post));
        using var payload = JsonDocument.Parse(dispatch.Body!);
        var requestId = payload.RootElement.GetProperty("inputs").GetProperty("foundry_request_id").GetString()!;
        Assert.Equal("github:actions-dispatch:" + requestId, result.OperationReference);
        var journalRecord = dispatchJournal.Get(requestId);
        Assert.Equal("task-123", journalRecord.TaskId);
        Assert.Equal(GitHubActionsDispatchState.Acknowledged, journalRecord.State);
    }

    [Fact]
    public async Task RejectsRepositoriesOutsideHostAllowlist()
    {
        var handler = new StubGitHubHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var adapter = CreateAdapter(handler);
        var operation = new ReadRepositoryFileOperation(
            "task-123", "Other/Repository", "agents/task-123", CandidateSha, "README.md");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await adapter.ExecuteAsync(operation, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsCanonicalAndNonCandidateBranches()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => GitHubRepositoryOperationPolicy.DemandCandidateBranch("development"));
        Assert.Throws<UnauthorizedAccessException>(
            () => GitHubRepositoryOperationPolicy.DemandCandidateBranch("agents/../master"));
        Assert.Throws<UnauthorizedAccessException>(
            () => GitHubRepositoryOperationPolicy.DemandCandidateBranch("agents//task"));
    }

    private static GitHubRepositoryOperationAdapter CreateAdapter(
        StubGitHubHandler handler, IGitHubRepositoryStagingStore? stagingStore = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        stagingStore ??= new JsonlGitHubRepositoryStagingStore(Path.Combine(
            Path.GetTempPath(), "FoundryGitHubHostTests", Guid.NewGuid().ToString("N") + ".jsonl"));
        return new GitHubRepositoryOperationAdapter(
            client, new TestTokenProvider(),
            new GitHubRepositoryOperationPolicy([Repository]), stagingStore);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class InMemoryStagingStore : IGitHubRepositoryStagingStore
    {
        private readonly Dictionary<string, GitHubStagedBlob> _entries = new(StringComparer.Ordinal);

        public GitHubStagedBlob Stage(GitHubStagedBlob stagedBlob)
        {
            var key = stagedBlob.Repository + "|" + stagedBlob.Branch + "|" +
                stagedBlob.CandidateSha + "|" + stagedBlob.Path;
            _entries[key] = stagedBlob;
            return stagedBlob;
        }

        public IReadOnlyList<GitHubStagedBlob> GetStaged(
            string repository, string branch, string candidateSha) =>
            _entries.Values.Where(entry =>
                entry.Repository == repository && entry.Branch == branch &&
                entry.CandidateSha == candidateSha).ToArray();

        public IReadOnlyList<GitHubStagedBlob> FreezeBatch(
            string repository, string branch, string candidateSha) =>
            GetStaged(repository, branch, candidateSha);
    }

    private sealed class TestTokenProvider : IGitHubAccessTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult("test-token");
    }

    private sealed class StubGitHubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(
                request.Method, request.RequestUri!.AbsoluteUri, request.RequestUri.AbsolutePath,
                body, request.Headers.Authorization?.ToString() ?? string.Empty));
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method, string Uri, string Path, string? Body, string Authorization);
}
