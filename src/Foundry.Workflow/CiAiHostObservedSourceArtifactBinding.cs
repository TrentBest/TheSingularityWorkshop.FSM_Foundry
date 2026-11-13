using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Independent host observation of the nested TRX source artifact. The trusted host adapter
/// must populate this only after provider artifact listing/detail observation, exact job lookup,
/// and bounded archive extraction/digest verification; never copy values from report JSON.
/// Foundry compares every field in this binding to the report before accepting it.
/// </summary>
public sealed record CiAiHostObservedSourceArtifactBinding(
    string Repository,
    long RunId,
    int RunAttempt,
    string CandidateSha,
    string WorkflowPath,
    long JobId,
    string JobName,
    string JobKey,
    long ArtifactId,
    string ArtifactName,
    string ProviderArchiveDigest,
    long ArchiveSizeBytes,
    DateTimeOffset JobStartedAt,
    DateTimeOffset JobCompletedAt,
    DateTimeOffset ArtifactCreatedAt,
    DateTimeOffset ArtifactExpiresAt,
    string PayloadSha256,
    long PayloadSizeBytes)
{
    public string IdentitySha256
    {
        get
        {
            var material = string.Join("\n", new[]
            {
                Repository.ToLowerInvariant(),
                RunId.ToString(CultureInfo.InvariantCulture),
                RunAttempt.ToString(CultureInfo.InvariantCulture),
                CandidateSha.ToLowerInvariant(),
                WorkflowPath,
                JobId.ToString(CultureInfo.InvariantCulture),
                JobName,
                JobKey,
                ArtifactId.ToString(CultureInfo.InvariantCulture),
                ArtifactName,
                ProviderArchiveDigest.ToLowerInvariant(),
                ArchiveSizeBytes.ToString(CultureInfo.InvariantCulture),
                JobStartedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                JobCompletedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                ArtifactCreatedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                ArtifactExpiresAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                PayloadSha256.ToLowerInvariant(),
                PayloadSizeBytes.ToString(CultureInfo.InvariantCulture)
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }
    }

    public CiAiHostObservedSourceArtifactBinding ValidateAgainst(
        CiAiRequestTaskMapping mapping,
        CiAiDispatchRunBinding run,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(run);
        if (now == default)
            throw new ArgumentException("A current source-artifact validation time is required.", nameof(now));

        static bool Clean(string? value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
        static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
        static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

        var repositoryParts = Repository.Split('/');
        if (!Clean(Repository, 200) || repositoryParts.Length != 2 ||
            repositoryParts.Any(part => !Clean(part, 100)) ||
            RunId <= 0 || RunAttempt <= 0 || !IsSha(CandidateSha) ||
            !Clean(WorkflowPath, 512) || !WorkflowPath.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            !Clean(JobName, 200) || !Clean(JobKey, 100) || JobKey.Any(c =>
                !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
            JobId <= 0 || ArtifactId <= 0 || !Clean(ArtifactName, 200) ||
            ProviderArchiveDigest is not { Length: 71 } ||
            !ProviderArchiveDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !ProviderArchiveDigest.AsSpan(7).ToString().All(Uri.IsHexDigit) ||
            ArchiveSizeBytes is <= 0 or > 20 * 1024 * 1024 ||
            !IsDigest(PayloadSha256) || PayloadSizeBytes is <= 0 or > 16 * 1024 * 1024 ||
            JobStartedAt == default || JobCompletedAt < JobStartedAt ||
            ArtifactCreatedAt < JobStartedAt || ArtifactCreatedAt > JobCompletedAt ||
            ArtifactExpiresAt <= ArtifactCreatedAt || ArtifactCreatedAt > now || ArtifactExpiresAt <= now)
            throw new InvalidDataException("Host-observed source artifact binding is malformed, expired, or outside the exact job-attempt window.");

        if (!string.Equals(Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            RunId != run.RunId || RunAttempt != run.RunAttempt ||
            !string.Equals(CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            WorkflowPath != mapping.WorkflowPath ||
            !string.Equals(JobName, mapping.JobName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(mapping.JobKey) ||
            !string.Equals(JobKey, mapping.JobKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(mapping.SourceArtifactName) ||
            !string.Equals(ArtifactName, mapping.SourceArtifactName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Host-observed source artifact does not match the persisted dispatch, required job, or configured artifact name.");

        return this;
    }
}
