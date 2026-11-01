<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Roadmap and Readiness

**Readiness: early implementation; no current percentage is asserted.** The previous 35% estimate predates the governed-work lifecycle and is not comparable to the expanded scope. The version-1 workflow runtime passed its development CI gate; the governed-work feature branch passed [feature-branch CI run #276](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41170698768) on exact head `230907aad4a7a75aed589d61f4c43aa9ea2a1eb4`, including workflow timeout/cancellation tests. This is not a release-readiness claim. The signed host-issued CODE RED recovery receipt boundary passed [feature-branch CI run #294](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/actions/runs/41172622514) on exact head 0ef31369daa115fb652e46b79d208ea66ab8fc83.

## Milestones

### M0 — Repository and documentation bootstrap
- [x] Establish repository identity and MIT license.
- [x] State intended boundaries and distinguish target from implementation.
- [x] Add documentation navigation, Gallery, and roadmap.
- [ ] Verify repository branch policy and protect intended development flow.

### M1 — Define the domain from real Workshop contracts
- [x] Inspect FSM_API public builder/context APIs and its CI workflow.
- [x] Inspect FSM_COS composition contract and runtime boundary.
- [x] Inspect the FSM_Serialization representation boundary.
- [x] Inspect ProtocolAi/GrammarAi responsibility boundaries and AnyApp's host path at README/project/source level.
- [x] Record an initial Foundry domain and ownership proposal in [DOMAIN.md](DOMAIN.md).
- [x] Trace representative WebPage Hub contracts, callers, and tests; record the non-equivalence and safe host-adapter seam in [HUB_INTEGRATION_BOUNDARY.md](HUB_INTEGRATION_BOUNDARY.md).
- [ ] Record final source-backed contract decisions and source-derived UML.

### M2 — First executable workflow
- [x] Add a minimal buildable .NET 8 probe project.
- [x] Define a versioned in-memory workflow definition contract (persisted format remains open).
- [x] Document version-1 execution semantics in [WORKFLOW_SEMANTICS.md](WORKFLOW_SEMANTICS.md).
- [x] Execute and assert a deterministic state transition through the actual published FSM_API API in a probe.
- [x] Demonstrate a deterministic terminal state in the API probe.
- [x] Add and verify a deterministic multi-state fox/sheepdog scenario with explicit state assertions.
- [x] Add invalid-definition, handled-failure, ordering, cancellation, and handler-registration tests.
- [x] Produce structured step and workflow outcomes distinct from console output and host exit codes.
- [x] Add retained in-process run lifecycle: submit, start once, observe snapshots, cancel, and async disposal.
- [x] Verify coordinator lifecycle tests in development CI run #84.

### M3 — CI proof
- [x] Run the FSM_API probe in GitHub Actions; the manifest-driven workflow itself remains pending.
- [x] Verify CI exit-code mapping and upload a probe-output artifact.
- [x] Compare deterministic output from two fresh basic-probe processes.
- [x] Run the fox/sheepdog scenario in two fresh processes and compare TSV and SVG evidence byte-for-byte.
- [x] Prove cooperative workflow timeout outcomes and distinguish timeout from caller-requested cancellation in feature-branch CI run #276.
- [ ] Harden permissions for untrusted pull requests.
- [ ] Publish workflow logs and a concise evidence summary.

### M4 — Living documentation and optional article production
- [x] Add a modular Foundry theory index and first textbook-style chapter; link the learning path from README and documentation index.
- [x] Correct the development-vs-feature-branch evidence ledger in README and DOCUMENTATION_INDEX so draft PR #14/#15/#16 work is not represented as shipped development behavior.
- [ ] Align README and existing docs with the full FSM_COS reader journey, including a real money-shot visual and consistent What/Why/How/See/Next-step flow.
- [x] Add Chapter 02 on workflow definitions and execution semantics, grounded in the development runner, validator, registry, coordinator, and execution-context contracts.
- [x] Add Chapter 03 on task identity, leases, fencing, audit history, local persistence, and exact-evidence repair decisions; explicitly record the CODE RED approval-ownership discrepancy.
- [x] Add Chapter 04 on capabilities, authority, host-issued scoped grants, CODE RED mutation suspension, and the human-approval ownership discrepancy.
- [ ] Complete the remaining theory chapters for evidence/repair, persistence/recovery, hosts/adapters, scheduling/concurrency, observability, and security.
- [ ] Generate diagrams from real source/runtime evidence.
- [ ] Include stable type identity, source spans, and provenance.
- [ ] Add stale-output verification and a focused change test.
- [ ] Add Gallery recipes linked to successful runs.
- [ ] Add an opt-in article build that selects real source excerpts and records repository revision/path/line range.
- [ ] Generate high-contrast, readable code-excerpt images with equivalent selectable text and accessible descriptions.
- [ ] Produce a local/CI preview artifact without requiring publishing credentials.
- [ ] Define a publisher adapter only after selecting a destination; keep actual publication disabled unless explicitly approved.

### M5 — Host and language expansion
- [ ] Evaluate AnyApp/WebApp as hosts only after the CI proof works.
- [ ] Evaluate ProtocolAi/GrammarAi adapters with explicit contracts.
- [ ] Add additional languages only when the first cross-boundary proof is testable.

### M6 — Governed repository-work lifecycle
The completed items below are present on `development`. The signed CODE RED recovery receipt implementation remains subject to the separately recorded architecture reconciliation: the owner selected host-adapter enforcement for human approval, while current Foundry code verifies a signed receipt in the lifecycle core.
- [x] Add a versioned, repository/issue/SHA-bound work-task envelope with bounded identifiers, path scope, and attempt budget.
- [x] Add validated lifecycle transitions, expiring agent leases, fencing tokens, and sequenced audit history.
- [x] Add append-only local task, CODE RED incident, and CI repair-attempt journals with restart replay and fail-closed corruption handling.
- [x] Add host-issued, short-lived capabilities scoped to task, lease, branch, candidate SHA, paths, and explicit actions.
- [x] Add credential-free CODE RED incident records and validated active/cleared notice projections.
- [x] Require a short-lived, signed host approval receipt for CODE RED recovery, bound to the active incident sequence and exact task-event sequence; persist the complete signed receipt in the task audit.
- [x] Add exact-CI-attempt provenance checks and bounded deterministic repair decisions.
- [x] Add a concrete `Foundry.HeadlessAiHost` adapter that maps Foundry turns to HeadlessAi's immutable request/executor without moving lifecycle or authorization into HeadlessAi; build it against pinned HeadlessAi source in CI run #337.
- [x] Add `Foundry.HeadlessAiHost.Console` as a manual host composition root and compile it in CI run #352.
- [ ] Provision host-managed provider credentials and execute one real provider turn through the manual smoke runner.
- [ ] Connect CiAi's exact-run evidence packet/outbox and bounded rerun request/ledger through trusted host adapters.
- [ ] Bridge the task lifecycle into the existing WorkflowExecutionCoordinator/WorkflowRunner; do not create a competing runner.
- [ ] Prove the red → evidence → bounded repair → green → draft-PR path with deterministic fake-provider/fake-GitHub tests.
- [ ] Complete the first integrated MyVR pilot and preserve human review/merge authority.

## Readiness rubric

| Gate | Weight | Current evidence |
|---|---:|---|
| Purpose and dependency boundaries | 15% | Source-level contract inspection plus an initial domain proposal |
| Contracts and implementation | 20% | Version-1 workflow runtime on development; PR #10 adds the governed task/lease/journal/capability/repair-policy slice |
| Automated tests | 20% | Workflow runtime, task lifecycle, strict journal replay, repair policy, signed CODE RED recovery receipt validation, and persistence tests passed on PR #10 exact head in CI run #294 (112 tests) |
| CI execution and security | 20% | Pinned-action workflow has read-only contents permission and a 15-minute timeout; broader threat cases pending |
| Demonstration and reproducibility | 15% | Successful basic probe plus fox/dog state assertions, cross-FSM transition, repeatable trace/SVG, and uploaded artifact |
| Documentation/UML completeness | 10% | Bootstrap docs, lifecycle UML, and Hub integration boundary; source-derived UML and complete reader journey remain |

**No release recommendation yet.** Readiness rises when gates are supported by verifiable implementation and test evidence, not merely by adding documents. CI run #294 verifies PR #10 head commit `0ef31369daa115fb652e46b79d208ea66ab8fc83`. PR #11's source-pinned HeadlessAi adapter build passed run #337 on exact head `d410afd4e23aaf459bdf2ce526faf964d3d36274`; this does not prove a live provider call, GitHub/CiAi integration, or the MyVR pilot.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
