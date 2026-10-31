using System.Net;
using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactRetrievalCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTransport(
        Func<Uri, CancellationToken, ValueTask<CiAiArtifactTransportResponse>> handler)
        : ICiAiArtifactTransport
    {
        public Uri? RequestedUri { get; private set; }

        public ValueTask<CiAiArtifactTransportResponse> GetAsync(
            Uri validatedRetrievalUri,
            CancellationToken cancellationToken)
        {
            RequestedUri = validatedRetrievalUri;
            return handler(validatedRetrievalUri, cancellationToken);
        }
    }

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

    private static CiAiArtifactTransportResponse Response(
        byte[] bytes,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json",
        Uri? finalUri = null,
        bool redirected = false) =>
        new(status, mediaType,
            finalUri ?? new Uri("https://artifacts.example.test/evidence.json"),
            redirected, new MemoryStream(bytes, writable: false));

    [Fact]
    public async Task RetrievesExactValidatedUriAndReturnsVerifiedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"result\":\"passed\"}");
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes)));
        var coordinator = new CiAiArtifactRetrievalCoordinator(transport);

        var verified = await coordinator.RetrieveAndVerifyAsync(notice, Now);

        Assert.Equal(notice.RetrievalUri, transport.RequestedUri);
        Assert.Equal(bytes, verified.Bytes.ToArray());
        Assert.Same(notice, verified.Notice);
    }

    [Fact]
    public async Task RejectsRedirectEvenWhenFinalUriMatches()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes, redirected: true)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task RejectsChangedFinalUri()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes,
                finalUri: new Uri("https://other.example.test/evidence.json"))));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task RejectsUnexpectedStatus(HttpStatusCode status)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes, status: status)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task RejectsUnexpectedMediaType()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes, mediaType: "text/html")));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task RejectsResponseLargerThanDeclaredSizeWithoutBufferingTheRemainder()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var notice = Notice(bytes[..3]);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task RejectsShortResponse()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var notice = Notice(expected);
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(expected[..2])));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task RejectsDigestMismatchAfterBoundedRead()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes) with { Digest = new string('0', 64) };
        var transport = new FakeTransport((_, _) =>
            ValueTask.FromResult(Response(bytes)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
    }

    [Fact]
    public async Task PropagatesCancellationToHostTransport()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes);
        var transport = new FakeTransport((_, token) =>
            ValueTask.FromCanceled<CiAiArtifactTransportResponse>(token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport)
                .RetrieveAndVerifyAsync(notice, Now, cancellation.Token));
    }

    [Fact]
    public async Task RejectsExpiredNoticeBeforeCallingTransport()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var notice = Notice(bytes) with { ExpiresAt = Now };
        var transport = new FakeTransport((_, _) =>
            throw new InvalidOperationException("Transport must not be called."));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CiAiArtifactRetrievalCoordinator(transport).RetrieveAndVerifyAsync(notice, Now));
        Assert.Null(transport.RequestedUri);
    }

    [Fact]
    public void RejectsInvalidTimeout()
    {
        var transport = new FakeTransport((_, _) =>
            throw new InvalidOperationException("Transport must not be called."));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CiAiArtifactRetrievalCoordinator(transport, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CiAiArtifactRetrievalCoordinator(transport, TimeSpan.FromMinutes(6)));
    }
}
