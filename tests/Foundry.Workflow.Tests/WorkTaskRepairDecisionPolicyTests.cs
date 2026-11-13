using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class WorkTaskRepairDecisionPolicyTests
{
    private static readonly DateTimeOffset TaskStart = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);

    private static WorkTaskRepairAttempt Attempt(int number = 1, char candidate = 'a',
        char diagnostics = 'd', bool successful = false, bool complete = true, bool actionable = true) =>
        new(WorkTaskRepairAttempt.CurrentSchemaVersion, "task-8", "correlation-8", "TrentBest/TheSingularityWorkshop.Foundry",
            "work/task-8", ".github/workflows/build.yml", TaskStart, number,
            new string(candidate, 40), 1000 + number, 1, new string((char)('f' - number), 64),
            complete, successful, successful ? false : actionable,
            actionable && !successful ? new string(diagnostics, 64) : null,
            TaskStart.AddMinutes(number * 3 - 2), TaskStart.AddMinutes(number * 3 - 1))
        { SourceArtifactIdentitySha256 = new string('c', 64) };

    private static WorkTaskRepairEvidenceExpectation Expectation(WorkTaskRepairAttempt attempt) =>
        new(attempt.TaskId, attempt.CorrelationId, attempt.Repository, attempt.Branch,
            attempt.WorkflowPath, attempt.CandidateSha, attempt.TaskStartedAt);

    private static WorkTaskRepairDecisionResult Evaluate(
        IReadOnlyList<WorkTaskRepairAttempt> history, WorkTaskRepairAttempt current,
        int maxAttempts = 3, TimeSpan? maxDuration = null,
        WorkTaskRepairEvidenceExpectation? expectation = null) =>
        WorkTaskRepairDecisionPolicy.Evaluate(history, current, expectation ?? Expectation(current),
            maxAttempts, maxDuration ?? TimeSpan.FromMinutes(30), current.ObservedAt.AddSeconds(1));

    [Fact]
    public void NewActionableFailureRequestsOneBoundedRepairCycle()
    {
        var result = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), Attempt());

        Assert.Equal(WorkTaskRepairDecision.RepairRequired, result.Decision);
        Assert.Equal("repair.actionable-failure", result.ReasonCode);
    }

    [Fact]
    public void CompleteSuccessfulEvidenceMovesTaskToReviewReady()
    {
        var result = Evaluate(Array.Empty<WorkTaskRepairAttempt>(),
            Attempt(successful: true, actionable: false));

        Assert.Equal(WorkTaskRepairDecision.ReviewReady, result.Decision);
    }

    [Fact]
    public void IncompleteEvidenceBlocksAndNonActionableFailureRaisesCodeRed()
    {
        var incomplete = Evaluate(Array.Empty<WorkTaskRepairAttempt>(),
            Attempt(complete: false, actionable: false));
        var nonActionable = Evaluate(Array.Empty<WorkTaskRepairAttempt>(),
            Attempt(actionable: false));

        Assert.Equal(WorkTaskRepairDecision.Blocked, incomplete.Decision);
        Assert.Equal("repair.evidence-incomplete", incomplete.ReasonCode);
        Assert.Equal(WorkTaskRepairDecision.CodeRed, nonActionable.Decision);
        Assert.Equal("repair.failure-not-actionable", nonActionable.ReasonCode);
    }

    [Fact]
    public void UnchangedCandidateOrRepeatedDiagnosticsStopsFutileRepairLoop()
    {
        var previous = Attempt();
        var unchangedCandidate = Attempt(2, 'a', 'e');
        var repeatedDiagnostics = Attempt(2, 'b', 'd');

        var sameSha = Evaluate(new[] { previous }, unchangedCandidate);
        var sameDiagnostics = Evaluate(new[] { previous }, repeatedDiagnostics);

        Assert.Equal(WorkTaskRepairDecision.CodeRed, sameSha.Decision);
        Assert.Equal("repair.candidate-unchanged", sameSha.ReasonCode);
        Assert.Equal(WorkTaskRepairDecision.CodeRed, sameDiagnostics.Decision);
        Assert.Equal("repair.diagnostics-repeated", sameDiagnostics.ReasonCode);
    }

    [Fact]
    public void NewCandidateAndNewDiagnosticsCanContinueWithinBudget()
    {
        var result = Evaluate(new[] { Attempt() }, Attempt(2, 'b', 'e'));

        Assert.Equal(WorkTaskRepairDecision.RepairRequired, result.Decision);
    }

    [Fact]
    public void ExhaustedAttemptOrTimeBudgetBlocksFurtherRepair()
    {
        var finalAttempt = Evaluate(new[] { Attempt() }, Attempt(2, 'b', 'e'), maxAttempts: 2);
        var timedOut = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), Attempt(),
            maxDuration: TimeSpan.FromSeconds(30));

        Assert.Equal(WorkTaskRepairDecision.Blocked, finalAttempt.Decision);
        Assert.Equal("repair.attempt-budget-exhausted", finalAttempt.ReasonCode);
        Assert.Equal(WorkTaskRepairDecision.Blocked, timedOut.Decision);
        Assert.Equal("repair.budget-exhausted", timedOut.ReasonCode);
    }

    [Fact]
    public void OutOfOrderAndAfterSuccessEvidenceCannotAuthorizeAnotherRepair()
    {
        var outOfOrder = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), Attempt(2, 'b', 'e'));
        var afterSuccess = Evaluate(new[] { Attempt(successful: true, actionable: false) },
            Attempt(2, 'b', 'e'));

        Assert.Equal(WorkTaskRepairDecision.Blocked, outOfOrder.Decision);
        Assert.Equal("repair.attempt-sequence-mismatch", outOfOrder.ReasonCode);
        Assert.Equal(WorkTaskRepairDecision.Blocked, afterSuccess.Decision);
        Assert.Equal("repair.attempt-after-success", afterSuccess.ReasonCode);
    }


    [Fact]
    public void EvidenceMustMatchTheExactExpectedCandidateAndWorkflow()
    {
        var attempt = Attempt();
        var wrongCandidate = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with { CandidateSha = new string('b', 40) });
        var wrongWorkflow = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with { WorkflowPath = ".github/workflows/release.yml" });
        var wrongRepository = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with { Repository = "Other/Repository" });
        var wrongRun = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with
            {
                ExpectedCiRunId = attempt.CiRunId + 1,
                ExpectedCiRunAttempt = attempt.CiRunAttempt
            });
        var wrongRunAttempt = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with
            {
                ExpectedCiRunId = attempt.CiRunId,
                ExpectedCiRunAttempt = attempt.CiRunAttempt + 1
            });
        var matchingRun = Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
            expectation: Expectation(attempt) with
            {
                ExpectedCiRunId = attempt.CiRunId,
                ExpectedCiRunAttempt = attempt.CiRunAttempt
            });

        Assert.Equal(WorkTaskRepairDecision.Blocked, wrongCandidate.Decision);
        Assert.Equal(WorkTaskRepairDecision.Blocked, wrongWorkflow.Decision);
        Assert.Equal(WorkTaskRepairDecision.Blocked, wrongRepository.Decision);
        Assert.Equal(WorkTaskRepairDecision.Blocked, wrongRun.Decision);
        Assert.Equal(WorkTaskRepairDecision.Blocked, wrongRunAttempt.Decision);
        Assert.Equal(WorkTaskRepairDecision.RepairRequired, matchingRun.Decision);
        Assert.Equal("repair.evidence-provenance-mismatch", wrongCandidate.ReasonCode);
    }

    [Fact]
    public void PolicyRejectsInvalidBudgetsAndFutureEvidence()
    {
        var attempt = Attempt();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkTaskRepairDecisionPolicy.Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt, Expectation(attempt), 11,
                TimeSpan.FromMinutes(30), attempt.ObservedAt.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() =>
            WorkTaskRepairDecisionPolicy.Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt, Expectation(attempt), 3,
                TimeSpan.FromMinutes(30), attempt.ObservedAt.AddSeconds(-1)));
        Assert.Throws<ArgumentException>(() =>
            WorkTaskRepairDecisionPolicy.Evaluate(Array.Empty<WorkTaskRepairAttempt>(), attempt,
                Expectation(attempt) with { ExpectedCiRunId = attempt.CiRunId }, 3,
                TimeSpan.FromMinutes(30), attempt.ObservedAt.AddSeconds(1)));
    }
}
