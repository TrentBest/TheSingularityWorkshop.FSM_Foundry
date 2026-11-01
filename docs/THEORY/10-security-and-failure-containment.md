<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 10 — Security Model and Failure Containment

**Chapter type:** security architecture and failure-analysis orientation  
**Evidence scope:** Foundry `development` validation, leases, capabilities, task journals, HeadlessAi bridge, and workflow contracts. DNS-pinned artifact transport and CiAi ingress/report integration are documented on separate unmerged draft branches, not as development behavior.

## 01 — Start with trust boundaries

Foundry coordinates work that may involve untrusted issue text, model output, external CI evidence, repository content, and privileged host operations. The security model must not assume that any one of those inputs is safe because it arrived through a familiar interface.

The primary trust boundaries are:

- **Work description:** issue bodies and task requests are untrusted data until validated.
- **Model output:** diagnosis and patch intent are proposals, never authority.
- **CI evidence:** a report is untrusted until its sender, dispatch identity, retrieval, content, and semantics are validated.
- **Host credentials:** provider and repository credentials remain in the host, not in prompts, task records, or model responses.
- **Repository mutation:** every operation must be authorized at the point where a trusted adapter performs it.
- **Persistence:** journal bytes are untrusted until replay validation succeeds.
- **Human approval:** the host authenticates the person and enforces its approval policy; model text cannot impersonate approval.

Security comes from preserving these boundaries across the entire path, not from one validator or one signed object.

## 02 — Validate before side effects

Workflow definitions are validated in full before the first handler runs. The host registers handler keys explicitly; definitions cannot add handlers or grant themselves filesystem, network, process, or publishing access.

Work-task requests validate identity, repository, issue URL, full base SHA, allowed repository-relative paths, and bounded attempt count. Branch and path validators reject malformed or unsafe values. Model-generated values must pass the same domain and host validation as any other untrusted input.

Validation reduces the accepted input space. It does not authenticate the caller or make a later adapter safe by itself.

## 03 — Leases and capabilities constrain action

A task lease identifies the currently accepted worker and fencing token. Expiry and replacement claims cause stale lease IDs to be rejected by the task coordinator.

A capability is narrower: it binds one host-approved action set to a task, repository, agent, lease, isolated branch, base SHA, candidate SHA, allowed paths, event sequence, and expiry. The registry checks the capability immediately before the adapter's operation and supports explicit revocation.

These are authorization controls at the Foundry boundary, not an operating-system sandbox. A privileged adapter that bypasses the registry can bypass the policy. Production hosts must centralize privileged operations, keep credentials inaccessible to the model, and make capability checks mandatory in every adapter path.

CODE RED suspends mutation. The capability registry allows only explicitly granted repository reads while the task is in CODE RED and invalidates capabilities issued before the incident. Recovery requires a fresh grant after the task returns to investigation.

## 04 — Model turns: bounded invocation, untrusted result

The HeadlessAi bridge requires an active unexpired lease and a task in `Investigating`. It validates the task, repository, branch, base/candidate SHAs, prior-turn pairing, evidence references, and request bounds before invocation.

The bridge persists a pending intent before the provider call. A completed duplicate returns the durable result without another provider call. If cancellation, transport failure, or process loss leaves a pending turn without a completed result, the invocation is indeterminate and is not automatically repeated. This avoids blindly duplicating a potentially accepted provider request.

The normalized response is checked for provenance and fingerprint, then returned as proposal data. It cannot transition the task or grant a repository capability. The host must independently authorize any resulting operation.

## 05 — Evidence retrieval and network security

A secure CI evidence path must bind the remote notice to a persisted dispatch intent, authenticate the sender, retrieve only the registered artifact, limit bytes and time, reject redirects or destination changes, verify content digest, and validate the report schema and aggregate semantics before applying a task decision.

The current development branch does not yet contain a production authenticated callback endpoint or a complete CiAi artifact transport/report pipeline. Draft PR #14 defines ingress and bounded retrieval contracts; draft PR #15 adds report-consumer logic; draft PR #16 adds a DNS-pinned HTTPS transport foundation. These remain unmerged and must not be described as shipped development behavior.

The transport proposal specifically addresses server-side request forgery and DNS rebinding by validating the registered HTTPS origin, resolving DNS at connection time, rejecting non-public address answers, connecting to a vetted address while preserving hostname-based TLS validation, and disabling redirects/proxy behavior. This is a feature-branch foundation, not proof of a deployed endpoint or live provider retrieval.

## 06 — Persistence and failure containment

Journal replay validates records before restoring state. The task persistence adapter fails closed after a write failure that occurs after an in-memory mutation; restart and restore from durable state before dispatch resumes. The repair-attempt journal rejects a torn final record and discontinuous or reused CI attempt history.

Separate journals are not one transaction. A crash between writes may leave an orphan record. The correct response is reconciliation and explicit reporting, not silently dropping evidence or guessing which operation completed.

