using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>A host-owned workflow entry in the GitHub Actions allowlist.</summary>
public sealed record GitHubActionsWorkflowDefinition
{
    public GitHubActionsWorkflowDefinition(string key, string workflowFile, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw new ArgumentException("Workflow key must be bounded and control-free.", nameof(key));
        if (string.IsNullOrWhiteSpace(workflowFile) || workflowFile.Length > 128 ||
            workflowFile.Contains('/') || workflowFile.Contains('\\') || workflowFile.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Workflow identifier must be a workflow file name or numeric ID.", nameof(workflowFile));
        if (string.IsNullOrWhiteSpace(expectedPath) || expectedPath.Length > 256 ||
            expectedPath.StartsWith('/') || expectedPath.Contains('\\') ||
            expectedPath.Split('/').Any(part => part is "" or "." or "..") ||
            !expectedPath.StartsWith(".github/workflows/", StringComparison.Ordinal))
            throw new ArgumentException("Expected workflow path must be a normalized workflow path.", nameof(expectedPath));
        Key = key;
        WorkflowFile = workflowFile;
        ExpectedPath = expectedPath;
    }

    public string Key { get; }
    public string WorkflowFile { get; }
    public string ExpectedPath { get; }
}

/// <summary>Immutable host-owned mapping from workflow keys to exact GitHub workflow definitions.</summary>
public sealed class GitHubActionsWorkflowAllowlist
{
    private readonly IReadOnlyDictionary<string, GitHubActionsWorkflowDefinition> _definitions;

