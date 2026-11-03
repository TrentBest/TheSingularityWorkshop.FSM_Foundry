using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            branch.Contains("..", StringComparison.Ordinal) ||
            branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Split('/').Any(part => part is "." or ".." || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) ||
            branch.EndsWith(".", StringComparison.Ordinal) || branch.EndsWith("/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The branch is outside the host candidate-branch namespace.");
    }

    private static string ValidateRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 ||
            repository.Any(char.IsControl))
            throw new ArgumentException("Repository must be a valid owner/name identifier.", nameof(repository));
        var segments = repository.Split('/');
        if (segments.Length != 2 || !IsGitHubSegment(segments[0]) || !IsGitHubSegment(segments[1]))
            throw new ArgumentException("Repository must be a valid owner/name identifier.", nameof(repository));
        return repository;
    }

    private static bool IsGitHubSegment(string value) =>
        value.Length is > 0 and <= 100 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}

/// <summary>
/// Concrete GitHub host adapter increment. It supports exact-base branch creation,
/// immutable-SHA bounded text reads, durable staged blob writes, and explicit single-parent
/// commits with non-force ref advancement and post-update verification. CI dispatch remains unavailable.
/// </summary>
public sealed class GitHubRepositoryOperationAdapter : IRepositoryOperationAdapter
{
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IGitHubAccessTokenProvider _tokens;
    private readonly GitHubRepositoryOperationPolicy _policy;
    private readonly IGitHubRepositoryStagingStore _stagingStore;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubRepositoryOperationAdapter(
        HttpClient httpClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy policy,
        IGitHubRepositoryStagingStore stagingStore,
        Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _stagingStore = stagingStore ?? throw new ArgumentNullException(nameof(stagingStore));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
            WriteRepositoryFileOperation write =>
                await WriteFileAsync(write, cancellationToken).ConfigureAwait(false),
            CreateRepositoryCommitOperation commit =>
                await CreateCommitAsync(commit, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                "This adapter increment supports branch creation, immutable text reads, staged writes, and explicit commits only.")
        };
    }

    private async ValueTask<RepositoryOperationResult> CreateBranchAsync(
        CreateRepositoryBranchOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        var branchRef = "refs/heads/" + operation.Branch;
        var encodedRef = Uri.EscapeDataString("heads/" + operation.Branch);

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
            !string.Equals(result.Object?.Type, "commit", StringComparison.Ordinal) ||
            !string.Equals(result.Object?.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub created a ref that does not match the authorized candidate identity.");

        return new RepositoryOperationResult(
            $"github:ref:{branchRef}", operation.CandidateSha, operation.CandidateSha);
    }

    private async ValueTask<RepositoryOperationResult> ReadFileAsync(
        ReadRepositoryFileOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        await DemandCandidateTipAsync(operation.Repository, operation.Branch,
            operation.CandidateSha, token, cancellationToken).ConfigureAwait(false);

        var encodedPath = string.Join("/", operation.Path!.Split('/').Select(Uri.EscapeDataString));
        using var file = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/contents/{encodedPath}?ref={operation.CandidateSha}",
            token, null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync<GitContentResponse>(file!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(payload.Path, operation.Path, StringComparison.Ordinal) ||
            !string.Equals(payload.Type, "file", StringComparison.Ordinal) ||
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

    private async ValueTask<RepositoryOperationResult> CreateCommitAsync(
        CreateRepositoryCommitOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        var staged = _stagingStore.FreezeBatch(operation.Repository, operation.Branch, operation.CandidateSha);
        if (staged.Count == 0)
            throw new InvalidOperationException("No staged file changes exist for this exact candidate SHA.");

        using var baseResponse = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/commits/{operation.CandidateSha}",
            token, null, cancellationToken).ConfigureAwait(false);
        var baseCommit = await ReadJsonAsync<GitCommitResponse>(baseResponse!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(baseCommit.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(baseCommit.Tree?.Sha))
            throw new InvalidDataException("GitHub did not return the exact candidate commit and tree.");
        RequireSha(baseCommit.Tree.Sha, nameof(baseCommit.Tree.Sha));

        var author = RequireSignature(baseCommit.Author, "author");
        var committer = RequireSignature(baseCommit.Committer, "committer");
        var entries = staged.Select(entry => new GitTreeEntry(entry.Path, "100644", "blob", entry.BlobSha)).ToArray();
        var treeBody = JsonSerializer.Serialize(
            new CreateTreeRequest(baseCommit.Tree.Sha, entries), JsonOptions);
        using var treeResponse = await SendAsync(HttpMethod.Post,
            $"repos/{operation.Repository}/git/trees", token, treeBody, cancellationToken).ConfigureAwait(false);
        var tree = await ReadJsonAsync<GitTreeResponse>(treeResponse!, cancellationToken).ConfigureAwait(false);
        RequireSha(tree.Sha, nameof(tree.Sha));

        // Reusing the base commit's author/committer identities and timestamps makes the
        // Git object deterministic for this exact message, parent, and tree. A retry after
        // a lost response therefore creates/retrieves the same immutable commit object.
        var commitBody = JsonSerializer.Serialize(new CreateCommitRequest(
            operation.Message, tree.Sha, [operation.CandidateSha], author, committer), JsonOptions);
        using var commitResponse = await SendAsync(HttpMethod.Post,
            $"repos/{operation.Repository}/git/commits", token, commitBody, cancellationToken).ConfigureAwait(false);
        var commit = await ReadJsonAsync<GitCommitResponse>(commitResponse!, cancellationToken).ConfigureAwait(false);
        RequireSha(commit.Sha, nameof(commit.Sha));
        return await AdvanceAndVerifyCandidateRefAsync(
            operation, commit.Sha, tree.Sha, token, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RepositoryOperationResult> AdvanceAndVerifyCandidateRefAsync(
        CreateRepositoryCommitOperation operation, string commitSha, string treeSha, string token,
        CancellationToken cancellationToken)
    {
        var branchRef = "refs/heads/" + operation.Branch;
        var lookupRef = "heads/" + operation.Branch;
        var refUrl = $"repos/{operation.Repository}/git/ref/{Uri.EscapeDataString(lookupRef)}";

        using (var currentResponse = await SendAsync(HttpMethod.Get, refUrl, token, null,
            cancellationToken).ConfigureAwait(false))
        {
            var current = await ReadJsonAsync<GitRefResponse>(currentResponse!, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Object?.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
                return await VerifyCommittedCandidateAsync(operation, branchRef, commitSha, treeSha,
                    token, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Object?.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Candidate branch moved to an unrelated commit; refusing to force-update it.");
        }

        var updateBody = JsonSerializer.Serialize(new UpdateRefRequest(commitSha, false), JsonOptions);
        try
        {
            using var updateResponse = await SendAsync(HttpMethod.Patch,
                $"repos/{operation.Repository}/git/refs/{Uri.EscapeDataString(lookupRef)}",
                token, updateBody, cancellationToken).ConfigureAwait(false);
            var updated = await ReadJsonAsync<GitRefResponse>(updateResponse!, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(updated.Ref, branchRef, StringComparison.Ordinal) ||
                !string.Equals(updated.Object?.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub ref update response does not identify the requested commit.");
        }
        catch (HttpRequestException)
        {
            // A lost response is indeterminate. Re-read the ref; if it is still at the
            // authorized parent, the same deterministic commit may be safely submitted again.
            using var afterFailure = await SendAsync(HttpMethod.Get, refUrl, token, null,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadJsonAsync<GitRefResponse>(afterFailure!, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Object?.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
                return await VerifyCommittedCandidateAsync(operation, branchRef, commitSha, treeSha,
                    token, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Object?.Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Candidate ref update outcome is ambiguous and the branch no longer has the authorized parent.");
            using var retryResponse = await SendAsync(HttpMethod.Patch,
                $"repos/{operation.Repository}/git/refs/{Uri.EscapeDataString(lookupRef)}",
                token, updateBody, cancellationToken).ConfigureAwait(false);
            var retried = await ReadJsonAsync<GitRefResponse>(retryResponse!, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(retried.Object?.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub did not confirm the retried candidate ref update.");
        }

        return await VerifyCommittedCandidateAsync(operation, branchRef, commitSha, treeSha,
            token, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RepositoryOperationResult> VerifyCommittedCandidateAsync(
        CreateRepositoryCommitOperation operation, string branchRef, string commitSha, string treeSha,
        string token, CancellationToken cancellationToken)
    {
        using var refResponse = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/ref/{Uri.EscapeDataString("heads/" + operation.Branch)}",
            token, null, cancellationToken).ConfigureAwait(false);
        var branch = await ReadJsonAsync<GitRefResponse>(refResponse!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(branch.Ref, branchRef, StringComparison.Ordinal) ||
            !string.Equals(branch.Object?.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate branch does not point to the newly created commit.");

        using var commitResponse = await SendAsync(HttpMethod.Get,
            $"repos/{operation.Repository}/git/commits/{commitSha}",
            token, null, cancellationToken).ConfigureAwait(false);
        var commit = await ReadJsonAsync<GitCommitResponse>(commitResponse!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(commit.Sha, commitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(commit.Tree?.Sha, treeSha, StringComparison.OrdinalIgnoreCase) ||
            commit.Parents is not { Length: 1 } ||
            !string.Equals(commit.Parents[0].Sha, operation.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(commit.Message, operation.Message, StringComparison.Ordinal))
            throw new InvalidDataException("The resulting commit does not preserve the exact authorized parent, tree, and message.");

        return new RepositoryOperationResult(
            $"github:commit:{commitSha}", operation.CandidateSha, commitSha);
    }

    private static GitSignatureResponse RequireSignature(
        GitSignatureResponse? signature, string name)
    {
        if (signature is null || string.IsNullOrWhiteSpace(signature.Name) ||
            string.IsNullOrWhiteSpace(signature.Email) || string.IsNullOrWhiteSpace(signature.Date) ||
            signature.Name.Length > 256 || signature.Email.Length > 320 || signature.Date.Length > 64 ||
            signature.Name.Any(char.IsControl) || signature.Email.Any(char.IsControl) ||
            signature.Date.Any(char.IsControl))
            throw new InvalidDataException($"The candidate commit has no valid {name} identity for deterministic commit creation.");
        return signature;
    }

    private async ValueTask<RepositoryOperationResult> WriteFileAsync(
        WriteRepositoryFileOperation operation, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(operation.Repository, cancellationToken).ConfigureAwait(false);
        await DemandCandidateTipAsync(operation.Repository, operation.Branch,
            operation.CandidateSha, token, cancellationToken).ConfigureAwait(false);

        var bytes = new UTF8Encoding(false, true).GetBytes(operation.Content);
        if (bytes.Length > MaximumFileBytes)
            throw new ArgumentException("UTF-8 file content exceeds the host 64 KiB byte limit.", nameof(operation));
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var body = JsonSerializer.Serialize(new CreateBlobRequest(operation.Content, "utf-8"), JsonOptions);
        using var response = await SendAsync(HttpMethod.Post,
            $"repos/{operation.Repository}/git/blobs", token, body, cancellationToken).ConfigureAwait(false);
        var blob = await ReadJsonAsync<GitBlobResponse>(response!, cancellationToken).ConfigureAwait(false);
        RequireSha(blob.Sha, nameof(blob.Sha));

        var staged = _stagingStore.Stage(new GitHubStagedBlob(
            operation.Repository, operation.Branch, operation.CandidateSha, operation.Path!,
            blob.Sha, digest, _clock()));
        return new RepositoryOperationResult(
            $"github:staged-blob:{staged.BlobSha}", operation.CandidateSha, operation.CandidateSha);
    }

    private async ValueTask DemandCandidateTipAsync(
        string repository, string branch, string candidateSha, string token,
        CancellationToken cancellationToken)
    {
        var branchRef = "refs/heads/" + branch;
        using var response = await SendAsync(HttpMethod.Get,
            $"repos/{repository}/git/ref/{Uri.EscapeDataString("heads/" + branch)}",
            token, null, cancellationToken).ConfigureAwait(false);
        var branchPayload = await ReadJsonAsync<GitRefResponse>(response!, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(branchPayload.Ref, branchRef, StringComparison.Ordinal) ||
            !string.Equals(branchPayload.Object?.Type, "commit", StringComparison.Ordinal) ||
            !string.Equals(branchPayload.Object?.Sha, candidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Candidate branch moved; the requested SHA is no longer its authorized tip.");
    }

    private static void RequireSha(string value, string parameterName)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"GitHub returned an invalid SHA for {parameterName}.");
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
        const int maximumJsonBytes = 128 * 1024;
        if (response.Content.Headers.ContentLength is > maximumJsonBytes)
            throw new InvalidDataException("GitHub response exceeds the host response limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (bounded.Length + read > maximumJsonBytes)
                throw new InvalidDataException("GitHub response exceeds the host response limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty or invalid JSON response.");
    }

    private sealed record CreateRefRequest(string Ref, string Sha);
    private sealed record CreateBlobRequest(string Content, string Encoding);
    private sealed record GitBlobResponse(string Sha);
    private sealed record GitCommitResponse(
        string Sha, GitTreeResponse? Tree, GitParentResponse[]? Parents, string? Message,
        GitSignatureResponse? Author, GitSignatureResponse? Committer);
    private sealed record GitTreeResponse(string Sha);
    private sealed record GitParentResponse(string Sha);
    private sealed record GitSignatureResponse(string? Name, string? Email, string? Date);
    private sealed record GitRefResponse(string Ref, GitObjectResponse? Object);
    private sealed record GitObjectResponse(string Sha, string Type);
    private sealed record GitContentResponse(string Path, string Type, string Encoding, string Content);
    private sealed record GitTreeEntry(string Path, string Mode, string Type, string Sha);
    private sealed record CreateTreeRequest(
        [property: JsonPropertyName("base_tree")] string BaseTree,
        IReadOnlyList<GitTreeEntry> Tree);
    private sealed record CreateCommitRequest(
        string Message, string Tree, IReadOnlyList<string> Parents,
        GitSignatureResponse Author, GitSignatureResponse Committer);
    private sealed record UpdateRefRequest(string Sha, bool Force);
}
