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
2. Read the task snapshot, verify repository identity, and demand the host-issued capability.
3. Check caller cancellation and persist a Pending audit intent.
4. Acquire the coordinator's per-task asynchronous reservation, shared with every lifecycle mutation for that task.
5. Re-read the task and demand the capability again while holding that reservation.
6. Check the linked cancellation/deadline token, invoke the host adapter, validate operation reference and candidate SHA provenance, and persist Completed—all before releasing the reservation.
7. Persist Failed only when rejection is known to have occurred before adapter invocation. Once transport starts, exceptions, cancellation, invalid adapter results, and audit-completion failures retain Pending for explicit reconciliation because a remote side effect may already have occurred.

The reservation is keyed by task ID. Lifecycle mutations for that task wait until dispatch and audit completion finish; unrelated task IDs can proceed independently. The coordinator's global persistence lock is held only for short local state/journal operations, never across adapter I/O.

The reservation and transport cancellation budget is capped at two minutes and linked to the caller token. Host adapters must honor the supplied cancellation token and configure matching transport-level timeouts. If an adapter ignores cancellation, the reservation remains held rather than allowing a lifecycle transition to overtake an operation that may still be executing.

The initial demand rejects obviously unauthorized requests before reserving an audit entry. The final demand is authoritative: it closes both the audit-write and reservation-wait windows. The adapter is not called if that recheck fails.

A Pending audit entry is intentionally not a Failed entry when transport may have started. It is an unresolved external outcome and must be reconciled against the host provider before any retry. Cancellation releases the in-process reservation only after the adapter has returned/canceled; it does not imply remote cancellation or rollback.

## Audit and crash semantics

`IRepositoryOperationAuditJournal` records a Pending intent before the external side effect. `JsonlRepositoryOperationJournal` appends and flushes JSONL records, maintains sequence/state integrity, and uses a local file lock for cooperating processes. It stores operation identity, candidate provenance, path, operation reference, failure type, and a SHA-256 payload digest—not file contents, commit messages, credentials, exception messages, or adapter detail.

A Pending record after process loss is indeterminate. It must be explicitly reconciled against the host provider; it is never automatically replayed. The file lock is not a distributed lock, and the journal is not a cross-system transaction with GitHub.

## Workflow integration

`RepositoryOperationWorkflowHandler` registers one handler key (`repository.operation`) in the existing `WorkflowHandlerRegistry`. Workflow data supplies a typed operation request but has no capability-ID field. The host-supplied resolver obtains the capability from host state. Only the five operation names above are mapped; arbitrary tool names fail before capability resolution.

The handler executes through the existing `WorkflowRunner`; it does not add a second runner or scheduler.

## Evidence and limits

Tests cover pre-transport rejection, exact provenance, second authorization check, CODE RED and expired-lease denial, candidate SHA rules, pending-record recovery, secret-safe audit, and workflow request restrictions. The adapter tests in this layer use fakes. A successful fake-adapter test is not evidence of live GitHub transport, provider credential readiness, or end-to-end CI evidence ingestion.
