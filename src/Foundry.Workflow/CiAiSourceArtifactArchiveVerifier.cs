using System.IO.Compression;
using System.Security.Cryptography;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Immutable result of verifying the provider-observed ZIP bytes and extracting the two
/// exact source-evidence payloads. This verifies archive integrity and safe extraction only;
/// it does not establish that the supplied provider metadata was independently observed.
/// </summary>
internal sealed class CiAiVerifiedSourceArtifactArchive
{
    private readonly byte[] _trxBytes;
    private readonly byte[] _provenanceBytes;

    internal CiAiVerifiedSourceArtifactArchive(
        string providerArchiveDigest,
        long archiveSizeBytes,
        byte[] trxBytes,
        byte[] provenanceBytes)
    {
        ProviderArchiveDigest = providerArchiveDigest;
        ArchiveSizeBytes = archiveSizeBytes;
        _trxBytes = trxBytes.ToArray();
        _provenanceBytes = provenanceBytes.ToArray();
        TrxSha256 = Hash(_trxBytes);
        ProvenanceSha256 = Hash(_provenanceBytes);
    }

    public string ProviderArchiveDigest { get; }
    public long ArchiveSizeBytes { get; }
    public string TrxSha256 { get; }
    public long TrxSizeBytes => _trxBytes.LongLength;
    public string ProvenanceSha256 { get; }
    public long ProvenanceSizeBytes => _provenanceBytes.LongLength;
    public ReadOnlyMemory<byte> TrxBytes => _trxBytes.ToArray();
    public ReadOnlyMemory<byte> ProvenanceBytes => _provenanceBytes.ToArray();

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>
/// Bounded in-memory verifier for the nested CiAi source-evidence ZIP. Call only after the
/// provider adapter has independently observed the archive digest and size for the exact
/// run attempt and artifact. No filesystem extraction is performed.
/// </summary>
internal static class CiAiSourceArtifactArchiveVerifier
{
    public const long MaximumArchiveBytes = 20 * 1024 * 1024;
    public const long MaximumPayloadBytes = 16 * 1024 * 1024;
    private const int MaximumCompressionRatio = 100;

    public static CiAiVerifiedSourceArtifactArchive Verify(
        ReadOnlyMemory<byte> archiveBytes,
        string providerArchiveDigest,
        long providerArchiveSizeBytes,
        string expectedTrxEntryName,
        string expectedProvenanceEntryName)
    {
        ValidateLeafName(expectedTrxEntryName, nameof(expectedTrxEntryName));
        ValidateLeafName(expectedProvenanceEntryName, nameof(expectedProvenanceEntryName));
        if (string.Equals(expectedTrxEntryName, expectedProvenanceEntryName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TRX and provenance entry names must be distinct.");
        if (archiveBytes.IsEmpty || archiveBytes.Length > MaximumArchiveBytes)
            throw new InvalidDataException("Source artifact archive is empty or exceeds the 20 MiB limit.");
        if (providerArchiveSizeBytes != archiveBytes.Length)
            throw new InvalidDataException("Source artifact archive size differs from provider-observed metadata.");

        var expectedHash = ParseDigest(providerArchiveDigest);
        var actualHash = SHA256.HashData(archiveBytes.Span);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("Source artifact archive digest differs from provider-observed metadata.");

        using var stream = new MemoryStream(archiveBytes.ToArray(), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != 2)
            throw new InvalidDataException("Source artifact archive must contain exactly the TRX and provenance entries.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? trx = null;
        ZipArchiveEntry? provenance = null;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry);
            if (!names.Add(entry.FullName))
                throw new InvalidDataException("Source artifact archive contains duplicate entry names.");

            if (string.Equals(entry.FullName, expectedTrxEntryName, StringComparison.Ordinal))
                trx = entry;
            else if (string.Equals(entry.FullName, expectedProvenanceEntryName, StringComparison.Ordinal))
                provenance = entry;
            else
                throw new InvalidDataException("Source artifact archive contains an unexpected entry.");
        }

        if (trx is null || provenance is null)
            throw new InvalidDataException("Source artifact archive is missing the exact TRX or provenance entry.");

        var trxBytes = ReadBounded(trx);
        var provenanceBytes = ReadBounded(provenance);
        return new CiAiVerifiedSourceArtifactArchive(
            "sha256:" + Convert.ToHexString(actualHash).ToLowerInvariant(),
            archiveBytes.Length,
            trxBytes,
            provenanceBytes);
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        ValidateLeafName(entry.FullName, nameof(entry));
        if (entry.Length < 0 || entry.Length > MaximumPayloadBytes ||
            entry.CompressedLength < 0 || entry.CompressedLength > MaximumArchiveBytes)
            throw new InvalidDataException("Source artifact entry exceeds its configured size limit.");
        if (entry.Length > 0 && entry.CompressedLength == 0)
            throw new InvalidDataException("Source artifact entry has an invalid zero-length compressed representation.");
        if (entry.CompressedLength > 0 &&
            entry.Length > (long)entry.CompressedLength * MaximumCompressionRatio)
            throw new InvalidDataException("Source artifact entry exceeds the maximum compression ratio.");

        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType != 0 && unixType != 0x8000)
            throw new InvalidDataException("Source artifact archive contains a directory, symbolic link, or special entry.");
        if ((entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException("Source artifact archive contains a reparse-point entry.");
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, MaximumPayloadBytes));
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total += read;
            if (total > MaximumPayloadBytes || total > entry.Length)
                throw new InvalidDataException("Source artifact entry expanded beyond its declared or configured size.");
            output.Write(buffer, 0, read);
        }

        if (total != entry.Length)
            throw new InvalidDataException("Source artifact entry length differs from its ZIP metadata.");
        return output.ToArray();
    }

    private static byte[] ParseDigest(string value)
    {
        if (value is not { Length: 71 } ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !value.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("Provider archive digest must use sha256:<64 hexadecimal characters>.");
        return Convert.FromHexString(value[7..]);
    }

    private static void ValidateLeafName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 200 || value is "." or ".." ||
            value.Contains('/') || value.Contains('\\') || value.Contains(':') ||
            value.Any(char.IsControl) || Path.IsPathRooted(value))
            throw new ArgumentException("Source artifact entry names must be safe, bounded leaf filenames.", parameterName);
    }
}
