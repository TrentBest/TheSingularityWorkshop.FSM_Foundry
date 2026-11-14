using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiSourceArtifactArchiveVerifierTests
{
    private const string TrxName = "results.trx";
    private const string ProvenanceName = "provenance.json";

    [Fact]
    public void VerifiesProviderDigestAndExtractsExactPayloadsInMemory()
    {
        var trx = Encoding.UTF8.GetBytes("<TestRun />");
        var provenance = Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-provenance.v1\"}");
        var archive = CreateArchive((TrxName, trx), (ProvenanceName, provenance));
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        var verified = CiAiSourceArtifactArchiveVerifier.Verify(
            archive, digest, archive.Length, TrxName, ProvenanceName);

        Assert.Equal(digest, verified.ProviderArchiveDigest);
        Assert.Equal(archive.Length, verified.ArchiveSizeBytes);
        Assert.Equal(trx, verified.TrxBytes.ToArray());
        Assert.Equal(provenance, verified.ProvenanceBytes.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(trx)).ToLowerInvariant(), verified.TrxSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(provenance)).ToLowerInvariant(), verified.ProvenanceSha256);

        var copy = verified.TrxBytes.ToArray();
        copy[0] ^= 0xff;
        Assert.Equal(trx, verified.TrxBytes.ToArray());
    }

    [Fact]
    public void RejectsWrongProviderDigestAndArchiveSize()
    {
        var archive = CreateArchive((TrxName, new byte[] { 1 }), (ProvenanceName, new byte[] { 2 }));
        var digest = "sha256:" + new string('0', 64);

        Assert.Throws<InvalidDataException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(archive, digest, archive.Length, TrxName, ProvenanceName));
        Assert.Throws<InvalidDataException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(archive, Digest(archive), archive.Length + 1, TrxName, ProvenanceName));
    }

    [Fact]
    public void RejectsUnexpectedDuplicateAndUnsafeEntries()
    {
        var extra = CreateArchive((TrxName, new byte[] { 1 }), (ProvenanceName, new byte[] { 2 }), ("extra.txt", new byte[] { 3 }));
        Assert.Throws<InvalidDataException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(extra, Digest(extra), extra.Length, TrxName, ProvenanceName));

        var wrongNames = CreateArchive(("nested/results.trx", new byte[] { 1 }), (ProvenanceName, new byte[] { 2 }));
        Assert.Throws<ArgumentException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(wrongNames, Digest(wrongNames), wrongNames.Length, TrxName, ProvenanceName));
    }

    [Fact]
    public void RejectsDuplicateExpectedNamesAndMalformedDigest()
    {
        var archive = CreateArchive((TrxName, new byte[] { 1 }), (ProvenanceName, new byte[] { 2 }));
        Assert.Throws<ArgumentException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(archive, Digest(archive), archive.Length, TrxName, TrxName));
        Assert.Throws<InvalidDataException>(() =>
            CiAiSourceArtifactArchiveVerifier.Verify(archive, "sha256:bad", archive.Length, TrxName, ProvenanceName));
    }

    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] CreateArchive(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var output = entry.Open();
                output.Write(bytes);
            }
        }
        return stream.ToArray();
    }
}
