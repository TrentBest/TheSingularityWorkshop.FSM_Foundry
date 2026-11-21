using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubActionsArtifactProvenanceReaderTests
{
    private const string Repository = "TrentBest/Sample";
    private const string RequestId = "33333333333333333333333333333333";
    private const string OperationId = "11111111111111111111111111111111";
    private const string IdempotencyKey = "22222222222222222222222222222222";
    private const string CandidateSha = "0123456789abcdef0123456789abcdef01234567";
    private const string TrustedSha = "abcdef0123456789abcdef0123456789abcdef01";
    private const string Branch = "agents/task-123";
    private const string WorkflowPath = ".github/workflows/foundry-fsm-api-probe.yml";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactRunArtifactIsValidatedAndOnlyThenJournalBecomesVerified()
    {
        var journal = CreateCorrelatedJournal();
        var zip = CreateZip(ProvenanceJson());
        var api = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ArtifactListJson());
            if (path.EndsWith("/987/zip", StringComparison.Ordinal))
                return Redirect();
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var download = new StubHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("productionresultssa.blob.core.windows.net", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var reader = CreateReader(journal, api, download);

        var result = await reader.RetrieveAndVerifyAsync(Receipt(), Correlation());

        Assert.Equal(CandidateSha, result.CheckedOutHead);
        Assert.Equal(GitHubActionsDispatchState.Verified, journal.Get(RequestId).State);
        Assert.Contains(api.Requests, item => item.EndsWith("/actions/runs/123456/attempts/2", StringComparison.Ordinal));
        Assert.Contains(api.Requests, item => item.EndsWith("/actions/runs/123456/artifacts?per_page=100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingArtifactLeavesDispatchCorrelated()
    {
        var journal = CreateCorrelatedJournal();
        var api = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (request.RequestUri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"total_count":0,"artifacts":[]}""");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var reader = CreateReader(journal, api, new StubHandler((_, _) => throw new InvalidOperationException()));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.RetrieveAndVerifyAsync(Receipt(), Correlation()));

        Assert.Equal(GitHubActionsDispatchState.Correlated, journal.Get(RequestId).State);
    }

    [Fact]
    public async Task ExpiredArtifactLeavesDispatchCorrelated()
    {
        var journal = CreateCorrelatedJournal();
        var api = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (request.RequestUri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ArtifactListJson(expiry: Now.AddMinutes(-1)));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var download = new StubHandler((_, _) => throw new InvalidOperationException("Expired artifacts must not be downloaded."));
        var reader = CreateReader(journal, api, download);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.RetrieveAndVerifyAsync(Receipt(), Correlation()));

        Assert.Empty(download.Requests);
        Assert.Equal(GitHubActionsDispatchState.Correlated, journal.Get(RequestId).State);
    }

    [Fact]
    public async Task MalformedZipLeavesDispatchCorrelated()
    {
        var journal = CreateCorrelatedJournal();
        var api = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ArtifactListJson());
            if (path.EndsWith("/987/zip", StringComparison.Ordinal))
                return Redirect();
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var download = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not a zip")) });
        var reader = CreateReader(journal, api, download);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.RetrieveAndVerifyAsync(Receipt(), Correlation()));

        Assert.Equal(GitHubActionsDispatchState.Correlated, journal.Get(RequestId).State);
    }

    [Fact]
    public async Task CandidateShaMismatchInArtifactLeavesDispatchCorrelated()
    {
        var journal = CreateCorrelatedJournal();
        var bad = ProvenanceJson().Replace(CandidateSha, new string('f', 40), StringComparison.Ordinal);
        var zip = CreateZip(bad);
        var api = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ArtifactListJson());
            if (path.EndsWith("/987/zip", StringComparison.Ordinal))
                return Redirect();
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var download = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var reader = CreateReader(journal, api, download);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.RetrieveAndVerifyAsync(Receipt(), Correlation()));

        Assert.Equal(GitHubActionsDispatchState.Correlated, journal.Get(RequestId).State);
    }

    [Fact]
    public async Task DuplicateNamedArtifactsAreAmbiguousAndNeverDownloaded()
    {
        var journal = CreateCorrelatedJournal();
        var api = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts/2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ExactAttemptJson());
            if (request.RequestUri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ArtifactListJson(2));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var download = new StubHandler((_, _) => throw new InvalidOperationException("Ambiguous artifacts must not be downloaded."));
        var reader = CreateReader(journal, api, download);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.RetrieveAndVerifyAsync(Receipt(), Correlation()));

        Assert.Empty(download.Requests);
        Assert.Equal(GitHubActionsDispatchState.Correlated, journal.Get(RequestId).State);
    }

    private static GitHubActionsArtifactProvenanceReader CreateReader(
        JsonlGitHubActionsDispatchJournal journal, StubHandler apiHandler, StubHandler downloadHandler)
    {
        var api = new HttpClient(apiHandler) { BaseAddress = new Uri("https://api.github.com/") };
        var download = new HttpClient(downloadHandler);
        return new GitHubActionsArtifactProvenanceReader(api, download, new TestTokenProvider(),
            new GitHubRepositoryOperationPolicy([Repository]), journal, () => Now);
    }

    private static JsonlGitHubActionsDispatchJournal CreateCorrelatedJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), "FoundryArtifactTests", Guid.NewGuid().ToString("N") + ".jsonl");
        var journal = new JsonlGitHubActionsDispatchJournal(path);
        journal.Begin(RequestId, "task-123", Repository, "foundry-fsm-api-probe.yml", WorkflowPath,
            Branch, CandidateSha, "development", TrustedSha, OperationId, IdempotencyKey,
            "correlation-123", Now.AddMinutes(-5));
        journal.Acknowledge(RequestId, Now.AddMinutes(-4));
        journal.Correlate(RequestId, 123456, 2);
        return journal;
    }

    private static GitHubActionsDispatchReceipt Receipt() =>
        new(RequestId, "task-123", Repository, "foundry-fsm-api-probe.yml", WorkflowPath,
            Branch, CandidateSha, "development", TrustedSha, Now.AddMinutes(-4));

    private static GitHubActionsRunCorrelation Correlation() =>
        new(123456, 2, "https://github.com/TrentBest/Sample/actions/runs/123456",
            "completed", "success", "development", TrustedSha, WorkflowPath, "Foundry CI " + RequestId);

    private static string ExactAttemptJson() => JsonSerializer.Serialize(new
    {
        id = 123456L, run_attempt = 2, path = WorkflowPath, @event = "workflow_dispatch",
        head_branch = "development", head_sha = TrustedSha, display_title = "Foundry CI " + RequestId,
        status = "completed", conclusion = "success"
    });

    private static string ArtifactListJson(int count = 1, DateTimeOffset? expiry = null)
    {
        var artifacts = Enumerable.Range(0, count).Select(index => new
        {
            id = 987L + index, name = "foundry-candidate-checkout-provenance",
            size_in_bytes = 512, expired = false,
            created_at = Now.AddMinutes(-2).ToString("O"),
            expires_at = (expiry ?? Now.AddDays(10)).ToString("O"),
            workflow_run = new { id = 123456L, head_branch = "development", head_sha = TrustedSha }
        });
        return JsonSerializer.Serialize(new { total_count = count, artifacts });
    }

    private static string ProvenanceJson() => JsonSerializer.Serialize(new
    {
        schema = "foundry.candidate-checkout-provenance.v1",
        request_id = RequestId, operation_id = OperationId, idempotency_key = IdempotencyKey,
        task_id = "task-123", correlation_id = "correlation-123", repository = Repository,
        workflow_path = WorkflowPath,
        workflow_ref = Repository + "/" + WorkflowPath + "@refs/heads/development",
        event_name = "workflow_dispatch",
        trusted_workflow = new { @ref = "refs/heads/development", sha = TrustedSha },
        candidate = new { @ref = Branch, requested_sha = CandidateSha, checked_out_head = CandidateSha },
        run = new { id = 123456L, attempt = 2 }
    });

    private static byte[] CreateZip(string json)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("candidate-checkout-provenance.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(json);
        }
        return output.ToArray();
    }

    private static HttpResponseMessage Redirect()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://productionresultssa.blob.core.windows.net/container/artifact.zip?sig=temporary");
        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestTokenProvider : IGitHubAccessTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult("host-test-token");
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(responder(request, cancellationToken));
        }
    }
}
