using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>A staged Git blob identity. File contents are never persisted in this record.</summary>
public sealed record GitHubStagedBlob(
    string Repository,
    string Branch,
    string CandidateSha,
    string Path,
    string BlobSha,
    string ContentSha256,
    DateTimeOffset StagedAt);

/// <summary>Durable host-owned staging metadata for one exact candidate commit.</summary>
public interface IGitHubRepositoryStagingStore
{
    GitHubStagedBlob Stage(GitHubStagedBlob stagedBlob);
    IReadOnlyList<GitHubStagedBlob> GetStaged(string repository, string branch, string candidateSha);
}

/// <summary>
/// Append-only JSONL staging metadata. It stores immutable Git blob identities and payload
/// digests, never file contents or credentials. It is suitable for a single-host proof only.
/// </summary>
public sealed class JsonlGitHubRepositoryStagingStore : IGitHubRepositoryStagingStore
{
    private const int SchemaVersion = 1;
    private const int MaximumRecordCharacters = 8192;
    private const int MaximumStagedPathsPerCandidate = 128;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Dictionary<string, GitHubStagedBlob> _entries = new(StringComparer.Ordinal);
    private long _sequence;

    public JsonlGitHubRepositoryStagingStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A staging journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public GitHubStagedBlob Stage(GitHubStagedBlob stagedBlob)
    {
        ArgumentNullException.ThrowIfNull(stagedBlob);
        Validate(stagedBlob);
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            var key = Key(stagedBlob.Repository, stagedBlob.Branch, stagedBlob.CandidateSha, stagedBlob.Path);
            if (_entries.TryGetValue(key, out var previous) &&
                string.Equals(previous.BlobSha, stagedBlob.BlobSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.ContentSha256, stagedBlob.ContentSha256, StringComparison.OrdinalIgnoreCase))
                return previous;
            var batchCount = _entries.Values.Count(entry => SameBatch(entry, stagedBlob));
            if (!_entries.ContainsKey(key) && batchCount >= MaximumStagedPathsPerCandidate)
                throw new InvalidOperationException("The candidate staging batch reached its 128-path limit.");

            var record = new JournalRecord(
                SchemaVersion, _sequence + 1, stagedBlob.Repository, stagedBlob.Branch,
                stagedBlob.CandidateSha, stagedBlob.Path, stagedBlob.BlobSha,
                stagedBlob.ContentSha256, stagedBlob.StagedAt);
            Append(record);
            _entries[key] = stagedBlob;
            _sequence = record.Sequence;
            return stagedBlob;
        }
    }

    public IReadOnlyList<GitHubStagedBlob> GetStaged(
        string repository, string branch, string candidateSha)
    {
        ValidateIdentity(repository, branch, candidateSha);
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Replay();
            return Array.AsReadOnly(_entries.Values
                .Where(entry =>
                    string.Equals(entry.Repository, repository, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Branch, branch, StringComparison.Ordinal) &&
                    string.Equals(entry.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray());
        }
    }

    private FileStream AcquireFileLock()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < LockTimeout)
            {
                Thread.Sleep(25);
            }
            catch (IOException exception)
            {
                throw new TimeoutException("Timed out waiting for the GitHub staging journal lock.", exception);
            }
        }
    }

    private void Replay()
    {
        _entries.Clear();
        _sequence = 0;
        if (!File.Exists(_path))
            return;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10)
                throw new InvalidDataException("GitHub staging journal has an incomplete final record.");
            stream.Position = 0;
        }

        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumRecordCharacters)
                throw new InvalidDataException($"GitHub staging journal record {lineNumber} is empty or oversized.");
            JournalRecord record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions)
                    ?? throw new JsonException("Null staging record.");
                Validate(record);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"GitHub staging journal record {lineNumber} is invalid.", exception);
            }
            if (record.Sequence != _sequence + 1)
                throw new InvalidDataException($"GitHub staging journal sequence is invalid at record {lineNumber}.");
            var key = Key(record.Repository, record.Branch, record.CandidateSha, record.Path);
            _entries[key] = new GitHubStagedBlob(
                record.Repository, record.Branch, record.CandidateSha, record.Path,
                record.BlobSha, record.ContentSha256, record.StagedAt);
            _sequence = record.Sequence;
        }
    }

    private void Append(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        if (line.Length > MaximumRecordCharacters)
            throw new InvalidOperationException("GitHub staging journal record exceeds its size limit.");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
        {
            writer.WriteLine(line);
            writer.Flush();
        }
        stream.Flush(flushToDisk: true);
    }

    private static void Validate(JournalRecord record)
    {
        if (record.SchemaVersion != SchemaVersion || record.Sequence <= 0 ||
            record.StagedAt == default)
            throw new ArgumentException("Staging journal metadata is invalid.");
        ValidateIdentity(record.Repository, record.Branch, record.CandidateSha);
        ValidatePath(record.Path);
        RequireSha(record.BlobSha, nameof(record.BlobSha));
        RequireSha(record.ContentSha256, nameof(record.ContentSha256));
    }

    private static void Validate(GitHubStagedBlob stagedBlob)
    {
        ValidateIdentity(stagedBlob.Repository, stagedBlob.Branch, stagedBlob.CandidateSha);
        ValidatePath(stagedBlob.Path);
        RequireSha(stagedBlob.BlobSha, nameof(stagedBlob.BlobSha));
        RequireSha(stagedBlob.ContentSha256, nameof(stagedBlob.ContentSha256));
        if (stagedBlob.StagedAt == default)
            throw new ArgumentException("A staging timestamp is required.", nameof(stagedBlob));
    }

    private static void ValidateIdentity(string repository, string branch, string candidateSha)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 ||
            string.IsNullOrWhiteSpace(branch) || branch.Length > 200 ||
            branch.Any(char.IsControl))
            throw new ArgumentException("Staging identity is invalid.");
        RequireSha(candidateSha, nameof(candidateSha));
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':') || path.Contains('%') ||
            path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Staging path must be a normalized relative repository path.", nameof(path));
    }

    private static void RequireSha(string value, string parameterName)
    {
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal SHA is required.", parameterName);
    }

    private static string Key(string repository, string branch, string candidateSha, string path) =>
        string.Join("\n", repository.ToUpperInvariant(), branch, candidateSha.ToLowerInvariant(), path);

    private static bool SameBatch(GitHubStagedBlob left, GitHubStagedBlob right) =>
        string.Equals(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Branch, right.Branch, StringComparison.Ordinal) &&
        string.Equals(left.CandidateSha, right.CandidateSha, StringComparison.OrdinalIgnoreCase);

    private sealed record JournalRecord(
        int SchemaVersion,
        long Sequence,
        string Repository,
        string Branch,
        string CandidateSha,
        string Path,
        string BlobSha,
        string ContentSha256,
        DateTimeOffset StagedAt);
}
