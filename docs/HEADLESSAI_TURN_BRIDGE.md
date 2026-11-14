<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry–HeadlessAi Turn Bridge

## Purpose

Foundry owns the governed task lifecycle. HeadlessAi owns one provider invocation and returns a normalized, provenance-bound proposal. The turn bridge connects those responsibilities without giving model output repository authority.

A turn may be requested only for a task that is in `Investigating` and has the exact active, unexpired Foundry lease. The request is bound to the task, repository, issue, isolated feature branch, base commit, candidate commit, prior turn, and any supplied CI evidence.

## Invoke one turn

The host creates the bridge with its persistent task coordinator, a concrete `IHeadlessAiAgentTurnClient` adapter, and a durable `JsonlHeadlessAiTurnJournal`:

```csharp
var turnJournal = new JsonlHeadlessAiTurnJournal(
    Path.Combine(dataDirectory, "headlessai-turns.jsonl"));

var turnCoordinator = new FoundryHeadlessAiTurnCoordinator(
    persistentTasks,
    headlessAiClient,
    turnJournal);

var result = await turnCoordinator.InvokeAsync(
    taskId,
    lease.LeaseId,
    isolatedBranch,
    candidateCommitSha,
    role,
    instructions,
    canonicalPrompt,
    responseSchema,
    evidence,
    priorTurnId,
    priorCandidateCommitSha,
    cancellationToken: cancellationToken);
```

The task must already be in `Investigating`. The branch must be an isolated Git-valid feature branch; `master` and `development` are rejected. The candidate SHA must be a full commit SHA. CI evidence must identify the same repository and current or prior candidate SHA. Resumable turns must provide both `PriorTurnId` and `PriorCandidateCommitSha`, matching HeadlessAi envelope validation.

## Implement the host adapter

Foundry keeps its workflow core provider-neutral. The separate `Foundry.HeadlessAiHost` project is the concrete in-process adapter and references the pinned HeadlessAi source contract; hosts compose it with a configured `HeadlessAiAgentTurnExecutor`. Its `Prepare` method is local and side-effect-free: it creates the immutable `HeadlessAiAgentTurnRequest` snapshot once and returns it behind `IFoundryHeadlessAiPreparedTurn`, exposing that snapshot's `Fingerprint`. `ExecuteAsync` executes that exact prepared snapshot rather than reconstructing a second envelope.

| Foundry request | HeadlessAi envelope |
|---|---|
| `TaskId`, `CorrelationId` | Same identities; the host may use `TaskId` as the stable `ConversationId` and `foundry.work-task.v1` as `TaskVersion` |
| `Repository`, `AuthorizedBranch` | Same repository and authorized branch |
| `BaseCommitSha`, `CandidateCommitSha` | Same commit provenance |
| `IssueUrl` | `IssueUrl` |
| `Role`, `Instructions`, `CanonicalPrompt` | Same role and prompt fields |
| `ResponseSchema`, `ExpiresAt` | Same response schema and expiry |
| `PriorTurnId`, `PriorCandidateCommitSha`, `IdempotencyKey` | Same resumable-turn fields; prior turn ID and prior candidate SHA must be supplied together |
| `Evidence` | Map each item to a `HeadlessAiEvidenceReference` |
| `AcceptedCiAiReport` | Map the host-accepted bytes and exact run lineage to `HeadlessAiAcceptedCiReportContext`; preserve the separate outer notice/receipt fingerprint and nested source-artifact fingerprint |
| (No Foundry field) | Use default `HeadlessAiAgentTurnLimits` and an empty `ContextReferences` collection unless the host has separately bounded context references |

