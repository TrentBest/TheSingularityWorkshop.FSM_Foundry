using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>Provides short-lived host-owned GitHub access tokens for one approved repository.</summary>
public interface IGitHubAccessTokenProvider
{
    ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken);
}

/// <summary>Host policy. Repository names and candidate branch namespace are never model-controlled.</summary>
public sealed class GitHubRepositoryOperationPolicy
{
    private static readonly Regex BranchPattern = new(
        @"^agents/[A-Za-z0-9][A-Za-z0-9._/-]{0,190}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HashSet<string> _repositories;

    public GitHubRepositoryOperationPolicy(IEnumerable<string> allowedRepositories)
    {
        ArgumentNullException.ThrowIfNull(allowedRepositories);
        _repositories = new HashSet<string>(allowedRepositories.Select(ValidateRepository),
            StringComparer.OrdinalIgnoreCase);
        if (_repositories.Count == 0)
            throw new ArgumentException("At least one repository must be allowlisted.", nameof(allowedRepositories));
    }

    public void DemandRepository(string repository)
    {
        if (!_repositories.Contains(ValidateRepository(repository)))
            throw new UnauthorizedAccessException("The repository is not in the host allowlist.");
    }

    public static void DemandCandidateBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 200 ||
            !BranchPattern.IsMatch(branch) || branch.Contains("//", StringComparison.Ordinal) ||
            branch.Split('/').Any(part => part is "." or "..") ||
            branch.EndsWith('.', StringComparison.Ordinal) || branch.EndsWith('/', StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The branch is outside the host candidate-branch namespace.");
    }

    private static string ValidateRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 ||
            repository.Any(char.IsControl) || repository.Count(ch => ch == '/') != 1 ||
            repository.Split('/') is not [var owner, var name] ||
            !IsGitHubSegment(owner) || !IsGitHubSegment(name))
            throw new ArgumentException("Repository must be a valid owner/name identifier.", nameof(repository));
        return repository;
    }

    private static bool IsGitHubSegment(string value) =>
        value.Length is > 0 and <= 100 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}

/// <summary>
/// First concrete GitHub host adapter increment. It supports exact-base branch creation and
/// immutable-SHA bounded text reads. Staged writes, commits, and CI dispatch remain deliberately
/// unavailable until their durable idempotency/reconciliation stores are implemented.
/// </summary>
public sealed class GitHubRepositoryOperationAdapter : IRepositoryOperationAdapter
{
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IGitHubAccessTokenProvider _tokens;
    private readonly GitHubRepositoryOperationPolicy _policy;

    public GitHubRepositoryOperationAdapter(
        HttpClient httpClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy policy)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (_httpClient.BaseAddress is null ||
            !string.Equals(_httpClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_httpClient.BaseAddress.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            _httpClient.BaseAddress.Port != 443)
            throw new ArgumentException("The GitHub API client must use the fixed https://api.github.com origin.", nameof(httpClient));
    }

    public async ValueTask<RepositoryOperationResult> ExecuteAsync(
        RepositoryOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _policy.DemandRepository(operation.Repository);
        GitHubRepositoryOperationPolicy.DemandCandidateBranch(operation.Branch);

        return operation switch
        {
            CreateRepositoryBranchOperation create =>
                await CreateBranchAsync(create, cancellationToken).ConfigureAwait(false),
            ReadRepositoryFileOperation read =>
                await ReadFileAsync(read, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                "This adapter increment supports branch creation and immutable text reads only.")
        };
    }

    private async ValueTask<RepositoryOperationResult> CreateBranchAsync(
        CreateRepositoryBranchOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        var branchRef = "refs/heads/" + operation.Branch;
        var encodedRef = Uri.EscapeDataString(branchRef);

        using (var existing = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/ref/{encodedRef}", token, null, cancellationToken,
            allowNotFound: true).ConfigureAwait(false))
        {
            if (existing is not null)
                throw new InvalidOperationException("Candidate branch already exists; automatic adoption is prohibited.");
        }

        using var baseCommit = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/commits/{operation.CandidateSha}",
            token, null, cancellationToken).ConfigureAwait(false);
        var basePayload = await ReadJsonAsync<GitCommitResponse>(baseCommit!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(basePayload.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub did not return the exact authorized base commit.");

        var body = JsonSerializer.Serialize(new CreateRefRequest(branchRef, operation.CandidateSha), JsonOptions);
        using var created = await SendAsync(HttpMethod.Post,
            $"repos/{operation.Repository}/git/refs", token, body, cancellationToken).ConfigureAwait(false);
        var result = await ReadJsonAsync<GitRefResponse>(created!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Ref, branchRef, StringComparison.Ordinal) ||
            !string.Equals(result.Object?.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub created a ref that does not match the authorized candidate identity.");

        return new RepositoryOperationResult(
            $"github:ref:{branchRef}", operation.CandidateSha, operation.CandidateSha);
    }

    private async ValueTask<RepositoryOperationResult> ReadFileAsync(
        ReadRepositoryFileOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        var branchRef = "refs/heads/" + operation.Branch;
        using var branch = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/ref/{Uri.EscapeDataString(branchRef)}",
            token, null, cancellationToken).ConfigureAwait(false);
        var branchPayload = await ReadJsonAsync<GitRefResponse>(branch!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(branchPayload.Object?.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Candidate branch moved; the requested SHA is no longer its authorized tip.");

        var encodedPath = string.Join("/", operation.Path!.Split('/').Select(Uri.EscapeDataString));
        using var file = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/contents/{encodedPath}?ref={operation.CandidateSha}",
            token, null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync<GitContentResponse>(file!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(payload.Type, "file", StringComparison.Ordinal) ||
            !string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(payload.Content))
            throw new InvalidDataException("GitHub returned an unsupported repository content representation.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload.Content);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("GitHub returned malformed base64 file content.", exception);
        }

        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Repository file exceeds the host read limit.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return new RepositoryOperationResult(
            $"github:file:{operation.CandidateSha}:{operation.Path}",
            operation.CandidateSha, operation.CandidateSha, text);
    }

    private async ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new UnauthorizedAccessException("The host token provider did not return a valid access token.");
        return token;
    }

    private async ValueTask<HttpResponseMessage?> SendAsync(
        HttpMethod method, string relativePath, string token, string? json,
        CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Foundry/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"GitHub repository operation failed with HTTP {status}.");
        }
        return response;
    }

    private static async ValueTask<T> ReadJsonAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty or invalid JSON response.");
    }

    private sealed record CreateRefRequest(string Ref, string Sha);
    private sealed record GitCommitResponse(string Sha);
    private sealed record GitRefResponse(string Ref, GitObjectResponse? Object);
    private sealed record GitObjectResponse(string Sha, string Type);
    private sealed record GitContentResponse(string Type, string Encoding, string Content);
}
