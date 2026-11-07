<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Host Repository Operation Boundary

**Status:** The host-neutral authorization boundary and the concrete candidate-branch/read/stage/commit path are implemented. The optional `Foundry.GitHubHost` project also provides host-allowlisted GitHub Actions dispatch, a durable JSONL dispatch-intent journal, restart-time request-ID reconciliation, exact run-attempt job inspection, and durable verification state. `RequestRepositoryCiOperation` is wired to dispatch when the host composes the dispatcher. Production GitHub App token provisioning, distributed audit/staging storage, and end-to-end task-evidence ingestion remain incomplete.

## 1. Theory: proposals are not permissions

An agent may propose a repository operation, but neither model output nor a valid workflow definition grants permission to perform it. The host is the authority that decides which task may act, on which repository and candidate branch, against which exact commit, for which paths and operation kinds, and for how long.

Foundry supplies the last-mile check immediately before a host adapter is invoked. It re-reads the durable task snapshot, checks repository identity, and asks the capability registry to demand the exact task, branch, candidate SHA, action, path scope, lease, and expiry. A failed check must prevent the adapter call.

The host adapter is a trusted boundary, not an agent-selected tool. It owns credentials, concrete transport, repository allowlists, branch policy, workflow allowlists, and network policy. Foundry does not accept tokens, capability identifiers, arbitrary URLs, shell commands, or arbitrary GitHub API operations from workflow input.

## 2. Operation and result semantics

The current closed operation set contains five typed operations:

| Operation | Capability action | Scope |
|---|---|---|
| Create candidate branch | `CreateBranch` | New isolated branch at the exact task base SHA |
| Read repository file | `ReadRepository` | One bounded path |
| Write repository file | `WriteFiles` | One bounded path and bounded content |
| Create commit | `CreateCommit` | Existing isolated candidate branch |
| Request CI | `RequestCiRun` | Host-defined workflow key and exact candidate SHA |

The operation carries the full 40-character candidate SHA against which its capability is checked. For branch creation, this is the task's authorized base SHA and the target branch must not already exist; the operation creates a ref at that exact commit. The adapter result distinguishes:

- `RequestedCandidateSha`: the exact commit authorized before the operation.
- `ResultingCandidateSha`: the commit after the operation. Only an explicit commit operation may advance it.

File writes are staged and must retain the exact requested candidate SHA; they do not create commits or move the branch ref. Only the explicit commit operation may advance the candidate SHA. The host adapter must verify that the resulting commit has the authorized candidate as its parent (or otherwise prove the documented ancestry invariant) and return the exact resulting SHA from the repository authority. Foundry validates the operation-kind/SHA rule, SHA shape, and requested-SHA correlation; it does not independently prove Git commit ancestry. A subsequent operation against the new SHA requires a new capability scoped to that SHA.

All operation identities and paths are bounded and control-character-free; file content is limited to 64 KiB. This is a narrow typed API, not a generic process or tool runner.

## 3. Durable operation audit

The dispatcher requires an `IRepositoryOperationAuditJournal`. After the initial authorization, it appends and flushes a `Pending` record, then re-reads the durable task snapshot and re-demands the capability immediately before transport. This closes the time-of-check window introduced by audit I/O. The record contains task/repository/branch identity, operation kind, action, candidate SHA, optional path, timestamp, and a SHA-256 digest for write content or other bounded request payload. It never stores file contents, commit messages, credentials, or adapter detail text.

After the adapter returns, Foundry validates result provenance and appends either `Completed` with the stable operation reference and resulting SHA, or `Failed` with only the exception type. Failure messages are deliberately excluded because host/provider exceptions can contain secrets or request data.

A process crash after the host has performed a side effect but before the terminal record is flushed leaves a durable `Pending` entry. That state is **indeterminate**: it is evidence that an attempt began, not proof that it failed or succeeded. A host must reconcile it against GitHub before any retry; the journal never replays an operation. The JSONL implementation uses a sidecar exclusive file lock, reloads under that lock, rejects malformed/incomplete history, and flushes each record to disk. It coordinates cooperating local processes on a local filesystem; it is not a distributed database or a substitute for a host's durable production audit store.

