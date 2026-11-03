<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Host Repository Operation Boundary

**Status:** The host-neutral authorization boundary is implemented. The optional `Foundry.GitHubHost` project now supports exact-base candidate branch creation, bounded immutable-SHA text reads, Git blob staging, and append-only local staging metadata. Explicit commit advancement, CI dispatch, production token provisioning, and distributed staging remain incomplete.

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
6. Map CI workflow keys through a host-owned allowlist. Dispatch only the exact authorized branch/ref and candidate SHA; never accept a model-supplied workflow file path, endpoint, or arbitrary dispatch payload.
7. Return a stable, non-secret operation reference and truthful result provenance. Do not treat a successful HTTP response alone as evidence that the requested repository state exists.
8. Apply bounded request sizes, timeouts, retries, rate limits, cancellation, and safe error redaction.

The adapter must not implement arbitrary shell execution, merge, release, deployment, branch deletion, or package publication through this interface.

## 6. Implemented evidence and remaining limits

The feature branch contains:

- `RepositoryOperation` and the five closed operation types, including exact-base candidate branch creation.
- `IRepositoryOperationAdapter` and `AuthorizedRepositoryOperationDispatcher`.
- The workflow-registry handler and data-only request contract.
- `IRepositoryOperationAuditJournal`, `JsonlRepositoryOperationJournal`, and dispatcher integration that flushes Pending before transport and a terminal record after result validation.
- Fake-adapter tests for authorization failures, path scope and traversal, wrong task/repository/branch/SHA, CODE RED, lease expiry, read/CI/branch SHA provenance, workflow integration, arbitrary-tool rejection, candidate advancement only after explicit commit, durable replay, pending crash state, payload redaction, and capability revocation during audit reservation.

The exact PR head `3a72ebdae73887adcca23a45dbeda3e3c09f3ed1` passed the full proof workflow in [GitHub Actions run #41582975207](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41582975207). Workflow tests, project builds, the separate-process FSM_API probe, the repeated fox/sheepdog scenario, and evidence artifact publication all succeeded.

Not implemented by this boundary:

- A concrete GitHub REST/GraphQL adapter or credential provider.
- A concrete host/GitHub implementation for branch creation, base-SHA selection, or branch lifecycle management.
- A host-enforced workflow-key allowlist implementation.
- A production/distributed audit backend; the current JSONL implementation is local-filesystem storage and not a distributed ledger.
- Git ancestry verification inside Foundry (the host adapter owns that verification).
- End-to-end connection to the ReviewReady draft-PR handoff in PR #25.
- A complete autonomous issue-to-draft-PR loop.

The new `Foundry.GitHubHost` tests use a fake HTTP handler and temporary JSONL store to verify exact-SHA branch creation, immutable-SHA reads, bounded response handling, repository allowlisting, branch namespace restrictions, staged Git blobs, durable metadata replay, payload redaction, and credential-header placement. They do not contact GitHub or prove a production token provider, installation permissions, network egress policy, commit ancestry/ref updates, CI dispatch, or the full autonomous loop. The branch/read adapter proof passed in [GitHub Actions run #41589082922](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41589082922) on commit `01bbd0dc75ce345df5bff6b5e722b0db2520f8f7`: 163 tests passed and all proof stages succeeded. The staged-blob implementation proof passed in [run #41589857412](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/actions/runs/41589857412) on commit `29e23af7b7e7cd46865acb9c35368ecd2eb77178`: 164 tests passed, all projects built, the separate-process FSM_API probe and repeated fox/sheepdog scenario passed, and the evidence artifact was uploaded.

## 7. Verification

Run the focused workflow tests and the repository's full proof workflow on the exact candidate commit. Evidence must identify the exact repository, full candidate SHA, workflow, run, attempt, and required job outcomes. A green run is evidence, not permission to merge or publish.

---

<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="420" /></p>
