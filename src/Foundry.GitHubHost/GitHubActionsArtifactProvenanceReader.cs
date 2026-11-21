using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Retrieves and validates the exact workflow-run artifact containing candidate checkout
/// provenance. Artifact bytes are never trusted merely because GitHub returned them.
/// </summary>
public sealed class GitHubActionsArtifactProvenanceReader
{
    private const string ArtifactName = "foundry-candidate-checkout-provenance";
    private const string ProvenanceFileName = "candidate-checkout-provenance.json";
    private const int MaximumMetadataBytes = 256 * 1024;
    private const int MaximumZipBytes = 2 * 1024 * 1024;
    private const int MaximumEntries = 8;
    private const int MaximumJsonBytes = 32 * 1024;
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _api;
    private readonly HttpClient _download;
    private readonly IGitHubAccessTokenProvider _tokens;
    private readonly GitHubRepositoryOperationPolicy _repositories;
    private readonly IGitHubActionsDispatchJournal _journal;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="api">A client rooted at https://api.github.com/ with automatic redirects disabled.</param>
    /// <param name="download">A separate client with automatic redirects disabled and no ambient credentials.</param>
    public GitHubActionsArtifactProvenanceReader(
        HttpClient api,
        HttpClient download,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy repositories,
        IGitHubActionsDispatchJournal journal,
        Func<DateTimeOffset>? clock = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (_api.DefaultRequestHeaders.Authorization is not null ||
            _download.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("API and artifact clients must not carry ambient authorization; credentials are attached only to the exact API request.", nameof(download));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_api.BaseAddress is null ||
            !string.Equals(_api.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_api.BaseAddress.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            _api.BaseAddress.Port != 443 || _api.BaseAddress.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(_api.BaseAddress.Query) || !string.IsNullOrEmpty(_api.BaseAddress.Fragment))
            throw new ArgumentException("Artifact metadata client must use the fixed https://api.github.com origin.", nameof(api));
    }

    /// <summary>
    /// Retrieves the unique artifact for the exact correlated run attempt, validates its ZIP
    /// and JSON envelope against the durable dispatch intent, then advances Correlated to Verified.
    /// Any exception leaves the journal unchanged.
    /// </summary>
    public async ValueTask<VerifiedCandidateCheckoutProvenance> RetrieveAndVerifyAsync(
        GitHubActionsDispatchReceipt receipt,
        GitHubActionsRunCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(correlation);
        _repositories.DemandRepository(receipt.Repository);
        DemandReceipt(receipt, correlation);
        var durable = _journal.Get(receipt.RequestId);
        DemandDurableBinding(durable, receipt, correlation);
        if (durable.State is not (GitHubActionsDispatchState.Correlated or GitHubActionsDispatchState.Verified))
            throw new InvalidOperationException("Candidate provenance can be retrieved only after exact run correlation.");

        var token = await _tokens.GetTokenAsync(receipt.Repository, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new UnauthorizedAccessException("The host token provider did not return a valid access token.");

        await VerifyExactAttemptAsync(receipt, correlation, token, cancellationToken).ConfigureAwait(false);
        var artifactId = await FindUniqueArtifactAsync(receipt, correlation, token, cancellationToken).ConfigureAwait(false);
        var zip = await DownloadArtifactZipAsync(receipt, artifactId, token, cancellationToken).ConfigureAwait(false);
        var json = ExtractProvenanceJson(zip);
        var provenance = ValidateProvenance(json, durable, receipt, correlation);
        _journal.Verify(receipt.RequestId, correlation.RunId, correlation.RunAttempt, provenance);
        return provenance;
    }

    private async ValueTask VerifyExactAttemptAsync(
        GitHubActionsDispatchReceipt receipt, GitHubActionsRunCorrelation correlation,
        string token, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get,
            $"repos/{receipt.Repository}/actions/runs/{correlation.RunId}/attempts/{correlation.RunAttempt}", token);
        using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        var run = document.RootElement;
        if (!run.TryGetProperty("id", out var id) || !id.TryGetInt64(out var runId) || runId != correlation.RunId ||
            !run.TryGetProperty("run_attempt", out var attempt) || !attempt.TryGetInt32(out var runAttempt) ||
            runAttempt != correlation.RunAttempt ||
            String(run, "path") != receipt.WorkflowPath ||
            String(run, "event") != "workflow_dispatch" ||
            String(run, "head_branch") != receipt.TrustedWorkflowRef ||
            !string.Equals(String(run, "head_sha"), receipt.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            String(run, "display_title") != "Foundry CI " + receipt.RequestId ||
            String(run, "status") != "completed" || String(run, "conclusion") != "success")
            throw new InvalidDataException("GitHub exact run-attempt metadata is missing, unsuccessful, or mismatched.");
    }
    private async ValueTask<long> FindUniqueArtifactAsync(
        GitHubActionsDispatchReceipt receipt, GitHubActionsRunCorrelation correlation,
        string token, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get,
            $"repos/{receipt.Repository}/actions/runs/{correlation.RunId}/artifacts?per_page=100", token);
        using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("total_count", out var totalValue) || !totalValue.TryGetInt32(out var total) ||
            total < 0 || total > 100 || !root.TryGetProperty("artifacts", out var artifacts) ||
            artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() != total)
            throw new InvalidDataException("Artifact enumeration is incomplete or exceeds the host limit.");

        var matches = new List<long>();
        foreach (var item in artifacts.EnumerateArray())
        {
            if (!string.Equals(String(item, "name"), ArtifactName, StringComparison.Ordinal))
                continue;
            if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id) || id <= 0 ||
                !item.TryGetProperty("expired", out var expiredValue) ||
                expiredValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                expiredValue.GetBoolean() ||
                !item.TryGetProperty("size_in_bytes", out var sizeValue) ||
                !sizeValue.TryGetInt64(out var size) || size <= 0 || size > MaximumZipBytes ||
                !item.TryGetProperty("workflow_run", out var run) ||
                !run.TryGetProperty("id", out var runIdValue) || !runIdValue.TryGetInt64(out var runId) ||
                runId != correlation.RunId ||
                String(run, "head_branch") != receipt.TrustedWorkflowRef ||
                !string.Equals(String(run, "head_sha"), receipt.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The candidate provenance artifact metadata is expired, malformed, oversized, or bound to another run.");
            var expiresAt = Date(item, "expires_at");
            var createdAt = Date(item, "created_at");
            var now = _clock();
            if (createdAt > now || createdAt < receipt.DispatchedAt || expiresAt <= now || expiresAt <= createdAt)
                throw new InvalidDataException("The candidate provenance artifact is not currently within its retention window.");
            matches.Add(id);
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException("The exact run attempt has no candidate checkout provenance artifact."),
            _ => throw new InvalidDataException("The exact run attempt has ambiguous duplicate candidate checkout provenance artifacts.")
        };
    }