The dispatcher depends on the audit interface rather than the JSONL implementation, so a host may supply a storage implementation appropriate to its deployment. The current JSONL store is the verified local implementation.

## 4. Workflow integration

`RepositoryOperationWorkflowHandler.Register` registers the `repository.operation` key in the existing `WorkflowHandlerRegistry`. The workflow input is data-only and identifies a supported operation. The host supplies a capability resolver callback and clock when registering the handler.

The request deliberately contains no capability ID. Its `create-branch` operation name can select only the closed branch-creation operation; it cannot select a branch name outside the branch already bound to the host-issued capability. The resolver receives the task ID and retrieves host-owned capability state. The dispatcher then validates that capability against the current durable task snapshot. The handler runs inside the existing sequential `WorkflowRunner`; this feature does not introduce a second executor or scheduler.

## 5. Host adapter obligations

A production adapter must independently enforce all policy that depends on the host or GitHub:

1. Keep credentials in host-managed secret storage; never place them in workflow input, model context, logs, or artifacts.
2. Bind the configured repository to the durable task repository and reject all other repositories.
3. For `CreateBranch`, create only the capability-bound isolated branch, only if it does not already exist, and point it at the exact authorized base SHA. For all later operations, operate only on that pre-authorized candidate branch.
4. Enforce normalized path containment and the capability's allowed path scope. Reject traversal, encoded traversal, absolute paths, and paths outside the configured repository root.
5. Stage file writes as immutable blobs while retaining the candidate SHA. For commit, construct a new commit from the staged tree with the authorized candidate as parent; update the candidate ref without force and verify its exact resulting SHA.
6. Map CI workflow keys through a host-owned allowlist. Dispatch only the exact authorized branch/ref and candidate SHA; never accept a model-supplied workflow file path, endpoint, or arbitrary dispatch payload. Correlate the resulting run using a host-generated request ID carried in the workflow run name, and verify the exact workflow path, `workflow_dispatch` event, trusted workflow branch/SHA, run ID, and run attempt. The candidate SHA is a separate dispatch input and must be proven by the trusted workflow's checkout-provenance artifact. A dispatch acknowledgment is not CI evidence.
7. Return a stable, non-secret operation reference and truthful result provenance. Do not treat a successful HTTP response alone as evidence that the requested repository state exists.
8. Apply bounded request sizes, timeouts, retries, rate limits, cancellation, and safe error redaction.

The adapter must not implement arbitrary shell execution, merge, release, deployment, branch deletion, or package publication through this interface.

## 6. GitHub Actions dispatch and correlation

`GitHubActionsWorkflowDispatcher` is a separate host-owned component. Its immutable `GitHubActionsWorkflowAllowlist` maps a host-selected key to one workflow file and one expected `.github/workflows/...` path. Model/workflow data cannot provide an endpoint, workflow path, or arbitrary input map. `GitHubRepositoryOperationAdapter` invokes it for `RequestRepositoryCiOperation` only when the host explicitly composes the dispatcher; otherwise the operation remains fail-closed. The repository-operation audit records the returned stable dispatch reference, while the dedicated dispatch journal retains the request ID and lifecycle.

Before dispatch, the component rechecks that the candidate branch tip equals the exact authorized 40-character SHA and independently rechecks that trusted `development` still points to the host-pinned workflow SHA. It dispatches with `ref: development`; the candidate branch and candidate SHA are carried only as fixed inputs alongside a host-generated 32-character request ID. The proof workflow runs from trusted `development`, verifies that trusted ref, fetches the candidate branch, requires its tip to equal the authorized candidate SHA, checks out that exact candidate SHA detached, and verifies the checkout before running the proof.

