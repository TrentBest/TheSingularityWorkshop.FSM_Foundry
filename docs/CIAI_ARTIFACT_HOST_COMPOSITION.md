<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Host Composition

## 01 — What

Foundry now composes the host-neutral v1 notice-ingress and artifact-retrieval contracts with its existing CiAi report consumer and same-task HeadlessAi repair-turn bridge.

This is a deterministic host-composition proof. The fake authenticator and fake transport are test adapters, not production security implementations.

## 02 — Why

The report consumer must not be reachable through an untrusted callback or a caller-constructed "verified" report. Each trust transition has a distinct owner and evidence boundary:

- The host authenticates the sender and supplies the registered sender identity.
- Foundry validates the exact notice against trusted provider registration and a persisted dispatch/run binding.
- The host-owned transport retrieves only the validated URI; Foundry rejects redirects, changed final URIs, unexpected status/media type, oversized content, expired artifacts, and digest/length mismatches.
- Foundry verifies the exact bytes and passes those same bytes to the ciai.test-results.v1 consumer.
- The consumer binds the report to the exact task, candidate, workflow, event, run, attempt, and registered job; only CiAi's named verdict determines evidence completeness and actionability.
- Foundry durably applies the attempt and may prepare a same-task HeadlessAi repair turn. The model response is a proposal, never repository authority.

## 03 — How

The integrated path is:

1. Persist the immutable dispatch intent and independently verified exact run binding in JsonlCiAiRequestTaskMappingJournal.
2. Receive the exact raw notice bytes. ICiAiArtifactNoticeAuthenticator authenticates the request-scoped sender; credentials remain outside Foundry and model context.
3. CiAiArtifactNoticeIngress.AcceptAsync strictly decodes UTF-8, validates the v1 notice and its full mapping/provider/capability/source-SHA binding, then persists the receipt. An exact retry returns the original receipt; a conflict is not accepted.
4. CiAiArtifactRetrievalCoordinator passes the validated URI unchanged to ICiAiArtifactTransport. The transport must disable redirects and enforce DNS/IP policy against the actual connected address. The coordinator independently checks response status, media type, final URI, expiry, byte limit, exact length, and SHA-256.
5. FoundryVerifiedCiAiReportArtifact.Verify creates the consumer's immutable artifact value from the validated request snapshot and the retrieved bytes. The v1 report is limited to **1 MiB**, matching CiAi and HeadlessAi; the older 16 MiB draft limit is not used for this JSON report.
6. PersistentWorkTaskCoordinator.ApplyCiAiReport validates the report and applies the named CiAi verdict to the current task/lease. Duplicate application is acknowledged without a second attempt or lifecycle transition.
7. For RepairRequired, the existing FoundryHeadlessAiTurnCoordinator receives the exact accepted report context, prior turn identity, prior candidate SHA, and bounded evidence. The task remains in RepairRequired; the host must separately authorize any repository operation.

## 04 — Trust boundaries and limits

- A durable notice receipt means only that an authenticated, validated notice was recorded. It does not mean artifact bytes were retrieved or accepted.
- A digest proves equality to the declared bytes, not sender trust or CI correctness.
- The ICiAiArtifactNoticeAuthenticator is an interface. Production JWT signature, issuer, audience, key rotation, subject, scope, expiry, and repository authorization remain the deployed host's responsibility.
- The ICiAiArtifactTransport is an interface. Production HTTPS, TLS, redirect disabling, DNS/IP classification, connection-time IP pinning, proxy policy, and rebinding defenses remain the deployed host's responsibility.
- The JSONL mapping and receipt journals are local-filesystem, single-process stores; they are not distributed transactions.
- The integration test uses deterministic fakes. It does not prove a live endpoint, production credentials, live DNS/socket/TLS behavior, remote artifact service, or the Experiences/Minesweeper red-to-green pilot.
- CODE RED is urgency, not permission escalation. No automatic apply, merge, canonical-branch write, publication, release, deployment, or branch deletion is enabled.

## 05 — See it in a minute

CiAiArtifactHostCompositionTests exercises the entire fake-host path: exact notice authentication and validation, durable receipt and duplicate retry, exact-URI retrieval and byte verification, accepted v1 report consumption, idempotent task lifecycle application, and a same-task HeadlessAi repair request carrying the byte-exact accepted report context.

The test demonstrates composition and contract compatibility. Production readiness still requires the real host adapters and the authorized Minesweeper pilot.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
