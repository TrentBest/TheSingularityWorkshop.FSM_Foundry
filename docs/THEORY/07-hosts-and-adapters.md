<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 07 — Hosts, Adapters, and External Systems

**Chapter type:** integration architecture and implementation orientation  
**Evidence scope:** Foundry `development` workflow and turn contracts, the concrete HeadlessAi adapter, and its manual console composition root. A production GitHub/CiAi host is not claimed.

## 01 — Foundry is a domain, not the environment

Foundry defines the meaning and lifecycle of work. It should not need to know whether it is running inside a desktop application, a web service, a command-line process, or a CI worker.

A host supplies the environment around that domain:

- durable storage paths and lifecycle for the process;
- authenticated user and agent identity;
- provider and repository credentials;
- network clients and external service adapters;
- operation policy and capability enforcement;
- logging, diagnostics, monitoring, and user interaction.

The host decides when to create a task, which capabilities to grant, which external systems to contact, and how to present outcomes. Foundry validates the work and governs its lifecycle. The GUI may present status, but it is not the executor or source of authorization.

## 02 — Ports describe the boundary; adapters perform the integration

A port is a contract owned by the domain. It describes the operation Foundry needs without requiring a particular vendor SDK or transport. An adapter translates between that contract and an external system.

This separation matters because external systems have different failure modes, credentials, versioning, retry semantics, and trust assumptions. Foundry should not copy provider-specific response objects into task state or allow a vendor SDK to decide task lifecycle transitions.

A useful adapter has a deliberately narrow shape:

1. validate the domain request and its provenance;
2. create one immutable request snapshot;
3. perform only the external operation represented by the port;
4. validate and normalize the response;
5. return a domain-level result without granting new authority.

The host composes adapters with the coordinators and stores. It retains credentials and makes authorization decisions.

## 03 — The current HeadlessAi adapter as a concrete example

Foundry's HeadlessAi bridge is the first concrete in-process adapter example.

The domain bridge accepts a turn only for a task in `Investigating` with the exact active, unexpired lease. It binds the request to task and correlation identity, repository, issue, isolated branch, base and candidate SHAs, role, prompt, evidence references, and optional prior-turn provenance.

The concrete `HeadlessAiAgentTurnClient` maps that request into HeadlessAi's versioned envelope, creates one immutable request snapshot, and executes that exact snapshot once. It maps normalized proposal fields back to Foundry and verifies the request fingerprint. It does not copy the raw provider response into Foundry state.

The adapter does not:
- access GitHub;
- create branches or commits;
- write repository files;
- dispatch CI;
- create pull requests;
- merge, release, deploy, or publish packages;
- own task scheduling, authorization, or provider credentials.

The model result is untrusted proposal data. It does not cause a task transition or grant a capability.

## 04 — Composition root: where dependencies become a running host

The manual `Foundry.HeadlessAiHost.Console` project demonstrates a composition root. It creates a local task store and persistent task coordinator, submits and claims one task, moves it into investigation, configures a HeadlessAi provider executor, creates the durable turn journal, composes the Foundry bridge, and invokes one turn.

The host reads `OPENAI_API_KEY`, `OPENAI_MODEL`, prompt, and optional data-directory settings from its process environment. The key is resolved by a request-time header provider; it is not part of the task request, prompt, journal, or model response. The console runner is intentionally manual and does not inspect or mutate GitHub.

The console project compiles in CI, but a live provider invocation has not been verified. The caller-supplied candidate SHA and allowed path in this smoke runner are not independently verified by a trusted GitHub adapter. A successful turn would prove one provider round-trip and normalized mapping—not a complete repository repair workflow.

## 05 — External integrations require their own trust gates

### Repository host

A GitHub adapter would need to authenticate the host, resolve the exact issue and repository state, verify the base and candidate SHAs, create only isolated feature branches, and enforce the Foundry capability immediately before each operation. It must not accept model output as a GitHub permission. The current development domain does not contain this complete live repository adapter.

### CI provider