GitHub's dispatch endpoint returns an acknowledgment rather than a run ID. Before sending the request, the dispatcher appends and flushes a `Dispatching` intent containing the host-generated request ID and immutable task/workflow/repository/branch/SHA identity. It appends `Acknowledged` after a successful response. If the process loses the response or crashes after GitHub accepts the request, the unresolved intent remains available for reconciliation; a second dispatch for the same workflow, branch, and candidate SHA is refused until the first is resolved. A host can rehydrate the receipt from the journal and query GitHub without resending. The journal is append-only JSONL with replay validation and a local exclusive lock; like the other JSONL stores, it is a single-host implementation rather than distributed storage.

A run is accepted as correlated only when all of these match the receipt: exact workflow path, `workflow_dispatch` event, trusted workflow branch and trusted workflow SHA, and display title containing the unique request ID. GitHub run metadata's `head_branch` and `head_sha` identify the trusted workflow source, not the candidate checkout. The candidate ref/SHA remain bound to the immutable dispatch journal and fixed workflow inputs. The run listing must be complete within the bounded 100-result limit; multiple matches are rejected as ambiguous. A correlated result includes the exact GitHub run ID and `run_attempt`, which is persisted alongside the originating task ID and dispatch identity in the journal.

`VerifyRequiredJobsAsync` first re-fetches the exact `/runs/{run_id}/attempts/{run_attempt}` resource and validates its task-bound dispatch title, workflow path, event, trusted branch, and trusted workflow SHA; it then reads the exact `/jobs` endpoint, rejects truncated/oversized job lists and cross-run job identities, and requires each host-configured required job to appear exactly once with `completed` / `success`. The workflow itself must also conclude successfully. For a successful `workflow_dispatch`, the trusted workflow now emits a separate `foundry-candidate-checkout-provenance` artifact only after verifying the detached checkout. Its versioned JSON record binds the request ID, repository, workflow path/ref, event, trusted ref/SHA, candidate ref/requested SHA/actual checked-out HEAD, and run ID/attempt. The upload step is absent-safe if earlier validation fails. This artifact is independent of GitHub run metadata and is the candidate-checkout evidence a host must retrieve and validate. A successful job verification appends `Verified` to the dispatch journal; incomplete or failed jobs remain correlated but unverified, and the candidate cannot silently be dispatched again. `RequestRepositoryCiOperation` is now wired through the adapter when the host supplies the dispatcher; its operation audit stores the stable dispatch reference. The separate journal then supports recovery, correlation, and exact-attempt verification. Artifact retrieval, digest/size/schema validation, and conversion into `WorkTaskRepairAttempt` / durable task evidence remain unimplemented host handoff work.

## 7. Implemented evidence and remaining limits


The feature branch contains:

- `RepositoryOperation` and the five closed operation types, including exact-base candidate branch creation.
- `IRepositoryOperationAdapter` and `AuthorizedRepositoryOperationDispatcher`.
- The workflow-registry handler and data-only request contract.
- `IRepositoryOperationAuditJournal`, `JsonlRepositoryOperationJournal`, and dispatcher integration that flushes Pending before transport and a terminal record after result validation.
- Fake-adapter tests for authorization failures, path scope and traversal, wrong task/repository/branch/SHA, CODE RED, lease expiry, read/CI/branch SHA provenance, workflow integration, arbitrary-tool rejection, candidate advancement only after explicit commit, durable replay, pending crash state, payload redaction, and capability revocation during audit reservation.

