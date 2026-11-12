<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry CiAi Test-Result Consumer

## 01 — What

Foundry consumes the ciai.test-results.v1 report only after the host has authenticated the registered CiAi sender, validated the artifact-ready notice against the persisted dispatch mapping, retrieved the artifact, and verified its declared size and SHA-256 digest.

FoundryVerifiedCiAiReportArtifact is the provider-neutral, immutable Foundry content value; after retrieval verification, the host supplies the validated notice's request snapshot, media type, digest, size, expiry, and provider run ID to its `Verify` factory. This keeps the report consumer independent of the HTTP ingress and transport implementation. CiAiTestResultReportConsumer is the provider-neutral report consumer. It checks the report's schema and exact binding to the task's persisted dispatch/run identity, then creates:

- a WorkTaskRepairAttempt whose evidence flags are derived exclusively from CiAi's named verdict; and
- a bounded FoundryHeadlessAiEvidence context item for the next HeadlessAi turn.

The consumer does not reference the CiAi assembly. It consumes the versioned JSON contract and Foundry's persisted dispatch-mapping contract. The host-neutral ingress and retrieval contracts are now composed with this consumer on draft PR #15; see [the host composition](CIAI_ARTIFACT_HOST_COMPOSITION.md).

## 02 — Why

Foundry must not create a second CI verdict engine. Raw job status, caller-provided booleans, TRX contents, and model interpretations are not substitutes for CiAi's policy verdict.

At the same time, a valid CiAi report must not be allowed to attach itself to an unrelated task, candidate, workflow, or run. The consumer binds the report to the exact CiAiRequestTaskMappingSnapshot retained by the verified artifact notice:

- task and correlation identity;
- repository and candidate ref/SHA (the code being tested);
- trusted workflow path/ref/SHA (the control-plane workflow);
- workflow-dispatch event ref (the trusted development branch) and its separately persisted event SHA; this is not assumed to equal the workflow-file SHA or the candidate SHA;
- exact GitHub run ID and attempt; and
- registered producing job name.

For complete and failure verdicts, Foundry also validates the nested source-artifact claim carried by the authenticated, registered CiAi capability: positive provider artifact ID, bounded provider artifact name, source job key matching the registered job, SHA-256 archive identity, archive-size limit, payload SHA-256/size, and source-artifact creation/expiry window relative to the exact run and report observation. These are claims attested by the registered CiAi implementation revision; Foundry does not download the nested source ZIP itself. The host/CiAi side remains responsible for independently observing provider artifact metadata, verifying the ZIP digest, safely extracting the exact TRX/provenance entries, and constructing the trusted source descriptor.

The Foundry content factory verifies the report artifact's bytes against the digest and size supplied by the already-validated notice. The report consumer hashes the bytes again when recording the attempt. These checks do not replace sender authentication, notice validation, or artifact transport security.

## 03 — How

The host sequence is:

1. Authenticate the notice sender through the configured host adapter.
2. Validate the notice against the trusted provider registration and exact persisted request/run mapping.
3. Retrieve the artifact through a transport that enforces connection-time network policy, redirect restrictions, timeout, and byte limits.
4. Verify the retrieved bytes against the notice's declared digest and size.
5. Restore the Foundry task snapshot and call `FoundryVerifiedCiAiReportArtifact.Verify` with the exact `CiAiRequestTaskMappingSnapshot` retained by the validated notice, the retrieved `CiAiVerifiedArtifactContent.Bytes`, and the digest, size, expiry, provider run ID, and media type from that same notice.
6. Call PersistentWorkTaskCoordinator.ApplyCiAiReport. Foundry consumes the report only for the active validation attempt, appends the exact attempt idempotently, and applies the existing bounded lifecycle policy; a repeated delivery already reflected in task history is acknowledged without a second transition. A CODE RED task must first pass the host approval/recovery path.
7. When the result is RepairRequired, supply the returned FoundryHeadlessAiEvidence to FoundryHeadlessAiTurnCoordinator.InvokeAsync with the prior turn ID and prior candidate SHA. The repair turn remains attached to the same task/lease, and Foundry verifies that the prior turn ID resolves to a completed turn for that same task, correlation, repository, branch, base SHA, and prior candidate SHA. The model response remains a proposal and requires independent host authorization before any repository mutation.