A CI adapter must bind dispatch intent to an exact repository, branch, workflow, candidate SHA, run ID, and attempt. Results must be authenticated, retrieved through a hardened transport, checked for integrity and completeness, and applied through Foundry's evidence policy. The CiAi artifact ingress, report consumer, and DNS-pinned transport proposals remain separate unmerged draft PRs; they are not part of the current development integration.

### Model provider

HeadlessAi owns provider invocation and response normalization. Foundry owns whether a turn is allowed and how its result is interpreted within task lifecycle. The host owns the provider credential and endpoint configuration. A normalized proposal remains untrusted until the host and Foundry policy authorize a subsequent operation.

### User interface

A desktop or web UI may submit requests to a host and observe task/workflow snapshots. It should not become the owner of leases, capability state, durable journals, provider credentials, or repository authorization. UI state is a presentation of authoritative host/domain state, not a replacement for it.

## 06 — Failure handling across adapters

An adapter should not hide failures behind optimistic status updates. It should distinguish at least:

- request validation failure before external side effects;
- authentication or authorization rejection;
- transport timeout or cancellation;
- external service rejection;
- malformed or mismatched response;
- successful response whose provenance cannot be validated;
- indeterminate outcome where the remote side may have acted but the host cannot prove the result.

Retries are not universally safe. A read may be retryable; a commit, CI dispatch, or model invocation may require an idempotency key or a reconciliation query. The HeadlessAi bridge records a pending intent before invocation and treats an unresolved pending turn as indeterminate rather than automatically invoking it again. Each future adapter must define equivalent semantics for its own operation.

## 07 — Implementation map and evidence

1. [HeadlessAi turn bridge](../HEADLESSAI_TURN_BRIDGE.md) — domain port, lease gate, immutable request, durable at-most-once behavior.
2. [Concrete HeadlessAi host adapter](../HEADLESSAI_HOST_ADAPTER.md) — request/result mapping and provider composition.
3. [HeadlessAiAgentTurnClient.cs](../../src/Foundry.HeadlessAiHost/HeadlessAiAgentTurnClient.cs) — concrete in-process adapter.
4. [Console composition root](../../src/Foundry.HeadlessAiHost.Console/Program.cs) — manual host wiring and environment-based credential resolution.
5. [Workflow handler registry](../../src/Foundry.Workflow/WorkflowHandlerRegistry.cs) and [execution coordinator](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.cs) — separate workflow handler registration and run orchestration.
6. [Domain and ownership](../DOMAIN.md) — Foundry/host/GUI responsibility boundaries.

CI builds the pinned HeadlessAi source integration and exercises adapter behavior with deterministic test infrastructure. This does not prove live provider availability, production secret provisioning, GitHub mutation, CiAi delivery, or a full red-to-green cycle.

## Questions for the reader

1. Why should the Foundry port be independent of a provider SDK?
2. Which component owns provider credentials, and which component owns task lifecycle?
3. Why does the adapter create one immutable request snapshot before execution?
4. Why is a normalized model proposal still untrusted?
5. Which operations need idempotency or reconciliation after an ambiguous timeout?
6. What evidence would be required before claiming that a GitHub/CiAi host is production-integrated?

## Exercise

Design a host that accepts a work task from a web UI, requests one HeadlessAi investigation turn, creates an isolated GitHub branch after explicit host authorization, and requests CiAi validation.

Draw the composition root and mark:
- Foundry domain contracts and coordinators;
- provider-neutral ports;
- concrete adapters;
- credential ownership;
- capability enforcement points;
- durable journals;
- evidence validation;
- user-visible status projection.

For every remote operation, state what the host does if the request times out after the remote system may have accepted it. Do not treat a retry as safe unless the operation has a defined idempotency or reconciliation contract.

## Further reading

- [Foundry–HeadlessAi turn bridge](../HEADLESSAI_TURN_BRIDGE.md)
- [Concrete HeadlessAi host adapter](../HEADLESSAI_HOST_ADAPTER.md)
- [Chapter 04 — Capabilities, authority, and human approval](04-capabilities-and-authority.md)
- [Chapter 06 — Persistence, replay, and recovery](06-persistence-and-recovery.md)
- [Foundry theory learning path](README.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
