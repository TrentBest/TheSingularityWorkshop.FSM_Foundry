<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# 04 — Capabilities, Authority, and Human Approval

**Chapter type:** security and authority theory with implementation orientation  
**Evidence scope:** Foundry `development` capability registry, task lifecycle contracts, and tests. The CODE RED approval ownership discrepancy is intentionally preserved rather than silently resolved.

## 01 — The central question: who is allowed to do what?

A work request describes an intended outcome. It does not grant the requester every permission needed to achieve that outcome.

An agent may need to inspect files, edit a bounded set of paths, create an isolated branch, commit a candidate, or request CI. Those are different powers. A request to “fix the build” must not become ambient permission to rewrite the repository, merge a pull request, publish a package, or deploy a service.

Foundry separates three ideas:

- **Identity and ownership:** which task and agent are involved, and which lease currently owns the task.
- **Capability:** which concrete operation a host has permitted, against which exact scope and provenance.
- **Human approval:** a policy decision made by an authenticated person through the host's approval experience.

None substitutes for another. A lease is not a capability. A capability is not proof of human approval. A model's recommendation is neither.

## 02 — Why authority must be explicit and narrow

If authority is inherited from a workflow definition, issue body, model response, or environment variable visible to an agent, untrusted input can influence what the system is allowed to do. If a single broad token is passed through every layer, a component that only needs read access may accidentally gain write or publication power.

The safer design is capability-oriented: the host decides which operation is needed, grants only that operation, limits its scope and lifetime, and checks the grant at the point where the operation is attempted.

This is a design principle, not a claim that a capability object magically sandboxes a process. The adapter that performs the actual filesystem or GitHub operation must enforce the check and must keep its underlying credentials outside model context.

## 03 — The capability grant as a bounded contract

The current `WorkTaskCapabilityRegistry` is an in-memory host-owned registry. A grant is created only when the host explicitly calls `Grant` with a task snapshot and a chosen subset of paths and actions.

A capability binds together:

- task ID and repository identity;
- the active agent identity and exact lease fencing ID;
- an isolated feature branch, never `master` or `development`;
- the task's base SHA and an exact candidate SHA;
- repository-relative paths contained within the task's original allowed-path scope;
- an explicit allowlist of `WorkTaskAction` values;
- the task event sequence at issue time;
- issue and expiry timestamps.

The maximum lifetime is one hour, and the effective expiry is the earlier of the requested lifetime and the active lease expiry. A capability cannot be issued for a task without a live lease or for terminal/blocked states. Empty scopes, unsafe paths, invalid actions, malformed SHAs, and invalid branch names are rejected.

The current action vocabulary is intentionally finite:

| Action | Meaning |
|---|---|
| `ReadRepository` | Read a path inside the granted path scope |
| `WriteFiles` | Write a path inside the granted path scope |
| `CreateBranch` | Create the exact isolated branch bound by the capability |
| `CreateCommit` | Create a commit for the bound candidate scope |
| `RequestCiRun` | Request CI for the bound candidate |
| `CreateDraftPullRequest` | Create a draft PR for the bound candidate |

Merge, release, deployment, package publication, and arbitrary shell/process execution are not grantable actions in this contract.

## 04 — Demand: authorize the operation at its boundary

Issuing a capability is only half of the contract. Before an adapter performs an operation, it calls `Demand` with the capability ID, current task snapshot, branch, candidate SHA, action, optional path, and current time.

The registry rejects a request when, among other things:

- the capability is unknown, revoked, not yet valid, or expired;
- the task no longer has an active lease;
- the task, repository, agent, lease, base SHA, branch, or candidate SHA differs from the grant;
- the action is not in the allowlist;
- a read/write path is unsafe or outside the granted path scope;
- a non-file action is incorrectly supplied with a path;
- a CODE RED transition has invalidated the grant.

This is a point-of-operation check. It prevents a stale capability from being reused after the task changes ownership or provenance. It does not forcibly stop an already-running external process; the host adapter and its operating environment must enforce the policy around the actual operation.

## 05 — CODE RED: suspend mutation, preserve investigation

CODE RED is a mutation-suspension boundary, not a license to discard evidence or destroy the task. The capability registry permits only explicitly granted repository reads while the task is in `CodeRed`. It denies writes, branch creation, commits, CI requests, and draft-PR creation.

