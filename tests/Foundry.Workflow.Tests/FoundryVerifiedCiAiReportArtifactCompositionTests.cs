using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class FoundryVerifiedCiAiReportArtifactCompositionTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
    private static readonly DateTimeOffset RunBoundAt = DateTimeOffset.Parse("2026-10-31T10:15:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T11:00:00Z");
    private static readonly DateTimeOffset ExpiresAt = DateTimeOffset.Parse("2026-10-31T11:30:00Z");
    private const string Repository = "owner/repo";
    private const string CandidateRef = "refs/heads/work/task-1";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WorkflowPath = ".github/workflows/ci.yml";
    private const string WorkflowRef = "refs/heads/development";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const long RunId = 123456789;
    private const int RunAttempt = 1;

    [Fact]
    public void AdaptsVerifiedNoticeContentWithoutChangingBytesOrProvenance()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"Schema\":\"ciai.test-results.v1\",\"Verdict\":\"CompletePass\"}");
        var mapping = new CiAiRequestTaskMapping(
            1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            Repository, CandidateRef, CandidateSha, WorkflowPath, WorkflowRef, WorkflowSha,
            "test-minesweeper", "resolve", "verify", "ciai-provider",
            "https://artifacts.example.com/", "https://foundry.example.com/callback", "auth-profile",
            CreatedAt, CreatedAt.AddHours(2), TrustedEventSha: EventSha).Validate();
        var run = new CiAiDispatchRunBinding(
            RunId, RunAttempt, Repository, WorkflowPath, WorkflowRef, WorkflowSha,
            CandidateRef, CandidateSha, "workflow_dispatch", RunBoundAt, EventSha: EventSha)
            .ValidateAgainst(mapping);
        var snapshot = new CiAiRequestTaskMappingSnapshot(mapping, run, false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var notice = new CiAiValidatedArtifactNotice(
            snapshot,
            "notice-1",
            Now.AddMinutes(-5),
            "TrentBest/TheSingularityWorkshop.CiAi",
            "workflow-evidence",
            "2",
            "producer-revision",
            "artifact-1",
            "v1",
            "application/json",
            bytes.Length,
            new Uri("https://artifacts.example.com/immutable/report.json"),
            digest,
            ExpiresAt,
            RunId.ToString(),
            "foundry-client",
            new string('c', 64));

        var retrieved = CiAiArtifactContentVerifier.Verify(notice, bytes, Now);
        var reportArtifact = FoundryVerifiedCiAiReportArtifact.FromVerifiedArtifactContent(retrieved, Now);

        Assert.Equal(bytes, reportArtifact.Bytes.ToArray());
        Assert.Equal(digest, reportArtifact.Digest);
        Assert.Equal(bytes.Length, reportArtifact.SizeBytes);
        Assert.Equal(ExpiresAt, reportArtifact.ExpiresAt);
        Assert.Equal(RunId.ToString(), reportArtifact.ProviderRunId);
        Assert.Equal("application/json", reportArtifact.MediaType);
        Assert.Same(snapshot, reportArtifact.Request);
    }
}
