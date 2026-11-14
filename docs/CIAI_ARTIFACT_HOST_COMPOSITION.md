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
- `PinnedHttpsCiAiArtifactTransport` retrieves only a URI whose HTTPS DNS origin is explicitly registered to a pinned client; it disables redirects and sends no credentials. Foundry rejects redirects, changed final URIs, unexpected status/media type, oversized content, expired artifacts, and digest/length mismatches.
- Foundry verifies the exact bytes and passes those same bytes to the ciai.test-results.v1 consumer.
- The consumer binds the report to the exact task, candidate, workflow, event, run, attempt, and registered job; only CiAi's named verdict determines evidence completeness and actionability.
- Foundry durably applies the attempt and may prepare a same-task HeadlessAi repair turn. The model response is a proposal, never repository authority.

## 03 — How

The integrated path is:

1. Persist the immutable dispatch intent and independently verified exact run binding in JsonlCiAiRequestTaskMappingJournal.
2. Receive the exact raw notice bytes. ICiAiArtifactNoticeAuthenticator authenticates the request-scoped sender; credentials remain outside Foundry and model context.
3. CiAiArtifactNoticeIngress.AcceptAsync strictly decodes UTF-8, validates the v1 notice and its full mapping/provider/capability/source-SHA binding, then persists the receipt. An exact retry returns the original receipt; a conflict is not accepted.
4. CiAiArtifactRetrievalCoordinator passes the validated URI unchanged to ICiAiArtifactTransport. The concrete PinnedHttpsCiAiArtifactTransport selects only an explicitly registered origin client created by PinnedHttpsArtifactHttpClientFactory; that client enforces DNS/IP policy against the actual connected address, and the adapter disables redirects and attaches no credentials. The coordinator independently checks response status, media type, final URI, expiry, byte limit, exact length, and SHA-256.
5. FoundryVerifiedCiAiReportArtifact.Verify binds the retrieved bytes to the exact durable ingress receipt and validated notice. It retains a distinct `NoticeIdentitySha256` fingerprint over sender, notice ID, receipt ID, notice-body digest, request/idempotency identity, and outer report-artifact metadata. The inner report remains separately identified by `ReportIdentity = sha256:<report-byte-digest>`. The v1 report is limited to **1 MiB**, matching CiAi and HeadlessAi; the older 16 MiB draft limit is not used for this JSON report.
6. PersistentWorkTaskCoordinator.ApplyCiAiReport validates the report and applies the named CiAi verdict to the current task/lease. Duplicate application is acknowledged without a second attempt or lifecycle transition.
7. For RepairRequired, the existing FoundryHeadlessAiTurnCoordinator receives the exact accepted report context, prior turn identity, prior candidate SHA, and bounded evidence. The concrete `HeadlessAiAgentTurnClient` maps that context—including the outer notice fingerprint and nested source-artifact fingerprint—into `HeadlessAiAcceptedCiReportContext`, then verifies the same provenance on the normalized result. The task remains in RepairRequired; the host must separately authorize any repository operation.

## 04 — Trust boundaries and limits

- A durable notice receipt means only that an authenticated, validated notice was recorded. It does not mean artifact bytes were retrieved or accepted.
- The notice/receipt identity, inner report-byte digest, and nested source-artifact fingerprint are separate identities; none substitutes for another. A digest proves equality to declared bytes, not sender trust or CI correctness.
- `CiAiHostObservedSourceArtifactBinding` no longer has a public constructor. Construction is internal to Foundry's workflow assembly and exposed only to the trusted host-adapter assembly plus the test assembly. This prevents ordinary external consumers from manufacturing the binding, but it does **not** prove the host adapter has independently observed provider run/job/artifact metadata. `CiAiSourceArtifactArchiveVerifier` now checks the provider-supplied archive digest and size, requires exactly `results.trx` and `provenance.json`, applies bounded in-memory extraction (20 MiB archive, 16 MiB per payload, 100:1 maximum compression ratio), rejects unsafe/special entries, and computes exact payload digests. It is not yet composed with a GitHub run/job/artifact observer or provenance validator, so its inputs are not independently sourced in the current host path. `IdentitySha256` remains a deterministic fingerprint, not a cryptographic attestation.
- The ICiAiArtifactNoticeAuthenticator is an interface. Production JWT signature, issuer, audience, key rotation, subject, scope, expiry, and repository authorization remain the deployed host's responsibility.
- `PinnedHttpsCiAiArtifactTransport` is the concrete HTTPS adapter for ICiAiArtifactTransport, backed by the pinned socket factory. Its origin allowlist must be configured from trusted provider registration. The deployed host still owns callback authentication/JWT validation, HTTPS ingress, credential provisioning for provider API calls, remote acknowledgement, and the real endpoint integration test. No GitHub API run/job/artifact observer or provenance-to-dispatch validator is implemented yet; the bounded archive verifier is currently a tested utility and is not yet connected to the accepted-report path.
- The JSONL mapping and receipt journals are local-filesystem, single-process stores; they are not distributed transactions.
- The report-composition integration test still uses deterministic fakes. The adapter's unit tests validate allowlist and URI policy but do not prove a live endpoint, production credentials, a real provider artifact service, independent provider run/job/artifact observation, nested archive verification, or the Experiences/Minesweeper red-to-green pilot.
- CODE RED is urgency, not permission escalation. No automatic apply, merge, canonical-branch write, publication, release, deployment, or branch deletion is enabled.

