using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactNoticeIngressTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private sealed class TestAuthenticator(string senderIdentity) : ICiAiArtifactNoticeAuthenticator
    {
        public ValueTask<string> AuthenticateSenderAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(senderIdentity);
        }
    }

    private static CiAiRequestTaskMapping Mapping() =>
        new(1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            "TrentBest/TheSingularityWorkshop.Foundry", "refs/heads/work/task-1",
            new string('a', 40), ".github/workflows/build.yml", "refs/heads/development",
            new string('b', 40), "build", "Resolve candidate", "Verify candidate",
            "ciai-production", "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2));

    private static JsonlCiAiRequestTaskMappingJournal CreateMappings(string path)
    {
        var journal = new JsonlCiAiRequestTaskMappingJournal(path);
        journal.CreateDispatchIntent(Mapping());
        journal.BindRun("request-1", new CiAiDispatchRunBinding(
            12345, 1, "TrentBest/TheSingularityWorkshop.Foundry",
            ".github/workflows/build.yml", "refs/heads/development", new string('b', 40),
            "refs/heads/work/task-1", new string('a', 40), "workflow_dispatch",
            Created.AddMinutes(1)), Created.AddMinutes(1));
        return journal;
    }

    private static byte[] Payload()
    {
        var manifest = new Dictionary<string, object?>
        {
            ["requestId"] = "request-1",
            ["operationId"] = "operation-1",
            ["idempotencyKey"] = "idempotency-1",
            ["providerRepository"] = "TrentBest/TheSingularityWorkshop.CiAi",
            ["capabilityIdentity"] = "workflow-evidence",
            ["capabilityVersion"] = "2",
            ["implementationRevision"] = "7f3d9a",
            ["artifactId"] = "artifact-123",
            ["artifactVersion"] = "v1",
            ["mediaType"] = "application/json",
            ["sizeBytes"] = 1024,
            ["retrievalUri"] = "https://artifacts.example.test/immutable/evidence.json",
            ["digest"] = new string('d', 64),
            ["expiresAt"] = Created.AddHours(1),
            ["sourceSha"] = new string('a', 40),
            ["clientIdentity"] = "foundry-production",
            ["providerRunId"] = "run-456"
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            noticeId = "notice-1",
            recipientIdentity = "foundry-production",
            notifiedAt = "2026-10-31T00:02:00Z",
            manifest
        }));
    }

    private static CiAiArtifactNoticeIngress CreateIngress(
        string mappingPath,
        string receiptPath,
        string sender = "ciai-production")
    {
        return new CiAiArtifactNoticeIngress(
            new TestAuthenticator(sender),
            CreateMappings(mappingPath),
            new CiAiArtifactNoticeValidationPolicy(
                "foundry-production", "TrentBest/TheSingularityWorkshop.CiAi",
                "workflow-evidence", "2", "7f3d9a"),
            new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath));
    }

    [Fact]
    public async Task AuthenticatesValidatesAndPersistsBeforeReturningAccepted()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mappingPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-map-{suffix}.jsonl");
        var receiptPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-receipt-{suffix}.jsonl");
        try
        {
            var ingress = CreateIngress(mappingPath, receiptPath);
            var result = await ingress.AcceptAsync(Payload(), Created.AddMinutes(3));

            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Accepted, result.Outcome);
            Assert.Equal("request-1", result.ValidatedNotice.Request.Mapping.RequestId);
            Assert.Single(new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath).GetAll());
            Assert.Equal(result.Receipt.PayloadSha256,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Payload())).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(mappingPath)) File.Delete(mappingPath);
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
        }
    }

    [Fact]
    public async Task ExactRetryReturnsOriginalDurableReceipt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mappingPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-map-{suffix}.jsonl");
        var receiptPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-receipt-{suffix}.jsonl");
        try
        {
            var ingress = CreateIngress(mappingPath, receiptPath);
            var first = await ingress.AcceptAsync(Payload(), Created.AddMinutes(3));
            var retry = await ingress.AcceptAsync(Payload(), Created.AddMinutes(4));

            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Accepted, first.Outcome);
            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Duplicate, retry.Outcome);
            Assert.Equal(first.Receipt.ReceiptId, retry.Receipt.ReceiptId);
            Assert.Single(new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath).GetAll());
        }
        finally
        {
            if (File.Exists(mappingPath)) File.Delete(mappingPath);
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
        }
    }

    [Fact]
    public async Task InvalidOrUnauthenticatedNoticeNeverWritesReceipt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mappingPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-map-{suffix}.jsonl");
        var receiptPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-receipt-{suffix}.jsonl");
        try
        {
            var ingress = CreateIngress(mappingPath, receiptPath, "unregistered-sender");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await ingress.AcceptAsync(Payload(), Created.AddMinutes(3)));
            Assert.Empty(new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath).GetAll());

            var validIngress = CreateIngress(mappingPath, receiptPath);
            await Assert.ThrowsAsync<FormatException>(async () =>
                await validIngress.AcceptAsync(new byte[] { 0xC3, 0x28 }, Created.AddMinutes(3)));
            Assert.Empty(new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath).GetAll());
        }
        finally
        {
            if (File.Exists(mappingPath)) File.Delete(mappingPath);
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
        }
    }

    [Fact]
    public async Task CancellationBeforeAuthenticationOrPersistenceDoesNotWriteReceipt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mappingPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-map-{suffix}.jsonl");
        var receiptPath = Path.Combine(Path.GetTempPath(), $"foundry-ingress-receipt-{suffix}.jsonl");
        try
        {
            var ingress = CreateIngress(mappingPath, receiptPath);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await ingress.AcceptAsync(Payload(), Created.AddMinutes(3), cancellation.Token));
            Assert.Empty(new JsonlCiAiArtifactNoticeReceiptLedger(receiptPath).GetAll());
        }
        finally
        {
            if (File.Exists(mappingPath)) File.Delete(mappingPath);
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
        }
    }
}