Likewise, cancellation is cooperative. A handler that ignores its cancellation token may continue running. A remote operation may have succeeded even when the client timed out. Such outcomes are indeterminate until reconciled; automatic retries can duplicate side effects.

## 07 — CODE RED and human approval

CODE RED is a fail-safe mutation suspension and escalation state. The host owns incident notification transport and authenticates the human approver.

The current development lifecycle requires a signed, single-use host-issued recovery receipt and verifies it in Foundry before returning the task to investigation. The recorded architecture decision for this workstream places human-approval enforcement in the host adapter rather than requiring a verifiable host-issued receipt inside Foundry's recovery path. This is a known implementation/decision discrepancy, documented rather than silently resolved. A dedicated architecture reconciliation must align the runtime, contract, and tests.

No model response, issue comment, CI result, or task transition reason is a substitute for an authenticated human approval.

## 08 — Failure-containment checklist

For every external or privileged operation, ask:

1. **Identity:** Which task, repository, user/agent, and correlation ID does it belong to?
2. **Provenance:** Which exact base/candidate SHA, workflow run/attempt, or immutable request fingerprint is authoritative?
3. **Authority:** Which live lease and capability authorize this operation?
4. **Scope:** Which branch, paths, actions, endpoint, or artifact are permitted?
5. **Bounds:** What size, time, attempt, and concurrency limits apply?
6. **Idempotency:** Can the operation be repeated safely, or must an ambiguous result be reconciled?
7. **Persistence:** What durable intent/result is written before and after the operation?
8. **Failure state:** Does uncertainty block, stop, or raise CODE RED rather than silently proceeding?
9. **Human gate:** Does this action require explicit host-authenticated approval?
10. **Evidence:** What artifact proves the operation occurred, and what does it not prove?

If any answer is missing, the operation should not inherit broad ambient authority by default.

## 09 — Implementation map and remaining security work

1. [Workflow validator](../../src/Foundry.Workflow/WorkflowValidator.cs) and [handler registry](../../src/Foundry.Workflow/WorkflowHandlerRegistry.cs) — validate complete definitions before side effects.
2. [Work task lifecycle](../../src/Foundry.Workflow/WorkTaskCoordinator.cs) — validated transitions, lease fencing, and bounded recovery.
3. [Capability registry](../../src/Foundry.Workflow/WorkTaskCapabilityRegistry.cs) — host-issued scoped action grants.
4. [HeadlessAi turn bridge](../../docs/HEADLESSAI_TURN_BRIDGE.md) — durable at-most-once invocation behavior and untrusted proposal boundary.
5. [Work task persistence](../../src/Foundry.Workflow/PersistentWorkTaskCoordinator.cs) — local persistence and fail-closed mutation behavior.
6. [CiAi transport security proposal](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/feature/foundry-pinned-artifact-transport/docs/CIAI_ARTIFACT_TRANSPORT_SECURITY.md) — feature-branch DNS-pinned transport design; not merged to development.
7. [CiAi callback contract](../CIAI_ARTIFACT_CALLBACK_CONTRACT.md) — host-owned authenticated ingress contract; production endpoint remains unimplemented.

Important remaining work includes production host authentication and authorization, secure credential provisioning and rotation, mandatory capability enforcement in a real GitHub adapter, authenticated CiAi callback deployment, hardened artifact retrieval integration, cross-process task coordination, journal backup/retention/reconciliation, production incident transport, and architecture reconciliation for CODE RED approval ownership.

## Questions for the reader

1. Why is model output treated as untrusted even when the model is operating on a valid task?
2. What is the difference between a lease, a capability, and human approval?
3. Why must a network adapter reject redirects and validate the actual connection destination?
4. Why is a timed-out remote operation not necessarily safe to retry?
5. Why should journal corruption or incomplete evidence fail closed?
6. Which CODE RED approval ownership discrepancy remains unresolved?

## Exercise

Threat-model a task that asks an agent to repair a failing repository build. Assume the issue text contains a prompt injection, the model proposes a broad repository rewrite, the CI callback is replayed from an old run, the artifact URL resolves to a private address, and the provider call times out after the remote service may have accepted it.

For each threat:
- identify the trust boundary;
- identify the validation, lease, capability, or evidence rule that should reject it;
- state whether the system should block, reconcile, or escalate;
- name the durable record needed to explain the outcome.

Then identify one production control that is still missing from the current development branch.

## Further reading

- [Foundry theory learning path](README.md)
- [Chapter 04 — Capabilities, authority, and human approval](04-capabilities-and-authority.md)
- [Chapter 05 — Evidence, verdicts, and repair decisions](05-evidence-and-repair.md)
- [Chapter 06 — Persistence, replay, and recovery](06-persistence-and-recovery.md)
- [Chapter 07 — Hosts, adapters, and external systems](07-hosts-and-adapters.md)
- [Chapter 08 — Scheduling, dependencies, and concurrency](08-scheduling-and-concurrency.md)
- [Foundry domain and ownership](../DOMAIN.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
