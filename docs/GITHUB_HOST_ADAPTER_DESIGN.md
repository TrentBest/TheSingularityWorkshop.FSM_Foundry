<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# GitHub Host Adapter: Design and Operation Semantics

## 1. Purpose

This document defines the proposed concrete GitHub host adapter for Foundry's capability-gated repository-operation boundary. It is a design contract, not a claim that a production adapter, credential provider, or autonomous issue-to-draft-PR loop is implemented.

The adapter translates Foundry's small typed operation vocabulary into a narrow set of GitHub Git Database, References, Contents, and Actions API operations. It must not become a general-purpose GitHub client or expose arbitrary API calls to workflow/model data.

The host owns credentials, repository policy, workflow allowlists, durable staging state, transport configuration, and the audit backend. Foundry owns task lifecycle, capability demand, operation typing, and the generic operation audit state machine.

## 2. Trust and ownership boundaries

| Concern | Owner | Contract |
|---|---|---|
| Task identity, lease, status, and current candidate | Foundry | Re-read durable task state for every operation. |
| Capability issuance and action/path scope | Host + Foundry registry | Capability is resolved by the host; workflow/model data never supplies a capability identifier. |
| GitHub credentials and installation/token selection | Host | Credentials never enter workflow context, model output, operation DTOs, or audit records. |
| Repository and branch allowlists | Host | Enforce independently of Foundry capability checks. |
| Git object construction and ref updates | GitHub adapter | Use exact commit/tree/blob identities and non-force ref updates. |
| Workflow dispatch allowlist | Host | Map a small logical workflow key to a fixed repository workflow ID/path and allowed ref policy. |
| Durable staged writes and idempotency | Host | Persist enough state to reconcile retries and process loss. |
| Operation audit | Foundry port + host implementation | Append Pending before remote side effects; append a terminal result only after validation. |

The host must fail closed when repository identity, branch, candidate SHA, operation type, path, workflow key, credential scope, or durable staging state cannot be established.

## 3. Why file writes are staged

GitHub's Contents API creates a commit as part of each file write. That behavior does not preserve Foundry's separate `WriteRepositoryFileOperation` and `CreateRepositoryCommitOperation` meanings. Mapping both operations to Contents API calls would either create multiple implicit commits or make the explicit commit operation meaningless.

The proposed adapter therefore uses Git's object model directly:

1. A write operation creates a Git blob and records a staged path-to-blob mapping under a durable staging key.
2. A successful write returns the same candidate commit SHA. Staging does not move the branch ref or create a commit.
3. A commit operation loads the staged set for the exact task, repository, branch, and candidate SHA.
4. It reads the candidate commit's tree, applies the staged path changes, creates a new tree, then creates a commit whose sole parent is the authorized candidate SHA.
5. It updates the candidate branch ref with `force=false`.
6. It re-reads the branch ref and commit metadata, verifies the new commit is the branch tip and has the authorized candidate as its parent, and returns the new candidate SHA.
7. The host durably records the commit operation result and closes the staging batch.

This gives the two operations distinct semantics: write stages content; commit advances the candidate. The dispatcher contract now enforces this invariant in the separate open PR #26 source change: write results must retain the input candidate SHA, and only explicit commit operations may advance it. The concrete GitHub adapter must implement the corresponding staging and commit behavior.

## 4. Operation mapping

### 4.1 Create branch

- Resolve the task's immutable base SHA and host-approved repository.
- Create a uniquely named candidate ref at exactly that SHA.
- If the ref already exists, do not silently adopt it. Read it and accept only if durable host state proves it was created for the same task and exact base SHA; otherwise fail with a branch-collision result.
- Never create, update, or delete `master` or `development`.
- Candidate branch naming is host-generated from a validated task identifier; model output cannot select an arbitrary ref name.

### 4.2 Read file

- Require the requested candidate branch to resolve to the exact authorized candidate SHA.
- Read the file at that immutable commit SHA, not at a moving branch name.
- Enforce the Foundry path restrictions and host-side repository/path policy.
- Bound response size and supported text encodings. Binary retrieval is a separate future capability, not an implicit fallback.

### 4.3 Write file

- Require the candidate branch still to point to the exact authorized candidate SHA.
- Create a blob through the Git Data API.
- Persist a staged-write record keyed by task ID, repository, branch, candidate SHA, and operation identity.
- Return the unchanged candidate SHA.
- Repeated operation identity with the same payload digest returns the existing staged result; the same identity with a different digest is a conflict.
- A new candidate SHA invalidates the previous staging batch unless a deliberate rebase/re-stage protocol is implemented.

### 4.4 Create commit

- Require at least one staged change for the exact candidate identity.
- Construct the new tree from the candidate tree plus staged blobs. Path deletion is not currently in Foundry's typed operation vocabulary and must not be smuggled through a write payload.
- Create a single-parent commit with the authorized candidate SHA as parent.
- Update the candidate ref using a non-force compare-and-swap style update. If another actor moved the ref, fail and reconcile; never force-push.
- Verify the resulting commit and branch tip before returning it.
- Clear/close the staging batch only after durable success is recorded. If the process stops mid-flight, retain enough information to reconcile the GitHub ref and commit before any retry.

### 4.5 Request CI