    public GitHubActionsWorkflowAllowlist(IEnumerable<GitHubActionsWorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<string, GitHubActionsWorkflowDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!map.TryAdd(definition.Key, definition))
                throw new ArgumentException("Workflow keys must be unique.", nameof(definitions));
        }
        if (map.Count == 0)
            throw new ArgumentException("At least one workflow must be allowlisted.", nameof(definitions));
        _definitions = map;
    }

    public GitHubActionsWorkflowDefinition Demand(string key) =>
        _definitions.TryGetValue(key, out var definition)
            ? definition
            : throw new UnauthorizedAccessException("The workflow key is not in the host allowlist.");

    public GitHubActionsWorkflowDefinition DemandByFileAndPath(string workflowFile, string expectedPath)
    {
        var matches = _definitions.Values.Where(definition =>
            string.Equals(definition.WorkflowFile, workflowFile, StringComparison.Ordinal) &&
            string.Equals(definition.ExpectedPath, expectedPath, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new UnauthorizedAccessException("Dispatch receipt does not identify one allowlisted workflow.");
    }
}

/// <summary>Durable correlation data returned after GitHub accepts a workflow dispatch.</summary>
public sealed record GitHubActionsDispatchReceipt(
    string RequestId,
    string Repository,
    string WorkflowFile,
    string WorkflowPath,
    string Branch,
    string CandidateSha,
    DateTimeOffset DispatchedAt);

/// <summary>An exact workflow run and attempt correlated to one host dispatch receipt.</summary>
public sealed record GitHubActionsRunCorrelation(
    long RunId,
    int RunAttempt,
    string HtmlUrl,
    string Status,
    string? Conclusion,
    string HeadBranch,
    string HeadSha,
    string WorkflowPath,
    string DisplayTitle);

public sealed record GitHubActionsRequiredJobResult(
    string Name, string Status, string? Conclusion, bool Succeeded);

public sealed record GitHubActionsAttemptEvidence(
    long RunId, int RunAttempt, string CandidateSha,
    IReadOnlyList<GitHubActionsRequiredJobResult> RequiredJobs, bool AllRequiredJobsSucceeded);

/// <summary>
/// Dispatches only host-allowlisted workflows and correlates the resulting run by an unguessable
/// request ID plus exact workflow path, event, branch, and candidate SHA. Dispatch acceptance
/// is not CI success; callers must inspect the exact run attempt and required jobs separately.
/// </summary>
public sealed class GitHubActionsWorkflowDispatcher
{
    private const string ApiVersion = "2022-11-28";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly IGitHubAccessTokenProvider _tokens;
    private readonly GitHubRepositoryOperationPolicy _repositoryPolicy;
    private readonly GitHubActionsWorkflowAllowlist _workflows;
    private readonly IGitHubActionsDispatchJournal _journal;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubActionsWorkflowDispatcher(
        HttpClient http,
        IGitHubAccessTokenProvider tokens,
        GitHubRepositoryOperationPolicy repositoryPolicy,
        GitHubActionsWorkflowAllowlist workflows,
        IGitHubActionsDispatchJournal journal,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _repositoryPolicy = repositoryPolicy ?? throw new ArgumentNullException(nameof(repositoryPolicy));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_http.BaseAddress is null ||
            !string.Equals(_http.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_http.BaseAddress.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            _http.BaseAddress.Port != 443 || _http.BaseAddress.AbsolutePath != "/" ||
            !_http.BaseAddress.Query.Equals(string.Empty, StringComparison.Ordinal))
            throw new ArgumentException("The GitHub API client must use the fixed https://api.github.com origin.", nameof(http));
    }

    public async ValueTask<GitHubActionsDispatchReceipt> DispatchAsync(
        string repository, string workflowKey, string branch, string candidateSha,
        CancellationToken cancellationToken = default)
    {
        _repositoryPolicy.DemandRepository(repository);
        GitHubRepositoryOperationPolicy.DemandCandidateBranch(branch);
        if (string.IsNullOrWhiteSpace(workflowKey) || workflowKey.Length > 128 || workflowKey.Any(char.IsControl))
            throw new ArgumentException("Workflow key must be bounded and control-free.", nameof(workflowKey));
        if (candidateSha is not { Length: 40 } || !candidateSha.All(Uri.IsHexDigit))
            throw new ArgumentException("A full candidate SHA is required.", nameof(candidateSha));

        var workflow = _workflows.Demand(workflowKey);
        var token = await GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        await DemandExactCandidateTipAsync(repository, branch, candidateSha, token, cancellationToken).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString("N");
        var dispatchTime = _clock();
        _journal.Begin(requestId, repository, workflow.WorkflowFile, workflow.ExpectedPath,
            branch, candidateSha, dispatchTime);
        var payload = JsonSerializer.Serialize(new
        {
            @ref = branch,
            inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["foundry_request_id"] = requestId,
                ["candidate_sha"] = candidateSha
            }
        }, JsonOptions);
        using var response = await SendAsync(HttpMethod.Post,
            $"repos/{repository}/actions/workflows/{Uri.EscapeDataString(workflow.WorkflowFile)}/dispatches",
            token, payload, cancellationToken).ConfigureAwait(false);
        var acknowledgedAt = _clock();
        _journal.Acknowledge(requestId, acknowledgedAt);

        return new GitHubActionsDispatchReceipt(
            requestId, repository, workflow.WorkflowFile, workflow.ExpectedPath,
            branch, candidateSha, acknowledgedAt);
    }

    /// <summary>
    /// Returns null until GitHub exposes the matching run. A result is accepted only when
    /// every correlation field matches; ambiguous duplicate matches are rejected.
    /// </summary>
    public async ValueTask<GitHubActionsRunCorrelation?> FindCorrelatedRunAsync(
        GitHubActionsDispatchReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _repositoryPolicy.DemandRepository(receipt.Repository);
        GitHubRepositoryOperationPolicy.DemandCandidateBranch(receipt.Branch);
        if (receipt.RequestId is not { Length: 32 } || !receipt.RequestId.All(Uri.IsHexDigit) ||
            receipt.CandidateSha is not { Length: 40 } || !receipt.CandidateSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Dispatch receipt identity is invalid.", nameof(receipt));

        var workflow = _workflows.DemandByFileAndPath(receipt.WorkflowFile, receipt.WorkflowPath);
        var durable = _journal.Get(receipt.RequestId);
        if (!string.Equals(durable.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(durable.WorkflowFile, receipt.WorkflowFile, StringComparison.Ordinal) ||
            !string.Equals(durable.WorkflowPath, receipt.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(durable.Branch, receipt.Branch, StringComparison.Ordinal) ||
            !string.Equals(durable.CandidateSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Dispatch receipt does not match the durable dispatch intent.");
        var token = await GetTokenAsync(receipt.Repository, cancellationToken).ConfigureAwait(false);
        var query = $"repos/{receipt.Repository}/actions/workflows/{Uri.EscapeDataString(workflow.WorkflowFile)}/runs?event=workflow_dispatch&branch={Uri.EscapeDataString(receipt.Branch)}&head_sha={receipt.CandidateSha}&per_page=100";
        using var response = await SendAsync(HttpMethod.Get, query, token, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response!, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("total_count", out var totalRuns) ||
            !totalRuns.TryGetInt32(out var totalRunCount) || totalRunCount < 0 || totalRunCount > 100 ||
            !document.RootElement.TryGetProperty("workflow_runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() != totalRunCount)
            throw new InvalidDataException("GitHub workflow-runs response is incomplete or exceeds the correlation limit.");

        var matches = new List<GitHubActionsRunCorrelation>();
        foreach (var run in runs.EnumerateArray())
        {
            var path = GetString(run, "path");
            var eventName = GetString(run, "event");
            var branch = GetString(run, "head_branch");
            var sha = GetString(run, "head_sha");
            var title = GetString(run, "display_title");
            if (!string.Equals(path, receipt.WorkflowPath, StringComparison.Ordinal) ||
                !string.Equals(eventName, "workflow_dispatch", StringComparison.Ordinal) ||
                !string.Equals(branch, receipt.Branch, StringComparison.Ordinal) ||
                !string.Equals(sha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(title, "Foundry CI " + receipt.RequestId, StringComparison.Ordinal))
                continue;

            if (!run.TryGetProperty("id", out var id) || !id.TryGetInt64(out var runId) || runId <= 0 ||
                !run.TryGetProperty("run_attempt", out var attempt) || !attempt.TryGetInt32(out var runAttempt) || runAttempt <= 0)
                throw new InvalidDataException("GitHub returned a correlated run without a valid run ID or attempt.");
            var htmlUrl = GetString(run, "html_url");
            var status = GetString(run, "status");
            var conclusion = GetNullableString(run, "conclusion");
            if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var parsedUrl) ||
                parsedUrl.Scheme != Uri.UriSchemeHttps || parsedUrl.Host != "github.com" ||
                parsedUrl.AbsolutePath != $"/{receipt.Repository}/actions/runs/{runId}" ||
                string.IsNullOrWhiteSpace(status))
                throw new InvalidDataException("GitHub returned invalid workflow run metadata.");

            matches.Add(new GitHubActionsRunCorrelation(
                runId, runAttempt, htmlUrl, status, conclusion, branch, sha, path, title));
        }

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
            throw new InvalidDataException("Multiple GitHub Actions runs claim the same Foundry dispatch request ID.");
        var correlated = matches[0];
        if (durable.State is GitHubActionsDispatchState.Correlated or GitHubActionsDispatchState.Verified)
        {
            if (durable.RunId != correlated.RunId || durable.RunAttempt != correlated.RunAttempt)
                throw new InvalidDataException("GitHub run correlation conflicts with the durable correlated attempt.");
        }
        else
        {
            _journal.Correlate(receipt.RequestId, correlated.RunId, correlated.RunAttempt);
        }
        return correlated;
    }

    /// <summary>Rehydrates a receipt after process restart without issuing another dispatch.</summary>
    public GitHubActionsDispatchReceipt GetDispatchReceipt(string requestId)
    {
        var record = _journal.Get(requestId);
        return new GitHubActionsDispatchReceipt(record.RequestId, record.Repository,
            record.WorkflowFile, record.WorkflowPath, record.Branch, record.CandidateSha,
            record.AcknowledgedAt ?? record.CreatedAt);
    }

    /// <summary>
    /// Reads the exact run attempt's jobs endpoint and verifies every configured required job.
    /// More than 100 jobs is rejected rather than silently accepting a truncated result.
    /// </summary>
    public async ValueTask<GitHubActionsAttemptEvidence> VerifyRequiredJobsAsync(
        GitHubActionsDispatchReceipt receipt, GitHubActionsRunCorrelation correlation,
        IReadOnlyCollection<string> requiredJobNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(requiredJobNames);
        if (correlation.RunId <= 0 || correlation.RunAttempt <= 0 ||
            !string.Equals(correlation.HeadSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(correlation.HeadBranch, receipt.Branch, StringComparison.Ordinal) ||
            !string.Equals(correlation.WorkflowPath, receipt.WorkflowPath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Run correlation does not match the dispatch receipt.");
        var durable = _journal.Get(receipt.RequestId);
        if (!string.Equals(durable.Repository, receipt.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(durable.WorkflowFile, receipt.WorkflowFile, StringComparison.Ordinal) ||
            !string.Equals(durable.WorkflowPath, receipt.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(durable.Branch, receipt.Branch, StringComparison.Ordinal) ||
            !string.Equals(durable.CandidateSha, receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            durable.State is not (GitHubActionsDispatchState.Correlated or GitHubActionsDispatchState.Verified) ||
            durable.RunId != correlation.RunId || durable.RunAttempt != correlation.RunAttempt)
            throw new UnauthorizedAccessException("Required-job verification is not bound to the durable exact run attempt.");
        var required = requiredJobNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (required.Length == 0 || required.Length != requiredJobNames.Count ||
            required.Distinct(StringComparer.Ordinal).Count() != required.Length ||
            required.Any(name => name.Length > 256 || name.Any(char.IsControl)))
            throw new ArgumentException("At least one unique, bounded required job name is required.", nameof(requiredJobNames));

        var token = await GetTokenAsync(receipt.Repository, cancellationToken).ConfigureAwait(false);
        var path = $"repos/{receipt.Repository}/actions/runs/{correlation.RunId}/attempts/{correlation.RunAttempt}/jobs?per_page=100";
        using var response = await SendAsync(HttpMethod.Get, path, token, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response!, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("total_count", out var totalCount) || !totalCount.TryGetInt32(out var total) ||
            total < 0 || total > 100 ||
            !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array ||
            jobs.GetArrayLength() != total)
            throw new InvalidDataException("GitHub job response is incomplete or exceeds the verification limit.");

        var parsed = new List<(string Name, string Status, string? Conclusion)>();
        foreach (var job in jobs.EnumerateArray())
        {
            if (job.TryGetProperty("run_id", out var runId) && runId.TryGetInt64(out var actualRunId) &&
                actualRunId != correlation.RunId)
                throw new InvalidDataException("A job belongs to a different workflow run.");
            if (job.TryGetProperty("run_attempt", out var runAttempt) && runAttempt.TryGetInt32(out var actualAttempt) &&
                actualAttempt != correlation.RunAttempt)
                throw new InvalidDataException("A job belongs to a different workflow run attempt.");
            parsed.Add((GetString(job, "name"), GetString(job, "status"), GetNullableString(job, "conclusion")));
        }

        var results = new List<GitHubActionsRequiredJobResult>();
        foreach (var requiredName in required)
        {
            var found = parsed.Where(job => string.Equals(job.Name, requiredName, StringComparison.Ordinal)).ToArray();
            if (found.Length > 1)
                throw new InvalidDataException("A required job name appears more than once in the exact run attempt.");
            var job = found.SingleOrDefault();
            var succeeded = found.Length == 1 &&
                string.Equals(job.Status, "completed", StringComparison.Ordinal) &&
                string.Equals(job.Conclusion, "success", StringComparison.Ordinal);
            results.Add(new GitHubActionsRequiredJobResult(requiredName,
                found.Length == 0 ? "missing" : job.Status, found.Length == 0 ? null : job.Conclusion, succeeded));
        }
        var allSucceeded = string.Equals(correlation.Status, "completed", StringComparison.Ordinal) &&
            string.Equals(correlation.Conclusion, "success", StringComparison.Ordinal) &&
            results.All(result => result.Succeeded);
        if (allSucceeded)
        {
            var verifiedRecord = _journal.Get(receipt.RequestId);
            if (verifiedRecord.State == GitHubActionsDispatchState.Correlated)
                _journal.Verify(receipt.RequestId, correlation.RunId, correlation.RunAttempt);
            else if (verifiedRecord.State != GitHubActionsDispatchState.Verified ||
                verifiedRecord.RunId != correlation.RunId || verifiedRecord.RunAttempt != correlation.RunAttempt)
                throw new InvalidOperationException("Durable dispatch state does not match the verified workflow attempt.");
        }
        return new GitHubActionsAttemptEvidence(correlation.RunId, correlation.RunAttempt,
            receipt.CandidateSha, results, allSucceeded);
    }

    private async ValueTask DemandExactCandidateTipAsync(
        string repository, string branch, string candidateSha, string token, CancellationToken cancellationToken)
    {
        var relative = $"repos/{repository}/git/ref/{Uri.EscapeDataString("heads/" + branch)}";
        using var response = await SendAsync(HttpMethod.Get, relative, token, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response!, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (GetString(root, "ref") != "refs/heads/" + branch ||
            !root.TryGetProperty("object", out var obj) ||
            !string.Equals(GetString(obj, "type"), "commit", StringComparison.Ordinal) ||
            !string.Equals(GetString(obj, "sha"), candidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Candidate branch is not at the exact authorized SHA; CI dispatch was refused.");
    }

    private async ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new UnauthorizedAccessException("The host token provider did not return a valid access token.");
        return token;
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string token, string? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Foundry/1.0");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"GitHub Actions operation failed with HTTP {status}.");
        }
        return response;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int maximumBytes = 512 * 1024;
        if (response.Content.Headers.ContentLength is > maximumBytes)
            throw new InvalidDataException("GitHub Actions response exceeds the host response limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes)
                throw new InvalidDataException("GitHub Actions response exceeds the host response limit.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
