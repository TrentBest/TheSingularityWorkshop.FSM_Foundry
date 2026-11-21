using System.Net.Http.Headers;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubRepositoryOperationHostTests
{
    private const string Repository = "TrentBest/Sample";
    private const string TrustedWorkflowSha = "abcdef0123456789abcdef0123456789abcdef01";
    private static readonly string[] RequiredJobs = ["Build, execute, and capture state evidence"];

    [Fact]
    public void CreateRejectsApiClientWithAmbientAuthorization()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ambient");
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => CreateHost(api, download, dependencies));
    }

    [Fact]
    public void CreateRejectsArtifactClientWithAmbientAuthorization()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        download.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ambient");
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => CreateHost(api, download, dependencies));
    }

    [Fact]
    public void CreateComposesAdapterDispatcherProvenanceReaderAndRepairEvidenceCoordinator()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        var dependencies = Dependencies();

        var host = CreateHost(api, download, dependencies);

        Assert.NotNull(host.RepositoryOperations);
        Assert.NotNull(host.ActionsDispatcher);
        Assert.NotNull(host.ProvenanceReader);
        Assert.NotNull(host.RepairEvidence);
        Assert.NotNull(host.OnboardingAdmissionVerifier);
        Assert.NotNull(host.CreateAuthorizedRepositoryOperationDispatcher(
            new WorkTaskCapabilityRegistry(),
            new JsonlRepositoryOperationJournal(Path.Combine(dependencies.Root, "operations.jsonl"))));
    }

    [Fact]
    public void CreateRejectsTaskCoordinatorUsingDifferentRepairAttemptJournal()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        var dependencies = Dependencies();
        var otherJournal = new JsonlWorkTaskRepairAttemptJournal(
            Path.Combine(dependencies.Root, "other-attempts.jsonl"));

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, dependencies.Tasks, otherJournal,
            RequiredJobs, TimeSpan.FromMinutes(30), TrustedWorkflowSha));
    }

    [Fact]
    public void CreateRejectsApiClientOutsideFixedGitHubOrigin()
    {
        using var api = new HttpClient { BaseAddress = new Uri("https://example.com/") };
        using var download = new HttpClient();
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => CreateHost(api, download, dependencies));
    }

    [Fact]
    public void CreateRejectsMalformedTrustedWorkflowSha()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, dependencies.Tasks, dependencies.Attempts,
            RequiredJobs, TimeSpan.FromMinutes(30), "not-a-commit-sha"));
    }

    private static GitHubRepositoryOperationHost CreateHost(
        HttpClient api, HttpClient download, DependenciesBundle dependencies) =>
        GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, dependencies.Tasks, dependencies.Attempts,
            RequiredJobs, TimeSpan.FromMinutes(30), TrustedWorkflowSha);

    private static HttpClient CreateApiClient() =>
        new() { BaseAddress = new Uri("https://api.github.com/") };

    private static DependenciesBundle Dependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "FoundryHostCompositionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var attempts = new JsonlWorkTaskRepairAttemptJournal(Path.Combine(root, "attempts.jsonl"));
        var tasks = new PersistentWorkTaskCoordinator(
            new JsonlWorkTaskStore(Path.Combine(root, "tasks.jsonl")), attempts);
        return new DependenciesBundle(
            root,
            new TestTokenProvider(),
            new GitHubRepositoryOperationPolicy([Repository]),
            new GitHubActionsWorkflowAllowlist([
                new GitHubActionsWorkflowDefinition(
                    "proof", "foundry-fsm-api-probe.yml",
                    ".github/workflows/foundry-fsm-api-probe.yml")
            ]),
            new JsonlGitHubActionsDispatchJournal(Path.Combine(root, "dispatch.jsonl")),
            new JsonlGitHubRepositoryStagingStore(Path.Combine(root, "staging.jsonl")),
            tasks,
            attempts);
    }

    private sealed record DependenciesBundle(
        string Root,
        IGitHubAccessTokenProvider Tokens,
        GitHubRepositoryOperationPolicy Policy,
        GitHubActionsWorkflowAllowlist Workflows,
        JsonlGitHubActionsDispatchJournal Journal,
        JsonlGitHubRepositoryStagingStore Staging,
        PersistentWorkTaskCoordinator Tasks,
        JsonlWorkTaskRepairAttemptJournal Attempts);

    private sealed class TestTokenProvider : IGitHubAccessTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult("composition-test-token");
    }
}
