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
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { type = "file", encoding = "base64", content = encoded }));
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

    private static GitHubRepositoryOperationAdapter CreateAdapter(StubGitHubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubRepositoryOperationAdapter(
            client, new TestTokenProvider(),
            new GitHubRepositoryOperationPolicy([Repository]));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

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
