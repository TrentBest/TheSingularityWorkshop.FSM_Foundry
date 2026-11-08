# Authorized Repository Operation Dispatcher

## Responsibility

`AuthorizedRepositoryOperationDispatcher` is the last-mile authorization boundary between workflow/agent proposals and an `IRepositoryOperationAdapter`. It re-reads the durable task snapshot, verifies repository identity, and demands a host-issued `WorkTaskCapability` before any host transport is invoked.

The dispatcher does not own credentials, select arbitrary tools, grant capabilities, or turn a workflow request into permission. The host owns the adapter, capability resolver, and transport policy.

## Closed operation model

The current operation records are:

| Operation | Capability action | Scope / effect |
|---|---|---|
| `ReadRepositoryFileOperation` | ReadRepository | Read one normalized relative path |
| `WriteRepositoryFileOperation` | WriteFiles | Write one path; content capped at 64 Ki characters |
| `CreateRepositoryBranchOperation` | CreateBranch | Create an isolated branch from the exact base SHA |
| `CreateRepositoryCommitOperation` | CreateCommit | Commit on the candidate branch; only operation allowed to advance candidate SHA |
| `RequestRepositoryCiOperation` | RequestCiRun | Request a host-allowlisted workflow against exact candidate SHA |

Paths reject rooted, traversal, encoded-percent, backslash, colon, and empty-segment forms. The closed hierarchy exposes no shell, arbitrary tool, merge, release, deployment, branch deletion, or package publication operation.

## Dispatch sequence

1. Validate operation and supplied time.
2. Read the task snapshot from `PersistentWorkTaskCoordinator`.
3. Require the durable task repository to match the operation repository.
4. Demand capability scope against that snapshot.
5. Check cancellation.
6. Persist a Pending audit intent.
7. Re-read the durable task snapshot and repository identity.
8. Demand the same capability again using the current clock, closing the journal-write time-of-check window.
9. Check cancellation again, then invoke the host adapter.
10. Validate operation reference, requested candidate SHA, and resulting full SHA.
11. Reject candidate SHA movement for every operation except explicit commit.
12. Persist Completed or Failed audit state. Adapter detail and exception messages are not persisted because they may contain secrets.

The second demand is important: a task can transition to CODE RED while the durable audit intent is being reserved. The adapter must not be called if the recheck fails.

## Audit and crash semantics

`IRepositoryOperationAuditJournal` records a Pending intent before the external side effect. `JsonlRepositoryOperationJournal` appends and flushes JSONL records, maintains sequence/state integrity, and uses a local file lock for cooperating processes. It stores operation identity, candidate provenance, path, operation reference, failure type, and a SHA-256 payload digest—not file contents, commit messages, credentials, exception messages, or adapter detail.

A Pending record after process loss is indeterminate. It must be explicitly reconciled against the host provider; it is never automatically replayed. The file lock is not a distributed lock, and the journal is not a cross-system transaction with GitHub.

## Workflow integration

`RepositoryOperationWorkflowHandler` registers one handler key (`repository.operation`) in the existing `WorkflowHandlerRegistry`. Workflow data supplies a typed operation request but has no capability-ID field. The host-supplied resolver obtains the capability from host state. Only the five operation names above are mapped; arbitrary tool names fail before capability resolution.

The handler executes through the existing `WorkflowRunner`; it does not add a second runner or scheduler.

## Evidence and limits

Tests cover pre-transport rejection, exact provenance, second authorization check, CODE RED and expired-lease denial, candidate SHA rules, pending-record recovery, secret-safe audit, and workflow request restrictions. The adapter tests in this layer use fakes. A successful fake-adapter test is not evidence of live GitHub transport, provider credential readiness, or end-to-end CI evidence ingestion.