The latest exact-head proof is [GitHub Actions run #41595913313](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41595913313) on commit `1dfa8e433d486f068acf97b81fa9637e5fd8b243`: 177 tests passed, all projects built, the separate-process FSM_API probe passed, the fox/sheepdog scenario passed twice with matching traces/SVG, and the evidence artifact was published.

The earlier exact PR head `3a72ebdae73887adcca23a45dbeda3e3c09f3ed1` passed the full proof workflow in [GitHub Actions run #41582975207](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41582975207). Workflow tests, project builds, the separate-process FSM_API probe, the repeated fox/sheepdog scenario, and evidence artifact publication all succeeded.

Not implemented by this boundary:

- A production GitHub App credential provider and installation-permission verification.
- Conversion of a verified exact run attempt into `WorkTaskRepairAttempt` / durable task evidence and the `ReviewReady` draft-PR handoff.
- A production/distributed audit backend; the current JSONL implementation is local-filesystem storage and not a distributed ledger.
- Foundry does not independently prove Git ancestry; the host adapter is responsible for the single-parent and exact-base verification.
- A complete autonomous issue-to-draft-PR loop.

The `Foundry.GitHubHost` tests use fake HTTP handlers and temporary JSONL stores to verify exact-SHA branch creation, immutable-SHA reads, bounded response handling, repository allowlisting, branch namespace restrictions, staged Git blobs, durable metadata replay, payload redaction, batch freezing across restart, idempotent retry after freeze, single-parent commit construction, non-force ref advancement, deterministic retry reconciliation, refusal to overwrite a moved branch, allowlisted Actions dispatch, durable dispatch intents, restart recovery after an ambiguous dispatch response without resending, exact-SHA preflight, request-ID-bound run correlation, exact-attempt required-job inspection, and durable verification state. They do not contact GitHub or prove a production token provider, installation permissions, network egress policy, live GitHub commit/ref behavior, or the full autonomous task-evidence loop. The branch/read adapter proof passed in [GitHub Actions run #41589082922](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41589082922) on commit `01bbd0dc75ce345df5bff6b5e722b0db2520f8f7`: 163 tests passed and all proof stages succeeded. The staged-blob implementation proof passed in [run #41589857412](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41589857412) on commit `29e23af7b7e7cd46865acb9c35368ecd2eb77178`: 164 tests passed and all proof stages succeeded. The explicit-commit implementation proof passed in [run #41590353590](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41590353590) on commit `858e7d457ed6e1f204ac7e7d50da5b617f49e424`: 166 tests passed and all proof stages succeeded. The durable batch-freeze and retry-safety proof passed in [run #41591073648](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41591073648) on commit `55544ebb00ef8df24b0fb5f4d38e11866de25ef4`: 168 tests passed, all projects built, runtime probes passed, and the evidence artifact was uploaded.

## 8. Verification

Run the focused workflow tests and the repository's full proof workflow on the exact candidate commit. Evidence must identify the exact repository, full candidate SHA, workflow, run, attempt, and required job outcomes. A green run is evidence, not permission to merge or publish.

---

<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="420" /></p>


## Trusted workflow-source boundary — implemented; live dispatch proof pending

The dispatcher now pins the trusted workflow source independently from the candidate being evaluated:

- The host supplies the full trusted `development` SHA. Before dispatch, the dispatcher confirms that `development` still points to that exact commit.
- The durable dispatch intent stores both identities: candidate branch/SHA and trusted workflow ref/SHA.
- The GitHub Actions dispatch request uses `ref: development`. The candidate ref and candidate SHA are separate inputs; they are never used as the workflow source.
- Run discovery and exact-attempt verification bind the workflow path, `workflow_dispatch` event, request ID, trusted `head_branch`, and trusted `head_sha`. Candidate provenance remains separately bound to the journaled dispatch intent.
- The trusted workflow verifies that it is running from `refs/heads/development`, fetches the candidate ref, requires its tip to equal the authorized candidate SHA, checks out that exact SHA detached, and verifies the checkout before running build/test steps.

This closes the candidate-controlled-workflow-source gap tracked by [issue #28](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/issues/28) at the implementation level. The latest previously verified push run on `adaed36ca4b5858767b67cbf7fced15292371ba0` passed in [run #42415726857](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/42415726857): workflow tests, all builds, FSM_API probe, both Fox/Sheepdog executions and comparison, and the state-evidence artifact succeeded. That push run did not execute the `workflow_dispatch`-only checkout validation or candidate-provenance artifact. The new artifact implementation is on the current feature branch and requires a fresh exact-head CI run; fake-HTTP tests still do not constitute a live dispatch. Do not treat the feature as production-ready until the live dispatch path is exercised and the host supplies a correctly refreshed trusted development SHA. Production GitHub App token provisioning, production host composition, and conversion of verified run evidence into durable task evidence remain incomplete. A green job remains execution evidence, not a CiAi named verdict.