### Verdict mapping

| CiAi verdict | Foundry attempt flags |
|---|---|
| CompletePass | complete, successful, not actionable |
| ActionableFailure | complete, unsuccessful, actionable |
| IncompleteOrUntrusted | incomplete, unsuccessful, not actionable |
| NonActionableFailure | complete, unsuccessful, not actionable |

Foundry validates the structural consistency of the verdict with required-check status and job conclusion, but does not inspect test failures to independently decide repairability. Stable CiAi reason codes and bounded failed-test diagnostics are carried forward as untrusted HeadlessAi context.

### Limits

This slice connects the verified report to Foundry's durable local task/attempt lifecycle and the existing HeadlessAi turn bridge, but it is not the complete CODE RED loop. It does not host HTTPS, authenticate JWTs, download provider artifacts, verify ZIP archives, safely extract TRX, create CI reports, automatically invoke HeadlessAi, authorize repository commands, or provide distributed persistence. The current Foundry JSONL journals remain local-filesystem, single-process stores.

### Canonical v1 byte contract

The consumer accepts the exact canonical bytes emitted by CiAi's `CiTestResultReport.ToCanonicalUtf8Bytes()`:

- UTF-8 without BOM, compact JSON, no trailing newline.
- camelCase property names; nested test-result fields are camelCase as well.
- String enum values and explicit null-valued properties.
- The producer's `ReportIdentity` is `sha256:` plus lowercase SHA-256 over these exact bytes; the body does not contain the derived identity.
- The shared maximum report size is 1 MiB. The verified artifact factory and report consumer both enforce this bound before parsing.

Foundry hashes and validates the exact retrieved bytes, then stores the digest as the evidence payload identity. It does not reserialize the report to compute a different identity, and it does not reinterpret CiAi's verdict. Unknown reason codes remain bounded diagnostic data; they do not change the verdict.

### Accepted report handoff to HeadlessAi

A successful `CiAiTestResultReportConsumer.Consume` also creates a host-only `FoundryAcceptedCiAiReportContext`. It preserves the exact validated UTF-8 report text, `sha256:<digest>` identity, lowercase digest, schema, run/attempt, repository, and tested candidate SHA. Its constructor is internal so callers cannot manufacture an accepted context from an unverified artifact.

The context is carried on `FoundryHeadlessAiTurnRequest.AcceptedCiAiReport`. Repair turns require it and require the report candidate to equal the prior candidate being repaired. The context is revalidated against the current/prior candidate, repository, exact-run evidence reference, byte cap, and digest before the host adapter prepares the HeadlessAi request. The accepted report identity/digest/run/candidate are included in the turn idempotency fingerprint.

The Foundry port remains independent of the HeadlessAi assembly. The host adapter maps the context directly to `HeadlessAiAcceptedCiReportContext` and calls the existing accepted-report `HeadlessAiAgentTurnRequest.Create(envelope, acceptedReport, now)` overload. It must preserve the report JSON unchanged; HeadlessAi still treats it as opaque evidence and returns only an untrusted proposal.

## 04 — See it in a minute

The executable contract tests cover:

- conversion of ActionableFailure into a provenance-bound repair attempt and HeadlessAi evidence;
- conversion of CompletePass without caller-supplied success flags;
- rejection of a candidate SHA that differs from the persisted dispatch; and
- rejection of duplicate JSON properties;
- idempotent durable attempt application; and
- a same-task HeadlessAi repair turn carrying the CiAi evidence and prior-turn provenance.

The consumer tests use deterministic verified-artifact fixtures. `CiAiArtifactHostCompositionTests` additionally composes a fake request authenticator and fake provider transport through durable notice receipt, exact byte retrieval/verification, report consumption, idempotent task application, and the same-task HeadlessAi repair request. This proves the host-neutral contracts compose; it does not claim a live provider retrieval or production host integration.

## 05 — Choose your next step

- Implement the production host adapter with real JWT validation and connection-time-safe HTTPS/DNS transport.
- Prove the full loop against the Experiences Minesweeper red artifact and a new exact-candidate green validation.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