## 05 — Provider-observer integration gate

CiAi PR #65 (`integration/ciai-v1-evidence-contracts`, current candidate
`91775679ad1e0d29b0be4dbfef2dec24a40e08c3`) now has a successful exact-source producer
and observer run. On source run `43547370162`, attempt `1`, the `contract` job (`132301723883`)
completed successfully and published `ciai-test-results-1` (artifact `14094561284`,
provider digest `sha256:542d776c5a1c3585c3560c135da2b14b976cac71f59b265eb039007d3229a030`).
The separate observer run `43547370174` independently retrieved and validated that source
artifact and its provenance, then published report bundle `14093774927`. The canonical
report verdict is `CompletePass`, report identity
`sha256:d62dd4834533d57787002328d82958776a06dd3bd8de6760a21a8709d7a58064`.

These runs prove the CiAi producer/observer path for this candidate; they do not prove
Foundry's receiver-side retrieval or binding. Keep source run `43547370162` distinct from
observer run `43547370174`: the observer artifact hosts the report, while the report's
nested source evidence is bound to the source run. The Foundry host must retrieve the
observer bundle, validate the canonical report and nested source binding, and preserve the
source run ID in the persisted dispatch/notice mapping. The CiAi provider-side collectors
remain useful reference implementations, not a drop-in Foundry host adapter or production
attestation merely because their constructors are internal.

The adapter must preserve these identities independently:

- **Provider run identity:** repository, run ID, run attempt, event, provider `head_sha`,
  workflow path, and workflow-definition ref.
- **Trusted workflow identity:** provenance workflow ref and workflow SHA. The run's
  provider `head_sha` is not a substitute for the workflow SHA.
- **Candidate checkout identity:** provenance checkout ref and checkout SHA, compared
  to the persisted dispatch mapping. Never copy the provider run `head_sha` into the
  candidate-SHA slot without an explicit workflow/event contract proving equality.
- **Job identity:** job ID, run ID, run attempt, exact job name, evidence URL, and the
  required checkout-resolution/verification step names and successful conclusions.
- **Artifact identity:** artifact ID selected from the exact run's complete listing,
  exact artifact name, archive digest/size, created/expiry timestamps, and download URL.
  The ID and metadata must never originate in the report or provenance JSON.

The PR #65 collector audit identified hardening required before reuse:

1. The run, job, and artifact collectors parse provider JSON directly from response
   streams without an explicit maximum response-byte limit. Foundry must wrap every
   API response in a bounded reader before JSON parsing, with bounded pagination and
   an explicit timeout.
2. Artifact detail validates the returned artifact ID, name, digest, size, expiry, and
   nested workflow-run ID/head SHA. The Foundry composition must additionally bind
   that detail to the independently observed repository and exact run attempt; it
   cannot infer attempt identity from the artifact detail object.
3. Run and job observations are separate API facts. The attempt-specific jobs endpoint
   must be queried for the exact observed attempt, and every returned job must retain
   and validate its own run ID/attempt. Required job steps must be checked by exact
   name, status, and conclusion; merely finding a configured step name is insufficient.
4. Provenance parsing validates its own schema, duplicate-property rejection, digest,
   repository, run/attempt, workflow fields, event fields, PR fields, and checkout
   fields. Foundry must still compare those parsed fields against the persisted dispatch
   mapping and independently observed run/job facts before it constructs
   `CiAiHostObservedSourceArtifactBinding`.
5. The archive URL returned by GitHub is an API-origin URL, while the ZIP download may
   redirect to object storage. Download handling must be an explicit bounded redirect
   state machine: validate every destination against separately registered pinned HTTPS
   origins, never forward GitHub API credentials to a storage origin, reject loops and
   excessive hops, and verify the downloaded ZIP against the independently observed
   digest and size before extraction.

Accordingly, PR #65 is the contract reference, not a trusted evidence source by itself.
The next implementation unit is a Foundry-owned bounded GitHub API observer and
cross-evidence validator. Only its successful output may feed the archive verifier;
only after archive and provenance checks may the host construct the internal source
binding. Until that composition and its tampering tests exist, nested source-artifact
attestation remains an open CODE RED gate.

## 06 — See it in a minute

CiAiArtifactHostCompositionTests exercises the entire fake-host path: exact notice authentication and validation, durable receipt and duplicate retry, exact-URI retrieval and byte verification, accepted v1 report consumption, idempotent task lifecycle application, and a same-task HeadlessAi repair request carrying the byte-exact accepted report context.

The test demonstrates composition and contract compatibility. Production readiness still requires the real host adapters and the authorized Minesweeper pilot.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