- Resolve the logical workflow key through a host-owned allowlist. The mapping fixes workflow identity, permitted event, and permitted ref.
- Dispatch only for the exact candidate SHA and approved candidate branch.
- Do not accept arbitrary workflow filenames, workflow IDs, event names, input keys, or refs from model output.
- GitHub's workflow-dispatch response does not itself provide a run ID. The host must correlate the dispatch to the subsequent run using repository, workflow identity, event, ref, head SHA, and a bounded dispatch time window; ambiguous matches remain unresolved.
- A dispatch acknowledgement is not CI success. Only a separately retrieved, exact-run/exact-attempt evidence record can satisfy Foundry's evidence policy.

## 5. Idempotency, concurrency, and recovery

The adapter must assume at-least-once delivery across process restarts. It must not assume that a timeout means GitHub did not perform the requested operation.

Each remote mutation needs a durable operation identity and a state machine such as:

`Prepared -> Submitted -> Reconciled`

or

`Prepared -> Submitted -> ReconciliationRequired`.

A timeout, connection loss, or process crash after submission is indeterminate. The host must query GitHub using the exact repository/ref/object identities and durable operation record before retrying. It must never blindly replay a branch creation, commit, or workflow dispatch.

Branch updates are compare-and-swap operations. Two commits from the same candidate SHA cannot both advance the same branch. One succeeds; the other detects a moved ref and must stop for reconciliation/rebase under a newly authorized candidate SHA.

Staged blobs are immutable Git objects, but the mapping from a task operation to its staged paths is mutable host state and therefore needs durable transactional protection. A local JSONL file is suitable only for a single-host development proof. A multi-instance host needs a shared transactional store with uniqueness constraints for operation identity and candidate batch identity.

## 6. Security requirements

- Use a least-privilege GitHub App installation token or equivalent host-managed credential, scoped to the approved repository set and minimum required permissions.
- Never log tokens, authorization headers, raw file content, commit messages, provider responses, or arbitrary GitHub error bodies.
- Keep repository allowlists, branch policy, workflow allowlists, and allowed file roots in host configuration, not model-controlled inputs.
- Restrict API calls to known GitHub API origins and fixed route templates; do not expose a generic URL or HTTP method operation.
- Bound request/response bytes, concurrency, timeouts, retry counts, and per-task operation budgets.
- Treat GitHub API responses as untrusted input; validate SHA shapes, object types, repository identity, ref names, and expected status codes.
- Use no force pushes, branch deletion, merge, release, deployment, package publication, or canonical-branch mutation operations.
- Preserve human review as the final authorization boundary. A model proposal, confidence score, consensus result, or green CI run cannot authorize merge or publication.

## 7. Implementation sequence

The proposed implementation should be split into reviewable increments:

1. **Contract alignment:** tighten operation result semantics so file staging retains the input SHA and only commit advances it; add tests for this invariant.
2. **Host abstractions:** define credential, repository-policy, workflow-policy, durable staging-store, and GitHub transport ports. Keep these outside `Foundry.Workflow`.
3. **Read-only adapter:** implement exact-ref branch verification and bounded file reads; add fake-transport tests.
4. **Staged write adapter:** implement blob creation and durable idempotent staging; test process restart and duplicate operations.
5. **Commit adapter:** implement tree construction, parent-SHA verification, non-force ref update, race handling, and reconciliation.
6. **CI dispatch and correlation:** add allowlisted dispatch and exact run/attempt correlation; never infer success from dispatch acknowledgement.
7. **Host composition:** wire host-owned credentials, policy, staging store, and audit backend into the existing Foundry workflow handler registry and runner.
8. **Vertical proof:** exercise issue -> task -> bounded proposal -> capability-gated operations -> exact-SHA CI evidence -> ReviewReady -> draft PR. Keep draft PR creation as the last repository mutation in this proof.

Each increment requires source, tests, theory, implementation guidance, and evidence to be updated together.

## 8. Current status and explicit non-claims

At the time of this design:

- Foundry has a typed, capability-gated repository-operation dispatcher and a JSONL audit-journal implementation on the open draft PR for that work.
- The dispatcher rechecks durable task state and capability immediately before adapter transport.
- The existing Foundry host sample composes HeadlessAi, but it is not a concrete GitHub repository-operation host.
- ServerApp's Foundry host is a server-role provisioning integration, not a general-purpose CODE RED GitHub adapter.
- No concrete GitHub repository adapter, GitHub credential provider, durable staged-write store, allowlisted CI dispatcher, or distributed audit backend is implemented.
- The full autonomous issue-to-draft-PR loop is not implemented or verified.
- This document does not authorize any merge, release, deployment, branch deletion, or NuGet publication.

## 9. Verification checklist

A concrete adapter is not ready until automated tests demonstrate:

- Exact base-SHA branch creation and collision rejection.
- Read operations bind to immutable candidate SHA, not a moving branch.
- Staged writes preserve candidate SHA and are idempotent by operation identity and payload digest.
- Commit uses the exact authorized parent SHA and only advances the candidate ref.
- Concurrent ref movement is detected without force-push.
- Process loss after each remote mutation is reconciled before retry.
- Workflow keys cannot select arbitrary workflows, events, refs, or inputs.
- CI dispatch is correlated to exact repository/workflow/ref/head SHA/run/attempt.
- No credentials or file/commit payloads appear in audit or logs.
- Canonical branches and prohibited repository operations remain unreachable through the adapter.
- Foundry's capability is re-demanded for every operation and does not become a durable substitute for host authorization.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
