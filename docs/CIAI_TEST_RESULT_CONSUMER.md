<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry CiAi Test-Result Consumer

## 01 — What

Foundry consumes the ciai.test-results.v1 report only after the host has authenticated the registered CiAi sender, validated the artifact-ready notice against the persisted dispatch mapping, retrieved the artifact, and verified its declared size and SHA-256 digest.

FoundryVerifiedCiAiReportArtifact is the provider-neutral, immutable Foundry content value; the host adapts the verified bytes from the ingress/retrieval layer into it. CiAiTestResultReportConsumer is the provider-neutral report consumer. It checks the report's schema and exact binding to the task's persisted dispatch/run identity, then creates:

- a WorkTaskRepairAttempt whose evidence flags are derived exclusively from CiAi's named verdict; and
- a bounded FoundryHeadlessAiEvidence context item for the next HeadlessAi turn.

The consumer does not reference the CiAi assembly or the separate ingress implementation project. It consumes the versioned JSON contract and Foundry's persisted dispatch-mapping contract, keeping this slice independently buildable on development.

## 02 — Why

Foundry must not create a second CI verdict engine. Raw job status, caller-provided booleans, TRX contents, and model interpretations are not substitutes for CiAi's policy verdict.

At the same time, a valid CiAi report must not be allowed to attach itself to an unrelated task, candidate, workflow, or run. The consumer binds the report to the exact CiAiRequestTaskMappingSnapshot retained by the verified artifact notice:

- task and correlation identity;
- repository and candidate ref/SHA;
- trusted workflow path/ref/SHA;
- workflow-dispatch event identity;
- exact GitHub run ID and attempt; and
- registered producing job name.

The Foundry content factory verifies the report artifact's bytes against the digest and size supplied by the already-validated notice. The report consumer hashes the bytes again when recording the attempt. These checks do not replace sender authentication, notice validation, or artifact transport security.

## 03 — How

The host sequence is:

1. Authenticate the notice sender through the configured host adapter.
2. Validate the notice against the trusted provider registration and exact persisted request/run mapping.
3. Retrieve the artifact through a transport that enforces connection-time network policy, redirect restrictions, timeout, and byte limits.
4. Verify the retrieved bytes against the notice's declared digest and size.
5. Restore the Foundry task snapshot and adapt the already-verified artifact bytes into FoundryVerifiedCiAiReportArtifact, then call CiAiTestResultReportConsumer.Consume(verifiedContent, taskSnapshot, now).
6. Append the returned WorkTaskRepairAttempt to the durable attempt journal and pass it through Foundry's bounded lifecycle policy.
7. If the verdict is actionable and budgets permit, supply the returned FoundryHeadlessAiEvidence to the same correlated task's next HeadlessAi turn. The model response remains a proposal and requires independent host authorization before any repository mutation.

### Verdict mapping

| CiAi verdict | Foundry attempt flags |
|---|---|
| CompletePass | complete, successful, not actionable |
| ActionableFailure | complete, unsuccessful, actionable |
| IncompleteOrUntrusted | incomplete, unsuccessful, not actionable |
| NonActionableFailure | complete, unsuccessful, not actionable |

Foundry validates the structural consistency of the verdict with required-check status and job conclusion, but does not inspect test failures to independently decide repairability. Stable CiAi reason codes and bounded failed-test diagnostics are carried forward as untrusted HeadlessAi context.

### Limits

This slice is a consumer boundary, not the complete CODE RED loop. It does not host HTTPS, authenticate JWTs, download provider artifacts, verify ZIP archives, safely extract TRX, create CI reports, dispatch HeadlessAi, authorize repository commands, or provide distributed persistence. The current Foundry JSONL journals remain local-filesystem, single-process stores.

## 04 — See it in a minute

The executable contract tests cover:

- conversion of ActionableFailure into a provenance-bound repair attempt and HeadlessAi evidence;
- conversion of CompletePass without caller-supplied success flags;
- rejection of a candidate SHA that differs from the persisted dispatch; and
- rejection of duplicate JSON properties.

The tests use a deterministic verified-artifact fixture. They do not claim a live provider retrieval or production host integration.

## 05 — Choose your next step

- Complete the lifecycle handoff by appending the consumed attempt idempotently, applying the bounded Foundry policy, and resuming the same correlated HeadlessAi task.
- Integrate a production host adapter with authenticated callback ingress and a connection-time-safe artifact transport.
- Prove the full loop against the Experiences Minesweeper red artifact and a new exact-candidate green validation.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