The adapter's `Prepare` method creates the envelope, maps evidence, and—when present—maps the accepted report plus every nested source-artifact field into `HeadlessAiAcceptedCiReportContext`. It calls the accepted-report overload of `HeadlessAiAgentTurnRequest.Create` once, so the exact report bytes and provenance are part of the canonical request fingerprint. It returns a prepared wrapper around that immutable snapshot and its canonical SHA-256 `Fingerprint`. `ExecuteAsync` invokes the configured `HeadlessAiAgentTurnExecutor.ExecuteAsync(preparedSnapshot, cancellationToken)` once, validates the returned `HeadlessAiAgentTurnResult`, and maps its task/candidate provenance, accepted report schema/verdict/digest/run/attempt/candidate, outer notice identity fingerprint, nested source-artifact fingerprint, turn identity, request fingerprint, provider/model identity, timestamps, error/diagnostic, and normalized proposal fields—including `OperationalStatus`—into `FoundryHeadlessAiTurnResult`. Foundry independently checks that every accepted-report identity matches the exact report supplied to the turn and that the mapped result's `RequestFingerprint` equals the prepared snapshot fingerprint; a 64-character hexadecimal shape check alone is not sufficient. Do not copy `RawResponse` into Foundry task state.

Use the host's configured `HeadlessAiAgentTemplate` and shared `HttpClient`. Provider credentials are resolved out of band by the host's request-header provider. Never put provider credentials, GitHub credentials, capability IDs, or authorization grants in the prompt, envelope, model response, or evidence summary.

## Durable invocation behavior

`JsonlHeadlessAiTurnJournal` records a pending intent and flushes it to disk before the provider call. After the returned result passes Foundry's provenance, canonical HeadlessAi fingerprint, and bounds checks, the journal records the completed result. The bridge then re-reads the task snapshot. If the lease expired, was superseded, or the task left `Investigating` during the provider call, the validated result remains durably recorded for host reconciliation, but the stale invocation throws and does not return the result to its caller.

- An exact duplicate of a completed invocation returns the recorded result without another provider call.
- Reuse of an idempotency key with different canonical invocation data is rejected.
- A cancellation already requested before dispatch is honored before the pending record is written. If cancellation, transport failure, or process loss occurs after reservation, a pending invocation with no completed result is treated as **indeterminate**. The provider may have received the request before the host stopped, so Foundry does not invoke it again automatically. The host must reconcile the provider outcome or escalate for an explicit decision.
- A failed journal write disables further use of that journal instance. Restart and inspect/recover the durable journal before dispatch resumes.

The JSONL journal coordinates cooperating instances and processes that use the same local filesystem path. Each operation acquires an exclusive OS file handle on a persistent `.lock` sidecar, reloads the JSONL sequence and invocation state while holding that lock, then appends and flushes its record before releasing the lock. The OS releases the handle if a process exits; the sidecar file itself is intentionally retained because deleting it could split lock ownership. Lock acquisition waits up to 30 seconds, then fails closed. A regression test constructs two journal instances before either writes and verifies that they observe each other's pending/completed records and preserve one monotonic sequence. This is local-filesystem coordination, not a distributed ledger: do not assume equivalent locking or durability semantics on network filesystems, across hosts, or when another writer bypasses this journal protocol. Protect the data directory with the host's normal filesystem access controls.

## Trust and authority

A normalized proposal is still untrusted model output. A diagnosis, patch intent, suggested status, or request for human approval does not change task state and does not grant a capability.

The bridge performs no repository reads or writes, branch creation, commit creation, CI dispatch, reruns, pull-request creation, merge, release, deployment, or package publication. The host must separately validate and authorize each requested action through Foundry's capability boundary, then use its trusted repository adapter to perform only the granted operation.

## Current verification and remaining work

The bridge contract, lease gate, provenance validation, durable completed-result replay, and indeterminate-call stop are covered by Foundry workflow tests. The concrete `Foundry.HeadlessAiHost` adapter is separately integration-tested with a fake HTTP transport and is built in CI against pinned HeadlessAi source. This verifies request/result mapping and adapter composition at build/test time; it does not prove a live provider call.

The remaining live integration requires:
1. Run the `Foundry.HeadlessAiHost.Console` composition root with host-side provider credential provisioning and verify one real provider invocation.
2. Trusted, isolated GitHub candidate-branch operations with capability checks.
3. Exact-candidate CiAi evidence retrieval, validation, and delivery into Foundry.
4. A bounded red-to-green repair cycle and a safe draft-PR handoff.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
