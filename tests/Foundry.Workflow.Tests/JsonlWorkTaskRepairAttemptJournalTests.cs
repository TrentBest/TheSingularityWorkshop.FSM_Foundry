using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlWorkTaskRepairAttemptJournalTests
{
    private static readonly DateTimeOffset TaskStart = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);

    private static WorkTaskRepairAttempt Attempt(int number = 1, char candidate = 'a',
        char diagnostics = 'd', bool successful = false, bool complete = true, bool actionable = true,
        long? runId = null, int runAttempt = 1) =>
        new(1, "task-8", "correlation-8", "TrentBest/TheSingularityWorkshop.Foundry",
            "work/task-8", ".github/workflows/build.yml", TaskStart, number,
            new string(candidate, 40), runId ?? 1000 + number, runAttempt,
            new string((char)('f' - number), 64), complete, successful,
            successful ? false : actionable, actionable && !successful ? new string(diagnostics, 64) : null,
            TaskStart.AddMinutes(number * 3 - 2), TaskStart.AddMinutes(number * 3 - 1))
        { SourceArtifactIdentitySha256 = new string('c', 64) };

    [Fact]
    public void JournalPersistsEveryExactAttemptAndReplaysAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-repair-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(path);
            var first = Attempt();
            var second = Attempt(2, 'b', 'e');

            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(first));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Duplicate, journal.Append(first));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(second));

            var reopened = new JsonlWorkTaskRepairAttemptJournal(path);
            var history = reopened.GetAttempts("task-8");
            Assert.Equal(2, history.Count);
            Assert.Equal(first.CandidateSha, history[0].CandidateSha);
            Assert.Equal(second.CandidateSha, history[1].CandidateSha);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JournalRejectsOutOfOrderConflictingAndRepeatedRunAttemptEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-repair-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(path);
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.OutOfOrder, journal.Append(Attempt(2, 'b', 'e')));
            var first = Attempt();
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(first));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Conflict,
                journal.Append(first with { CandidateSha = new string('c', 40) }));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Conflict,
                journal.Append(Attempt(2, 'b', 'e', runId: first.CiRunId, runAttempt: first.CiRunAttempt)));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Conflict,
                journal.Append(first with { TaskId = "task-9", CorrelationId = "correlation-9" }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JournalRejectsAttemptsAfterSuccessAndTornFinalRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-repair-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(path);
            var success = Attempt(successful: true, actionable: false);
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(success));
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Conflict, journal.Append(Attempt(2, 'b', 'e')));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        var tornPath = Path.Combine(Path.GetTempPath(), $"foundry-repair-torn-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(tornPath, System.Text.Json.JsonSerializer.Serialize(Attempt()));
            Assert.Throws<InvalidDataException>(() => new JsonlWorkTaskRepairAttemptJournal(tornPath));
        }
        finally
        {
            if (File.Exists(tornPath)) File.Delete(tornPath);
        }
    }

    [Fact]
    public void JournalRejectsMalformedAttemptEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-repair-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(path);
            Assert.Throws<ArgumentException>(() => journal.Append(Attempt() with
            {
                EvidencePayloadSha256 = "not-a-digest"
            }));
            Assert.Throws<ArgumentException>(() => journal.Append(Attempt() with
            {
                WorkflowPath = "../../unsafe.yml"
            }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    [Fact]
    public void DecisionCallbackSerializesAgainstConcurrentAttemptAppend()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foundry-repair-lock-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlWorkTaskRepairAttemptJournal(path);
            var first = Attempt();
            var second = Attempt(2, 'b', 'e');
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, journal.Append(first));

            using var callbackEntered = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            using var appendStarted = new ManualResetEventSlim();

            var decision = Task.Run(() => journal.ExecuteWithAttempts("task-8", history =>
            {
                Assert.Single(history);
                callbackEntered.Set();
                Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
                Assert.Single(history);
                return history[^1];
            }));

            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            var append = Task.Run(() =>
            {
                appendStarted.Set();
                return journal.Append(second);
            });
            Assert.True(appendStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(append.Wait(TimeSpan.FromMilliseconds(100)));

            releaseCallback.Set();
            Assert.Equal(first, decision.GetAwaiter().GetResult());
            Assert.Equal(WorkTaskRepairAttemptAppendOutcome.Appended, append.GetAwaiter().GetResult());
            Assert.Equal(2, journal.GetAttempts("task-8").Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
