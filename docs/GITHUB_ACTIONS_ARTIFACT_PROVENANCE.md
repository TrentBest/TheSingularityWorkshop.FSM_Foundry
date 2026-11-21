<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# GitHub Actions Artifact Provenance Retrieval

## 01 — What

`GitHubActionsArtifactProvenanceReader` is the host-side consumer for the dedicated
`foundry-candidate-checkout-provenance` artifact emitted by the trusted
`workflow_dispatch` path. It turns an already-correlated run attempt into
`VerifiedCandidateCheckoutProvenance` only after independently retrieving and validating the
artifact for that exact run and attempt.

A successful build or successful required jobs is not candidate-checkout provenance. The
candidate SHA and trusted workflow SHA remain separate identities.

## 02 — Retrieval contract

The reader performs these operations in order:

1. Revalidates the receipt against the durable dispatch journal and requires state
   `Correlated` for the exact run ID and attempt.
2. Re-fetches GitHub's exact run-attempt metadata and requires the allowlisted workflow path,
   `workflow_dispatch`, trusted `development` ref/SHA, request-ID run title, and successful
   terminal conclusion.
3. Enumerates artifacts for that run (bounded to the first 100). The exact
   `foundry-candidate-checkout-provenance` name must occur exactly once. Expired, malformed,
   future-dated, oversized, or wrong-run metadata is rejected.
4. Requests the artifact ZIP from GitHub's API. The API response must be one explicit HTTPS
   redirect to a trusted GitHub artifact-storage host. The redirect is followed once with a
   separate client that carries no GitHub API authorization header. A second redirect is rejected.
5. Reads the ZIP entirely in memory under a 2 MiB compressed limit. It permits at most eight
   entries and requires exactly one root `candidate-checkout-provenance.json` entry. The JSON
   entry is bounded to 32 KiB before and during decompression.
6. Parses a strict, depth-bounded JSON object with exact property sets. It binds request,
   operation, idempotency, task, correlation, repository, workflow path/ref, trusted workflow
   SHA, candidate ref/SHA, checked-out HEAD, run ID, and run attempt to the durable dispatch
   record and exact GitHub attempt metadata.
7. Constructs the immutable host-owned provenance object and calls the dispatch journal's
   `Verify` transition. Every failure before that final step leaves the journal `Correlated`.

## 03 — Security limits

| Boundary | Policy |
|---|---:|
| Artifact enumeration | 100 metadata records maximum |
| Metadata JSON | 256 KiB maximum |
| ZIP download | 2 MiB maximum |
| ZIP entries | 8 maximum |
| Provenance JSON | 32 KiB maximum |
| Redirects | Exactly one; HTTPS port 443 only |
| Redirect host | `*.blob.core.windows.net` or `*.githubusercontent.com` |
| Artifact bytes | In-memory only; no filesystem extraction |

The API client must have automatic redirects disabled. The artifact-download client must also have
automatic redirects disabled and must not carry ambient credentials. The signed redirect URI is
treated as a bearer capability: it is never logged, persisted, or returned in provenance.

The reader does not trust artifact name alone, run-level `head_sha` as candidate identity, ZIP
metadata alone, or a JSON claim without comparing every identity to the durable dispatch journal.
The artifact's embedded run attempt disambiguates GitHub artifacts retained across reruns; if
more than one same-name artifact exists for the run, retrieval fails closed rather than guessing.

## 04 — Failure semantics

Missing, duplicate, expired, malformed, oversized, wrong-run, wrong-attempt, unsuccessful, or
mismatched artifacts do not advance dispatch state. No partial provenance object is returned.
The reader does not retry dispatch, infer a candidate SHA, or reinterpret build evidence as
checkout evidence.

This reader establishes candidate-checkout provenance for the exact CI attempt. It does not by
itself create a `WorkTaskRepairAttempt`, authorize a repair, create a draft PR, or move a task to
`ReviewReady`. Those remain separate host lifecycle decisions.

## 05 — Validation

The contract tests exercise successful retrieval, exact attempt binding, missing artifacts,
ambiguous duplicate artifacts, candidate-SHA mismatch, and the absence of API credentials on the
signed-download request. Live GitHub dispatch and production credential composition remain
separate deployment gates.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
