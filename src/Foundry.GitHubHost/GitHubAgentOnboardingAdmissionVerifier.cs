using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Retrieves the current development ref, reads the root onboarding document by immutable commit SHA,
/// and delegates all contract interpretation to CiAi's authoritative AgentOnboardingValidator.
/// </summary>
public sealed class GitHubAgentOnboardingAdmissionVerifier : IAgentOnboardingAdmissionVerifier
{
    private const int MaximumJsonBytes = 128 * 1024;
    private const string RequiredRef = "refs/heads/development";
    private readonly HttpClient _apiClient;
    private readonly IGitHubAccessTokenProvider _tokens;
    private readonly GitHubRepositoryOperationPolicy _policy;

    public GitHubAgentOnboardingAdmissionVerifier(
        HttpClient apiClient,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy policy)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        if (_apiClient.BaseAddress is null ||
            !string.Equals(_apiClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_apiClient.BaseAddress.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            _apiClient.BaseAddress.Port != 443)
            throw new ArgumentException("The GitHub API client must use the fixed https://api.github.com origin.", nameof(apiClient));
        if (_apiClient.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("The GitHub API client must not carry ambient Authorization headers.", nameof(apiClient));
    }

    public async ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
        string repository, string targetSha, CancellationToken cancellationToken = default)
    {
        _policy.DemandRepository(repository);
        RequireSha(targetSha, nameof(targetSha));
        cancellationToken.ThrowIfCancellationRequested();

        var token = await _tokens.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new UnauthorizedAccessException("The host token provider did not return a valid access token.");

        // Admission is intentionally tied to the current development head. The subsequent
        // content read uses targetSha, never the mutable branch name.
        using var refResponse = await SendAsync(
            HttpMethod.Get, $"repos/{repository}/git/ref/heads/development",
            token, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub did not return the development ref response.");
        using var refJson = await ReadJsonAsync(refResponse, cancellationToken).ConfigureAwait(false);
        var refRoot = refJson.RootElement;
        var resolvedRef = GetString(refRoot, "ref");
        var gitObject = refRoot.GetProperty("object");
        var resolvedSha = GetString(gitObject, "sha");
        var objectType = GetString(gitObject, "type");
        if (!string.Equals(resolvedRef, RequiredRef, StringComparison.Ordinal) ||
            !string.Equals(objectType, "commit", StringComparison.Ordinal) ||
            !IsFullSha(resolvedSha))
            throw new InvalidDataException("GitHub did not resolve a valid development commit ref.");

        if (!string.Equals(resolvedSha, targetSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                "The requested onboarding target is not the repository's current development head.");

        var source = await ReadOnboardingSourceAsync(repository, targetSha, resolvedSha!,
            token, cancellationToken).ConfigureAwait(false);
        var result = AgentOnboardingValidator.Evaluate(source);
        var evidence = new AgentOnboardingAdmissionEvidence(
            result.Repository,
            result.RequestedCommitSha,
            result.Path,
            result.ContractVersion,
            result.IsEligible,
            result.Outcome,
            result.FailureCode,
            result.Reason,
            result.ContentSha256);
        return evidence.ValidateFor(repository, targetSha);
    }

    private async ValueTask<AgentOnboardingSourceObservation> ReadOnboardingSourceAsync(
        string repository, string targetSha, string resolvedSha, string token,
        CancellationToken cancellationToken)
    {
        var path = AgentOnboardingValidator.RequiredPath;
        using var response = await SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/contents/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(targetSha)}",
            token, cancellationToken, allowNotFound: true).ConfigureAwait(false);

        if (response is null)
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Missing, null);

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!string.Equals(GetString(root, "path"), path, StringComparison.Ordinal) ||
            !string.Equals(GetString(root, "type"), "file", StringComparison.Ordinal) ||
            !string.Equals(GetString(root, "encoding"), "base64", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("size", out var sizeElement) ||
            !sizeElement.TryGetInt32(out var size) || size < 0)
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Unreadable, null);

        // The validator owns the 64 KiB contract decision. This bounded sentinel lets it
        // produce the canonical exceeds-size-limit result without trusting API metadata alone.
        if (size > AgentOnboardingValidator.MaximumContentBytes)
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Available,
                new string('x', AgentOnboardingValidator.MaximumContentBytes + 1));

        var encoded = GetString(root, "content");
        if (encoded is null)
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Unreadable, null);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Unreadable, null);
        }

        if (bytes.Length != size)
            return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
                path, AgentOnboardingReadStatus.Unreadable, null);

        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Preserve a deterministic CiAi rejection for invalid UTF-8 by supplying a
            // string that is guaranteed not to satisfy the required document contract.
            content = "\uD800";
        }

        return new AgentOnboardingSourceObservation(repository, targetSha, resolvedSha,
            path, AgentOnboardingReadStatus.Available, content);
    }

    private async ValueTask<HttpResponseMessage?> SendAsync(
        HttpMethod method, string relativePath, string token,
        CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Foundry/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
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
            throw new HttpRequestException($"GitHub onboarding read failed with HTTP {status}.",
                null, (HttpStatusCode)status);
        }
        return response;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
            throw new InvalidDataException("GitHub onboarding response exceeds the host response limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (bounded.Length + read > MaximumJsonBytes)
                throw new InvalidDataException("GitHub onboarding response exceeds the host response limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void RequireSha(string value, string parameterName)
    {
        if (!IsFullSha(value))
            throw new ArgumentException("A full 40-character hexadecimal commit SHA is required.", parameterName);
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
