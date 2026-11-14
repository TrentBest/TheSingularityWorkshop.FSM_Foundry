using System.Security.Cryptography;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactContentVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static CiAiValidatedArtifactNotice Notice(byte[] content) =>
        new(
            new CiAiRequestTaskMappingSnapshot(
                new CiAiRequestTaskMapping(
                    1, "request", "operation", "key", "task", "correlation",
                    "owner/repo", "refs/heads/work/task", new string('a', 40),
                    ".github/workflows/build.yml", "refs/heads/development", new string('b', 40),
                    "workflow", "instruction", "schema", "provider", "https://artifacts.example.test/",
                    "https://provider.example.test/receipts", "profile",
                    Now.AddMinutes(-5), Now.AddHours(1)),
                null, false),
            "notice", Now.AddMinutes(-1), "owner/provider", "capability", "1", "revision",
            "artifact", "v1", "application/json", content.Length,
            new Uri("https://artifacts.example.test/evidence.json"),
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            Now.AddMinutes(30), null, "receiver", new string('c', 64));

    [Fact]
    public void AcceptsExactBytesAndReturnsDefensiveCopy()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("{\"result\":\"passed\"}");
        var notice = Notice(content);

        var verified = CiAiArtifactContentVerifier.Verify(notice, content, Now);

        Assert.Equal(content, verified.Bytes.ToArray());
        Assert.Same(notice, verified.Notice);
        content[0] ^= 0xff;
        Assert.Equal("{\"result\":\"passed\"}", System.Text.Encoding.UTF8.GetString(verified.Bytes.Span));
    }

    [Fact]
    public void RejectsLengthMismatch()
    {
        var content = new byte[] { 1, 2, 3 };
        Assert.Throws<InvalidDataException>(() =>
            CiAiArtifactContentVerifier.Verify(Notice(content), new byte[] { 1, 2 }, Now));
    }

    [Fact]
    public void RejectsDigestMismatch()
    {
        var content = new byte[] { 1, 2, 3 };
        var notice = Notice(content) with { Digest = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() =>
            CiAiArtifactContentVerifier.Verify(notice, content, Now));
    }

    [Fact]
    public void RejectsExpiredArtifact()
    {
        var content = new byte[] { 1, 2, 3 };
        var notice = Notice(content) with { ExpiresAt = Now };
        Assert.Throws<InvalidOperationException>(() =>
            CiAiArtifactContentVerifier.Verify(notice, content, Now));
    }

    [Fact]
    public void RejectsMalformedValidatedDigest()
    {
        var content = new byte[] { 1, 2, 3 };
        var notice = Notice(content) with { Digest = "not-hex" };
        Assert.Throws<InvalidDataException>(() =>
            CiAiArtifactContentVerifier.Verify(notice, content, Now));
    }
}
