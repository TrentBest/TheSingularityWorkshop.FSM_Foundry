# GitHub Actions Workflow Dispatcher

## Responsibility

`GitHubActionsWorkflowDispatcher` dispatches only a host-allowlisted workflow against an exact candidate branch/SHA, using a separately pinned trusted workflow source. Dispatch acknowledgment is not CI success. The caller must correlate the run and verify the exact attempt and configured required jobs.

## Host-owned configuration

- `GitHubRepositoryOperationPolicy` allowlists repositories and the `agents/...` candidate namespace.
- `GitHubActionsWorkflowAllowlist` maps an opaque workflow key to a workflow file identifier and exact `.github/workflows/...` path.
- The trusted workflow source is pinned to `development` plus a full 40-character SHA supplied by the host.
- `IGitHubAccessTokenProvider` supplies credentials; `IGitHubActionsDispatchJournal` persists intent and correlation.

## Dispatch and provenance

Before dispatch, the dispatcher validates repository, branch, task/workflow key, and candidate SHA; resolves the workflow key from the allowlist; obtains a host token; and verifies both the candidate branch tip and trusted `development` tip against their exact expected SHAs.

It generates a unique request ID and flushes a durable `Dispatching` journal record before sending GitHub's workflow-dispatch request. The request uses `ref: development`, not the candidate branch. Fixed inputs carry the request ID, candidate ref/SHA, and trusted workflow SHA. The trusted workflow independently checks its source identity, resolves and verifies the candidate ref, and checks out the exact candidate SHA detached; it emits a separate candidate-checkout provenance artifact.

GitHub's `head_branch` and `head_sha` identify the trusted workflow source, not the candidate checkout. Candidate identity is separately bound to the journal and provenance artifact.

## Correlation and exact-attempt verification

Run discovery requires all of: exact workflow path, `workflow_dispatch` event, trusted workflow branch/SHA, and display title containing the exact request ID. Zero matches means not yet visible; multiple matches are rejected. The durable journal binds task, repository, workflow, candidate ref/SHA, trusted ref/SHA, and later exact run ID/attempt.

`VerifyRequiredJobsAsync` re-fetches metadata for the exact run attempt, then fetches that attempt's jobs. It rejects incomplete/truncated responses, duplicate required-job names, wrong run/attempt identity, missing jobs, and any required job not completed successfully. The journal becomes Verified only when the workflow and every configured required job succeed.

## Recovery and no-blind-retry rule

The append-only dispatch journal progresses through Dispatching, Acknowledged, Correlated, and Verified. A process restart can rehydrate the receipt and reconcile by request ID. An interrupted Dispatching intent is unresolved; it must not trigger an automatic second dispatch. Duplicate unresolved dispatches for the same workflow/branch/candidate are rejected.

The JSONL journal is local-filesystem persistence with cooperating-process file locking, not distributed storage or a provider-side transaction.

## Remaining external proof

The implementation and fake HTTP tests do not prove a live authorized dispatch. The candidate-checkout provenance artifact still requires host-side retrieval, size/digest/schema validation, and conversion into durable Foundry task evidence. A green ordinary push/PR CI run skips the workflow-dispatch-only candidate verification path and is not a substitute for live dispatch evidence.
