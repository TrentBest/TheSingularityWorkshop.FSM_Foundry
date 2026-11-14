using TheSingularityWorkshop.HeadlessAi;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// Concrete in-process host adapter from Foundry's lease-governed turn port to HeadlessAi's
/// immutable request snapshot and one-shot provider executor. It owns no credentials or GitHub access.
/// </summary>
public sealed class HeadlessAiAgentTurnClient : IHeadlessAiAgentTurnClient
{
    private const string TaskVersion = "foundry.work-task.v1";
    private readonly HeadlessAiAgentTurnExecutor _executor;
    public HeadlessAiAgentTurnClient(HeadlessAiAgentTurnExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Maps and validates the Foundry request and creates the immutable HeadlessAi snapshot.
    /// This method performs no provider or network operation.
    /// </summary>
    public IFoundryHeadlessAiPreparedTurn Prepare(
        FoundryHeadlessAiTurnRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(now);

        var envelope = new HeadlessAiAgentTurnEnvelope
        {
            TaskId = request.TaskId,
            ConversationId = request.TaskId,
            TaskVersion = TaskVersion,
            CorrelationId = request.CorrelationId,
            Repository = request.Repository,
            AuthorizedBranch = request.AuthorizedBranch,
            BaseCommitSha = request.BaseCommitSha,
            CandidateCommitSha = request.CandidateCommitSha,
            IssueUrl = request.IssueUrl,
            Role = request.Role,
            Instructions = request.Instructions,
            CanonicalPrompt = request.CanonicalPrompt,
            ResponseSchema = request.ResponseSchema,
            ExpiresAt = request.ExpiresAt,
            PriorTurnId = request.PriorTurnId,
            PriorCandidateCommitSha = request.PriorCandidateCommitSha,
            IdempotencyKey = request.IdempotencyKey,
            Evidence = request.Evidence.Select(item => new HeadlessAiEvidenceReference
            {
                Repository = item.Repository,
                CommitSha = item.CommitSha,
                WorkflowRunId = item.WorkflowRunId,
                Attempt = item.Attempt,
                WorkflowName = item.WorkflowName,
                JobName = item.JobName,
                StepName = item.StepName,
                Conclusion = item.Conclusion,
                SourceUrl = item.SourceUrl,
                ObservedAt = item.ObservedAt,
                BoundedSummary = item.BoundedSummary
            }).ToArray(),
            ContextReferences = Array.Empty<string>()
        };

        HeadlessAiAcceptedCiReportContext? acceptedReport = null;
        if (request.AcceptedCiAiReport is { } foundryReport)
        {
            HeadlessAiAcceptedCiSourceArtifactContext? sourceArtifact = null;
            if (foundryReport.SourceArtifactBinding is { } foundrySource)
            {
                sourceArtifact = new HeadlessAiAcceptedCiSourceArtifactContext
                {
                    Repository = foundrySource.Repository,
                    WorkflowPath = foundrySource.WorkflowPath,
                    RunId = foundrySource.RunId,
                    RunAttempt = foundrySource.RunAttempt,
                    CandidateCommitSha = foundrySource.CandidateSha,
                    JobId = foundrySource.JobId,
                    JobName = foundrySource.JobName,
                    JobKey = foundrySource.JobKey,
                    ArtifactId = foundrySource.ArtifactId,
                    ArtifactName = foundrySource.ArtifactName,
                    ProviderArchiveDigest = foundrySource.ProviderArchiveDigest,
                    ArchiveSizeBytes = foundrySource.ArchiveSizeBytes,
                    JobStartedAt = foundrySource.JobStartedAt,
                    JobCompletedAt = foundrySource.JobCompletedAt,
                    ArtifactCreatedAt = foundrySource.ArtifactCreatedAt,
                    ArtifactExpiresAt = foundrySource.ArtifactExpiresAt,
                    PayloadSha256 = foundrySource.PayloadSha256,
                    PayloadSizeBytes = foundrySource.PayloadSizeBytes,
                    IdentitySha256 = foundrySource.IdentitySha256
                };
            }

            acceptedReport = new HeadlessAiAcceptedCiReportContext
            {
                SchemaVersion = foundryReport.SchemaVersion,
                Verdict = foundryReport.Verdict,
                Repository = foundryReport.Repository,
                WorkflowPath = foundryReport.WorkflowPath,
                ReportObservedAt = foundryReport.ReportObservedAt,
                ReportIdentity = foundryReport.ReportIdentity,
                NoticeIdentitySha256 = foundryReport.NoticeIdentitySha256,
                CanonicalReportJson = foundryReport.CanonicalReportJson,
                ReportSha256 = foundryReport.ReportSha256,
                WorkflowRunId = foundryReport.WorkflowRunId,
                RunAttempt = foundryReport.RunAttempt,
                CandidateCommitSha = foundryReport.CandidateCommitSha,
                SourceArtifact = sourceArtifact
            };
        }

        var snapshot = acceptedReport is null
            ? HeadlessAiAgentTurnRequest.Create(envelope, now)
            : HeadlessAiAgentTurnRequest.Create(envelope, acceptedReport, now);
        return new PreparedTurn(request, snapshot);
    }

    /// <summary>Executes exactly the immutable request snapshot produced by Prepare.</summary>
    public async Task<FoundryHeadlessAiTurnResult> ExecuteAsync(
        FoundryHeadlessAiTurnRequest request,
        IFoundryHeadlessAiPreparedTurn prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prepared);

        if (prepared is not PreparedTurn snapshot || !ReferenceEquals(snapshot.SourceRequest, request))
            throw new ArgumentException(
                "The prepared HeadlessAi snapshot was not created for this exact Foundry request instance.",
                nameof(prepared));

        var result = await _executor.ExecuteAsync(snapshot.Request, cancellationToken).ConfigureAwait(false);
        result.Validate();

        if (result.CandidateCommitSha is null)
            throw new InvalidOperationException("HeadlessAi returned no candidate SHA for a Foundry candidate turn.");

        var proposal = result.Proposal;
        var mapped = new FoundryHeadlessAiTurnResult
        {
            TurnId = result.TurnId,
            TaskId = result.TaskId,
            CorrelationId = result.CorrelationId,
            Repository = result.Repository,
            AuthorizedBranch = result.AuthorizedBranch,
            BaseCommitSha = result.BaseCommitSha,
            CandidateCommitSha = result.CandidateCommitSha,
            PriorTurnId = result.PriorTurnId,
            PriorCandidateCommitSha = result.PriorCandidateCommitSha,
            IdempotencyKey = result.IdempotencyKey,
            RequestFingerprint = result.RequestFingerprint,
            ProviderId = result.ProviderId,
            ModelId = result.ModelId,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            AcceptedReportSchemaVersion = result.AcceptedReportSchemaVersion,
            AcceptedReportVerdict = result.AcceptedReportVerdict,
            AcceptedReportIdentity = result.AcceptedReportIdentity,
            AcceptedReportNoticeIdentitySha256 = result.AcceptedReportNoticeIdentitySha256,
            AcceptedReportSha256 = result.AcceptedReportSha256,
            AcceptedReportWorkflowRunId = result.AcceptedReportWorkflowRunId,
            AcceptedReportRunAttempt = result.AcceptedReportRunAttempt,
            AcceptedReportCandidateCommitSha = result.AcceptedReportCandidateCommitSha,
            AcceptedReportSourceArtifactIdentitySha256 = result.AcceptedReportSourceArtifactIdentitySha256,
            ErrorCode = result.ErrorCode,
            Diagnosis = proposal.Diagnosis,
            PatchIntent = proposal.PatchIntent,
            Questions = proposal.Questions.ToArray(),
            Confidence = proposal.Confidence,
            Uncertainty = proposal.Uncertainty,
            OperationalStatus = proposal.OperationalStatus,
            Diagnostic = result.Diagnostic,
            FinishReason = result.FinishReason,
            CurrentAction = proposal.CurrentAction,
            Blocker = proposal.Blocker,
            NextExpectedUpdate = proposal.NextExpectedUpdate,
            StopOrEscalateReason = proposal.StopOrEscalateReason,
            HumanDecisionRequired = proposal.HumanDecisionRequired
        };

        mapped.ValidateAgainst(request);
        if (!string.Equals(mapped.RequestFingerprint, snapshot.Request.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("HeadlessAi result fingerprint does not match the prepared immutable request.");

        return mapped;
    }

    private sealed class PreparedTurn(
        FoundryHeadlessAiTurnRequest sourceRequest,
        HeadlessAiAgentTurnRequest request) : IFoundryHeadlessAiPreparedTurn
    {
        public FoundryHeadlessAiTurnRequest SourceRequest { get; } = sourceRequest;
        public HeadlessAiAgentTurnRequest Request { get; } = request;
        public string RequestFingerprint => Request.Fingerprint;
    }
}