    private async ValueTask<byte[]> DownloadArtifactZipAsync(
        GitHubActionsDispatchReceipt receipt, long artifactId, string token,
        CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get,
            $"repos/{receipt.Repository}/actions/artifacts/{artifactId}/zip", token);
        using var redirect = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (redirect.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            throw new InvalidDataException("GitHub artifact download did not return one explicit temporary redirect; automatic redirect following is not accepted.");
        var location = redirect.Headers.Location;
        if (location is null || !location.IsAbsoluteUri ||
            !string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            location.Port != 443 || !string.IsNullOrEmpty(location.UserInfo) ||
            !string.IsNullOrEmpty(location.Fragment) || !IsTrustedArtifactHost(location.IdnHost))
            throw new InvalidDataException("GitHub returned an artifact redirect outside the trusted HTTPS artifact-host policy.");

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, location);
        downloadRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using var downloaded = await _download.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (downloaded.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("The signed artifact URL did not return HTTP 200 without another redirect.");
        if (downloaded.Content.Headers.ContentLength is > MaximumZipBytes)
            throw new InvalidDataException("The candidate provenance ZIP exceeds the host download limit.");
        return await ReadBoundedAsync(downloaded.Content, MaximumZipBytes, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTrustedArtifactHost(string host) =>
        host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static byte[] ExtractProvenanceJson(byte[] zipBytes)
    {
        using var input = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 1 or > MaximumEntries)
            throw new InvalidDataException("Candidate provenance ZIP has an invalid entry count.");
        var matches = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.StartsWith('/') ||
                entry.FullName.Contains('\\') ||
                entry.FullName.Split('/').Any(segment => segment is "." or ".."))
                throw new InvalidDataException("Candidate provenance ZIP contains an unsafe entry path.");
            if (string.Equals(entry.FullName, ProvenanceFileName, StringComparison.Ordinal))
                matches.Add(entry);
        }
        if (matches.Count != 1)
            throw new InvalidDataException("Candidate provenance ZIP must contain exactly one root provenance JSON file.");
        var selected = matches[0];
        if (selected.Length <= 0 || selected.Length > MaximumJsonBytes || selected.CompressedLength > MaximumZipBytes)
            throw new InvalidDataException("Candidate provenance JSON entry exceeds its uncompressed or compressed size limit.");
        using var stream = selected.Open();
        using var output = new MemoryStream((int)selected.Length);
        var buffer = new byte[4096];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumJsonBytes)
                throw new InvalidDataException("Candidate provenance JSON expands beyond the host limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != selected.Length)
            throw new InvalidDataException("Candidate provenance JSON length does not match ZIP metadata.");
        return output.ToArray();
    }

    private static VerifiedCandidateCheckoutProvenance ValidateProvenance(
        byte[] json, GitHubActionsDispatchRecord durable, GitHubActionsDispatchReceipt receipt,
        GitHubActionsRunCorrelation correlation)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var root = document.RootElement;
        DemandUniqueProperties(root, "schema", "request_id", "operation_id", "idempotency_key", "task_id",
            "correlation_id", "repository", "workflow_path", "workflow_ref", "event_name",
            "trusted_workflow", "candidate", "run");
        Demand(String(root, "schema"), "foundry.candidate-checkout-provenance.v1", "schema");
        Demand(String(root, "request_id"), receipt.RequestId, "request ID");
        Demand(String(root, "operation_id"), durable.OperationId, "operation ID");
        Demand(String(root, "idempotency_key"), durable.IdempotencyKey, "idempotency key");
        Demand(String(root, "task_id"), receipt.TaskId, "task ID");
        Demand(String(root, "correlation_id"), durable.CorrelationId, "correlation ID");
        Demand(String(root, "repository"), receipt.Repository, "repository");
        Demand(String(root, "workflow_path"), receipt.WorkflowPath, "workflow path");
        Demand(String(root, "workflow_ref"), receipt.Repository + "/" + receipt.WorkflowPath + "@refs/heads/" + receipt.TrustedWorkflowRef, "workflow ref");
        Demand(String(root, "event_name"), "workflow_dispatch", "event");

        var trusted = RequiredObject(root, "trusted_workflow");
        DemandUniqueProperties(trusted, "ref", "sha");
        Demand(String(trusted, "ref"), "refs/heads/" + receipt.TrustedWorkflowRef, "trusted workflow ref");
        DemandSha(String(trusted, "sha"), receipt.TrustedWorkflowSha, "trusted workflow SHA");

        var candidate = RequiredObject(root, "candidate");
        DemandUniqueProperties(candidate, "ref", "requested_sha", "checked_out_head");
        Demand(String(candidate, "ref"), receipt.Branch, "candidate ref");
        var requestedSha = String(candidate, "requested_sha");
        var checkedOutHead = String(candidate, "checked_out_head");
        DemandSha(requestedSha, receipt.CandidateSha, "requested candidate SHA");
        DemandSha(checkedOutHead, receipt.CandidateSha, "checked-out candidate HEAD");

        var run = RequiredObject(root, "run");
        DemandUniqueProperties(run, "id", "attempt");
        if (!run.TryGetProperty("id", out var runId) || !runId.TryGetInt64(out var actualRunId) ||
            actualRunId != correlation.RunId ||
            !run.TryGetProperty("attempt", out var attempt) || !attempt.TryGetInt32(out var actualAttempt) ||
            actualAttempt != correlation.RunAttempt)
            throw new InvalidDataException("Candidate provenance does not identify the exact GitHub run attempt.");

        return new VerifiedCandidateCheckoutProvenance(receipt.RequestId, receipt.Repository,
            correlation.RunId, correlation.RunAttempt, receipt.Branch, requestedSha, checkedOutHead,
            Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant());
    }

    private static void DemandReceipt(GitHubActionsDispatchReceipt receipt, GitHubActionsRunCorrelation correlation)
    {
        _ = receipt.Repository.Split('/');
        if (receipt.RequestId is not { Length: 32 } || !Guid.TryParseExact(receipt.RequestId, "N", out _) ||
            receipt.CandidateSha is not { Length: 40 } || !receipt.CandidateSha.All(Uri.IsHexDigit) ||
            correlation.RunId <= 0 || correlation.RunAttempt <= 0 ||
            correlation.HeadBranch != receipt.TrustedWorkflowRef ||
            !string.Equals(correlation.HeadSha, receipt.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            correlation.WorkflowPath != receipt.WorkflowPath ||
            correlation.Status != "completed" || correlation.Conclusion != "success")
            throw new UnauthorizedAccessException("Artifact retrieval requires a successful, exact correlated run attempt.");
    }

    private static void DemandDurableBinding(
        GitHubActionsDispatchRecord durable, GitHubActionsDispatchReceipt receipt,
        GitHubActionsRunCorrelation correlation)
    {
        if (durable.RequestId != receipt.RequestId || durable.TaskId != receipt.TaskId ||
            !string.Equals(durable.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
            durable.WorkflowFile != receipt.WorkflowFile || durable.WorkflowPath != receipt.WorkflowPath ||
            durable.Branch != receipt.Branch ||
            !string.Equals(durable.CandidateSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            durable.TrustedWorkflowRef != receipt.TrustedWorkflowRef ||
            !string.Equals(durable.TrustedWorkflowSha, receipt.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            durable.RunId != correlation.RunId || durable.RunAttempt != correlation.RunAttempt)
            throw new UnauthorizedAccessException("Artifact retrieval receipt does not match the durable exact-run dispatch binding.");
    }

    private static HttpRequestMessage CreateApiRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Foundry/1.0");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub artifact metadata request failed with HTTP {(int)response.StatusCode}.");
        var bytes = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximumBytes)
            throw new InvalidDataException("Artifact response exceeds the host byte limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Artifact response exceeds the host byte limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Candidate provenance is missing object '{name}'.");
        return value;
    }

    private static string String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidDataException($"Candidate provenance property '{name}' is missing or not a string.");

    private static DateTimeOffset Date(JsonElement parent, string name)
    {
        var value = String(parent, name);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : throw new InvalidDataException($"Artifact timestamp '{name}' is invalid.");
    }

    private static void Demand(string actual, string expected, string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Candidate provenance {field} does not match the durable dispatch intent.");
    }

    private static void DemandSha(string actual, string expected, string field)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Candidate provenance {field} does not match the durable dispatch intent.");
    }

    private static void DemandUniqueProperties(JsonElement element, params string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Candidate provenance contains duplicate or unknown JSON properties.");
        if (seen.Count != expected.Length || expected.Any(name => !seen.Contains(name)))
            throw new InvalidDataException("Candidate provenance does not contain the exact required JSON property set.");
    }
}