A transition into CODE RED invalidates every earlier capability. This remains true after recovery: a capability issued before the incident cannot become valid again merely because the task leaves CODE RED. The host must issue a fresh grant after the task returns to investigation.

That separation allows an agent to inspect and explain an incident without retaining the power to continue mutating the repository while the incident is active.

## 06 — Human approval belongs to the host

Human approval is a separate policy boundary. The host authenticates the person, presents the incident and proposed recovery, obtains an explicit decision, and decides whether to permit the recovery operation. A task transition reason, issue comment, model response, or capability grant is not itself proof that a human approved.

There is an implementation/decision discrepancy that must remain visible:

- **Current development implementation:** CODE RED recovery verifies a short-lived, signed, single-use host-issued receipt inside the Foundry task lifecycle. The receipt is bound to the task, active incident sequence, task-event sequence, recovery action, issuer, principal, and validity window.
- **Recorded architecture decision:** human-approval enforcement remains in the host adapter; the design should not require a verifiable host-issued receipt inside Foundry's CODE RED recovery path.

These designs differ in where the approval proof is enforced. The current code and tests establish the first behavior; the recorded decision calls for the second ownership boundary. This chapter does not choose between them or change runtime behavior. Reconcile the contract, implementation, and tests in a dedicated reviewed architecture change before describing the boundary as aligned.

Regardless of the eventual seam, the host must authenticate the human and own the approval experience. The model must never be treated as the approver.

## 07 — Follow the implementation and tests

1. [WorkTaskCapabilityRegistry.cs](../../src/Foundry.Workflow/WorkTaskCapabilityRegistry.cs) — host-issued grants, scope checks, expiry, revocation, and CODE RED mutation suspension.
2. [Work task lifecycle contract](../WORK_TASK_LIFECYCLE.md) — capability contract, recovery behavior, and current limitations.
3. [Capability registry tests](../../tests/Foundry.Workflow.Tests/WorkTaskCapabilityRegistryTests.cs) — executable checks for grants and denied operations.
4. [WorkTaskCoordinator.cs](../../src/Foundry.Workflow/WorkTaskCoordinator.cs) — task ownership and lifecycle transitions that capabilities inspect.

The registry is an in-memory object. Its grants do not survive process restart, and it is not a distributed authorization service. The host must keep the registry and capability IDs on the host side, keep credentials out of model context, and re-evaluate policy before issuing new grants after restart.

## 08 — What this boundary does not prove

The current capability contract does not, by itself:

- authenticate a user or agent account;
- provide OAuth, JWT, or credential management;
- sandbox a process or prevent an adapter from bypassing the registry;
- provide distributed revocation or cross-process capability state;
- guarantee that a GitHub or filesystem adapter enforces every check;
- authorize merge, release, deployment, package publication, or arbitrary command execution;
- resolve the CODE RED approval ownership discrepancy.

A passing registry test proves the registry's decision for the exercised inputs. Production safety additionally depends on every privileged adapter calling the registry immediately before the corresponding operation and keeping its credentials inaccessible to untrusted model output.

## Questions for the reader

1. Why is a task lease insufficient to authorize a repository write?
2. Why should read and write permissions be separate actions?
3. What exact provenance fields prevent a capability from being replayed against another candidate?
4. Why does CODE RED invalidate earlier grants even after recovery?
5. Why is a signed receipt not automatically the same architecture as host-owned human approval?
6. Which adapter would have to enforce a capability before a real GitHub commit?

## Exercise

Imagine an agent has a live lease for task `task-42`, a branch `work/task-42`, and candidate SHA `0123456789abcdef0123456789abcdef01234567`. The task allows edits only under `src/Feature`.

Design two grants: one for reading source and one for writing a single file. Then evaluate these requests:

- write `src/Feature/Handler.cs` using the write grant;
- write `README.md` using the same grant;
- create a commit for a different candidate SHA;
- use the original grant after the task enters CODE RED;
- use the grant after its lease expires.

For each request, identify the field or policy check that accepts or rejects it. Finally, explain why passing these tests does not prove a production adapter cannot bypass the registry.

## Further reading

- [Governed work task lifecycle](../WORK_TASK_LIFECYCLE.md)
- [Chapter 02 — Workflow definitions and execution semantics](02-workflow-definitions-and-execution.md)
- [Chapter 03 — Tasks, leases, and lifecycle ownership](03-tasks-leases-and-lifecycle.md)
- [Foundry theory learning path](README.md)
- [Foundry domain and ownership](../DOMAIN.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
