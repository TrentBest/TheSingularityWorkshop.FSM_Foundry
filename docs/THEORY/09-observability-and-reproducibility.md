<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 09 — Observability, Audit, and Reproducibility

**Chapter type:** operational evidence theory and implementation orientation  
**Evidence scope:** Foundry workflow snapshots, task event history, durable journals, repair evidence, and existing deterministic FSM_API demonstrations. A complete production telemetry or repository-wide trace platform is not implemented.

## 01 — Seeing a system is not the same as proving what happened

An operator needs to understand what a system is doing, why it made a decision, and what evidence supports the result. Those needs overlap, but they are not the same.

- **Observability** helps an operator inspect current state and diagnose behavior.
- **Audit** preserves who performed an accepted action, under which ownership and policy context, and in what sequence.
- **Evidence** supports a specific claim, such as “candidate SHA X passed workflow run Y, attempt Z.”
- **Reproducibility** makes it possible to repeat a defined experiment and compare its outputs under controlled inputs.

A console line may help with diagnosis but is not necessarily durable audit. An immutable task snapshot may show current state but not explain every decision. A passing workflow run may prove a specific test execution but not establish a universal property of the system.

## 02 — Model state and observation separately

Foundry exposes immutable snapshots for workflow runs and work tasks. A workflow snapshot reports run identity, workflow identity/name, lifecycle status, timestamps, and the latest structured result. A task snapshot reports the task request, current status, lease, attempt count, and immutable event history.

These snapshots are observations of domain-owned state. A UI may render them, but it should not mutate them or become the authority that decides whether a transition occurred.

The distinction is especially important for asynchronous work. A run can be accepted but not started, running, or terminal. A task can be queued, claimed, investigating, implementing, validating, blocked, or in an explicit terminal/escalation state. Displaying a status string without its identity, sequence, and provenance can hide stale observations.

## 03 — Audit requires sequence and ownership

The task lifecycle records accepted transitions with a monotonic event sequence, previous/current state, agent identity, lease ID, timestamp, and bounded reason. Evidence-derived decisions also record the exact CI run and attempt and the evidence payload digest.

This makes the task history more than free-form logging. Replay validates that the event sequence is continuous and that the state edges and provenance are acceptable. A stale lease cannot advance the task simply by submitting a plausible reason.

The event history still has limits. It is local to the task journal, not a central tamper-proof audit service. Filesystem administrators can access the journal, and the current stores do not provide distributed consensus or remote immutable retention.

## 04 — Evidence artifacts need identity and context

A useful evidence artifact should be traceable to:
- repository and source revision;
- task or workflow identity;
- exact candidate commit where applicable;
- CI workflow run and attempt;
- schema/version and content digest;
- observation time and producing component;
- the specific claim the artifact is intended to support.

Foundry's repair-attempt projection binds a CI result to task/correlation identity, repository, branch, workflow path, candidate SHA, run/attempt, evidence digest, completeness, outcome, diagnostics fingerprint, and timestamps. This lets the deterministic repair policy reject evidence that belongs to another task or candidate.

A digest proves that bytes match a recorded digest. It does not authenticate the producer by itself. Authentication, immutable retrieval, and required-workflow policy remain separate gates.

## 05 — Reproducibility: make demonstrations inspectable

The Foundry repository includes deterministic FSM_API probe and Fox/Sheepdog scenario workflows. They run separate processes, assert state outcomes, compare generated TSV/SVG scenario evidence, and publish artifacts. This is stronger than a screenshot of a successful console session because the inputs, executable path, assertions, and output artifacts can be inspected.

Reproducibility is still scoped. A repeatable state-machine demonstration does not prove a live GitHub adapter, a real provider call, or a distributed task scheduler. The artifact proves the tested scenario under the recorded workflow environment.

A good demonstration should therefore answer:
1. What exact input and version were used?
2. Which command or workflow produced the result?
3. Which assertions were checked?
4. Where is the resulting artifact?
5. What was not tested?

