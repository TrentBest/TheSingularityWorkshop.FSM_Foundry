using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactReadyNoticeValidatorTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static CiAiRequestTaskMapping Mapping() =>
        new(1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            "TrentBest/TheSingularityWorkshop.Foundry", "refs/heads/work/task-1",
            new string('a', 40), ".github/workflows/build.yml", "refs/heads/development",
            new string('b', 40), "build", "Resolve candidate", "Verify candidate",
            "ciai-production", "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2));

    private static CiAiArtifactNoticeValidationPolicy Policy() =>
        new("foundry-production", "TrentBest/TheSingularityWorkshop.CiAi",
            "workflow-evidence", "2", "7f3d9a");

    private static JsonlCiAiRequestTaskMappingJournal CreateJournal(string path, bool bind = true)
    {
        var journal = new JsonlCiAiRequestTaskMappingJournal(path);
        journal.CreateDispatchIntent(Mapping());
        if (bind)
        {
            var binding = new CiAiDispatchRunBinding(
                12345, 1, "TrentBest/TheSingularityWorkshop.Foundry",
                ".github/workflows/build.yml", "refs/heads/development", new string('b', 40),
                "refs/heads/work/task-1", new string('a', 40), "workflow_dispatch",
                Created.AddMinutes(1));
            journal.BindRun("request-1", binding, Created.AddMinutes(1));
        }
        return journal;
    }

    private static string Payload(
        string? sourceSha = null,
        string? retrievalUri = null,
        string? recipientIdentity = null,
        string? providerRepository = null,
        string? capabilityIdentity = null,
        string? clientIdentity = null,
        string? digest = null,
        long sizeBytes = 1024,
        string? additionalProperty = null,
        string? notifiedAt = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["requestId"] = "request-1",
            ["operationId"] = "operation-1",
            ["idempotencyKey"] = "idempotency-1",
            ["providerRepository"] = providerRepository ?? "TrentBest/TheSingularityWorkshop.CiAi",
            ["capabilityIdentity"] = capabilityIdentity ?? "workflow-evidence",
            ["capabilityVersion"] = "2",
            ["implementationRevision"] = "7f3d9a",
            ["artifactId"] = "artifact-123",
            ["artifactVersion"] = "v1",
            ["mediaType"] = "application/json",
            ["sizeBytes"] = sizeBytes,
            ["retrievalUri"] = retrievalUri ?? "https://artifacts.example.test/immutable/evidence.json",
            ["digest"] = digest ?? new string('d', 64),
            ["expiresAt"] = Created.AddHours(1),
            ["sourceSha"] = sourceSha ?? new string('a', 40),
            ["clientIdentity"] = clientIdentity ?? "foundry-production",
            ["providerRunId"] = "run-456"
        };
        if (additionalProperty is not null)
            manifest["unexpected"] = additionalProperty;

        return JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            noticeId = "notice-1",
            recipientIdentity = recipientIdentity ?? "foundry-production",
            notifiedAt = notifiedAt ?? "2026-10-31T00:02:00Z",
            manifest
        });
    }

    private static CiAiArtifactReadyNotice Notice(string payload, string sender = "ciai-production") =>
        CiAiArtifactReadyNotice.FromUtf8Bytes(
            sender, "notice-1", "idempotency-1", Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void ValidatesCompleteNoticeAgainstAuthenticatedProviderAndExactBoundRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-ciai-validator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = CreateJournal(path);
            var result = CiAiArtifactReadyNoticeValidator.Validate(
                Notice(Payload()), journal, Policy(), Created.AddMinutes(3));

            Assert.Equal("request-1", result.Request.Mapping.RequestId);
            Assert.Equal(12345, result.Request.RunBinding!.RunId);
            Assert.Equal("artifact-123", result.ArtifactId);
            Assert.Equal("https://artifacts.example.test/immutable/evidence.json", result.RetrievalUri.AbsoluteUri);
            Assert.Equal(new string('d', 64), result.Digest);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RejectsWrongSenderRecipientProviderCapabilityAndCandidateSha()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-ciai-validator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = CreateJournal(path);
            var now = Created.AddMinutes(3);

            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(), sender: "unregistered-sender"), journal, Policy(), now));
            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(recipientIdentity: "other-host")), journal, Policy(), now));
            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(providerRepository: "attacker/producer")), journal, Policy(), now));
            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(capabilityIdentity: "unregistered-capability")), journal, Policy(), now));
            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(sourceSha: new string('c', 40))), journal, Policy(), now));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RejectsUntrustedRetrievalOriginMalformedDigestAndOversizedArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-ciai-validator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = CreateJournal(path);
            var now = Created.AddMinutes(3);

            Assert.Throws<UnauthorizedAccessException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(retrievalUri: "https://attacker.example.test/evidence.json")),
                    journal, Policy(), now));
            Assert.Throws<FormatException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(digest: "not-a-sha256")), journal, Policy(), now));
            Assert.Throws<FormatException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(sizeBytes: 16 * 1024 * 1024 + 1)),
                    journal, Policy(), now));
            Assert.Throws<FormatException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(additionalProperty: "unexpected")),
                    journal, Policy(), now));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RejectsTimestampsWithoutAnExplicitUtcOffset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-ciai-validator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = CreateJournal(path);
            Assert.Throws<FormatException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload(notifiedAt: "2026-10-31T00:02:00")),
                    journal, Policy(), Created.AddMinutes(3)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void UnboundOrExpiredDispatchIntentCannotAuthorizeArtifactNotice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-ciai-validator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = CreateJournal(path, bind: false);
            Assert.Throws<InvalidOperationException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload()), journal, Policy(), Created.AddMinutes(3)));
            Assert.Throws<InvalidOperationException>(() =>
                CiAiArtifactReadyNoticeValidator.Validate(
                    Notice(Payload()), journal, Policy(), Created.AddHours(3)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