## 06 — Operational status and CODE RED notices

The CODE RED status notice factory creates bounded, credential-free active and cleared status projections. It checks that an active notice matches a currently leased task in CODE RED and requires a next-update time before lease expiry. A cleared notice requires an explicitly cleared incident.

The notifier is a host-owned publication boundary. It propagates delivery failures and does not claim that an incident was cleared because a message was sent. Actual GitHub/chat transport, destination policy, and delivery idempotency remain adapter responsibilities.

This is an example of separating domain facts from presentation and transport: Foundry creates a validated status projection; the host decides where and how it is published.

## 07 — A practical evidence ledger

The documentation index and roadmap should remain living evidence ledgers, not aspirational status pages. Every capability claim should point to one or more of:

- source implementation;
- tests that exercise the relevant behavior;
- exact commit and CI run;
- reproducible artifact;
- explicitly identified feature branch;
- a known gap or decision requiring review.

The evidence label must match the actual scope. “Implemented on development” is not the same as “present on a draft PR.” “Build passed” is not the same as “live provider invocation verified.” “ReviewReady” is not merge authorization.

## 08 — Implementation map and current limits

1. [WorkflowExecutionCoordinator.cs](../../src/Foundry.Workflow/WorkflowExecutionCoordinator.cs) — immutable run snapshots and structured outcomes.
2. [WorkTaskCoordinator.cs](../../src/Foundry.Workflow/WorkTaskCoordinator.cs) — sequenced task event history and evidence-derived decisions.
3. [JsonlWorkTaskStore.cs](../../src/Foundry.Workflow/JsonlWorkTaskStore.cs) — durable task snapshots and replay validation.
4. [CodeRedStatusNoticeFactory.cs](../../src/Foundry.Workflow/CodeRedStatusNoticeFactory.cs) and [CodeRedStatusNotifier.cs](../../src/Foundry.Workflow/CodeRedStatusNotifier.cs) — validated status projection and host delivery boundary.
5. [WorkTaskRepairAttemptJournal.cs](../../src/Foundry.Workflow/WorkTaskRepairAttemptJournal.cs) — exact CI attempt provenance and evidence digest.
6. [Gallery](../../Gallery.md) and [FSM_API probe workflow](../../.github/workflows/foundry-fsm-api-probe.yml) — reproducible demonstrations and CI artifacts.
7. [Documentation index](../../DOCUMENTATION_INDEX.md) and [roadmap](../ROADMAP.md) — capability status and evidence ledger.

The current implementation does not provide a complete metrics/tracing platform, centralized tamper-resistant audit storage, automated artifact lineage across every adapter, repository-wide source-derived UML generation, or production incident notification transport.

## Questions for the reader

1. How does an immutable snapshot differ from an audit event?
2. Why is a console log not automatically an audit record?
3. What provenance fields make a CI artifact useful for a repair decision?
4. What does a content digest prove, and why is producer authentication still needed?
5. Why must a demonstration state what it did not test?
6. Which evidence would support a claim that a provider adapter is live rather than merely buildable?

## Exercise

Write an evidence record for a hypothetical CI pass. Include repository, task/correlation identity, candidate SHA, workflow path, run ID, attempt, evidence digest, completeness, observation time, and producing system.

Now remove the candidate SHA, then change the run attempt while keeping the same run ID. Explain why each altered record is insufficient or ambiguous for a safe repair decision. Finally, write one sentence describing what the CI pass proves and one sentence describing what it does not prove.

## Further reading

- [Governed work task lifecycle](../WORK_TASK_LIFECYCLE.md)
- [Chapter 05 — Evidence, verdicts, and repair decisions](05-evidence-and-repair.md)
- [Chapter 06 — Persistence, replay, and recovery](06-persistence-and-recovery.md)
- [Foundry theory learning path](README.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
