<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# UML and Source Documentation Inventory

**Inventory scope:** `feature/foundry-host-repository-operation-gate`. The earlier 2026-10-23 inventory covered only the FSM_API probe and Fox/Sheepdog demonstration and is superseded by this expanded source inventory.

## Source inventory

The current source tree contains three distinct areas: the sequential workflow runtime, governed work/agent coordination, and host-side repository/CI integration. The companion pages currently cover only a subset of the types; do not infer UML coverage from source existence.

| Source area | Representative source | Companion / UML | Status |
|---|---|---|---|
| FSM_API probe | `Foundry.FsmApiProbe/Program.cs` | `Program.md`, `FoundryFsmApiProbeUML.md` | Implemented; deterministic CI probe |
| Fox/Sheepdog scenario | `Foundry.FoxSheepdog/Program.cs` | `Program.md`, `FoundryFoxSheepdogUML.md` | Implemented; repeatable trace/SVG evidence |
| Workflow definition/validation | `WorkflowDefinition.cs`, `WorkflowValidator.cs` | `WORKFLOW_SEMANTICS.md` | Implemented and tested |
| Workflow execution | `WorkflowRunner.cs`, `WorkflowExecutionCoordinator.cs`, `WorkflowExecutionContext.cs` | `WorkflowExecutionCoordinatorUML.md`, workflow guides | Implemented and tested; manual UML exists for coordinator |
| Governed task lifecycle | `WorkTaskCoordinator.cs`, `PersistentWorkTaskCoordinator.cs`, `JsonlWorkTaskStore.cs` | `WorkTaskCoordinator.md`, `WorkTaskCoordinatorUML.md`, `WORK_TASK_LIFECYCLE.md` | Implemented; deterministic lifecycle/persistence tests; coordinator neighborhood and evidence/recovery sequences documented |
| Capability authorization | `WorkTaskCapabilityRegistry.cs`, `AuthorizedRepositoryOperationDispatcher.cs` | `WorkTaskCapabilityRegistry.md`, `AuthorizedRepositoryOperationDispatcher.md`, `RepositoryOperationAuthorizationUML.md` | Implemented; deterministic capability and fake-adapter authorization tests; live provider proof remains separate |
| CODE RED incidents/recovery | `CodeRedIncident.cs`, `CodeRedRecoveryApprovalReceipt.cs`, `PersistentCodeRedRecoveryCoordinator.cs`, `JsonlCodeRedIncidentStore.cs` | `WORK_TASK_LIFECYCLE.md`, `CodeRedRecoveryApprovalReceipt.md`, `CodeRedRecoveryApprovalReceiptUML.md` | Implemented; signed receipt, stale-sequence, replay, and local serialization tests |
| Repair decisions/evidence | `WorkTaskRepairDecisionPolicy.cs`, `WorkTaskRepairAttemptJournal.cs` | `WORK_TASK_LIFECYCLE.md` | Implemented; exact-run provenance and bounded decision tests |
| HeadlessAi bridge | `HeadlessAiTurnBridge.cs`, `JsonlHeadlessAiTurnJournal.cs` | `HEADLESSAI_TURN_BRIDGE.md` | Implemented; fake executor and durable replay tests |
| HeadlessAi host adapter | `HeadlessAiAgentTurnClient.cs`, console composition root | `HEADLESSAI_HOST_ADAPTER.md` | Source adapter/console compile; live provider turn not verified |
| CiAi dispatch/reconciliation | `CiAiDispatchSubmissionCoordinator.cs`, `CiAiDispatchRunReconciler.cs`, `CiAiDispatchRunMarker.cs` | `CIAI_ARTIFACT_CALLBACK_CONTRACT.md` | Host-neutral contract/core implemented; live CiAi transport remains |
| GitHub repository adapter | `GitHubRepositoryOperationAdapter.cs`, `JsonlGitHubRepositoryStagingStore.cs` | `REPOSITORY_OPERATION_ADAPTER.md` | Implemented on feature branch; fake-HTTP/local-store tests |
| GitHub Actions dispatcher | `GitHubActionsWorkflowDispatcher.cs`, `GitHubActionsDispatchJournal.cs` | `REPOSITORY_OPERATION_ADAPTER.md` | Dispatch/correlation/required-job verification implemented; live dispatch and artifact retrieval remain |

## View coverage

| View | Current status | Evidence needed for completion |
|---|---|---|
| Architecture/component | Source-grounded architecture diagram in `docs/ARCHITECTURE.md` | Keep diagram synchronized with component ownership and real integration joins |
| Class neighborhoods | Manual views: workflow coordinator, task coordinator, CODE RED recovery receipt, and initial probe | Capability dispatcher neighborhood added; agent bridge and GitHub adapter neighborhoods remain |
| Sequence: successful workflow | Workflow behavior documented; coordinator UML exists | Add tested sequence trace with exact source revision |
| Sequence: failure/recovery | CODE RED recovery success and fail-closed branches documented in `CodeRedRecoveryApprovalReceiptUML.md` | Add stale-lease and indeterminate external-effect sequences |
| Activity/control flow | Partial in workflow semantics | Authorization flow added; repair decision flow remains |
| State lifecycle | Workflow and task state vocabularies documented | Add source-derived diagrams for both separate lifecycle owners |
| Use cases | Initial target/use cases documented | Validate live host scenarios and human review interactions |
| Data model and lineage | Task, operation, dispatch, and evidence contracts documented | Diagram the exact candidate/run/attempt/artifact lineage |
| Test traceability | Test files and evidence linked from guides | Map each critical invariant to named tests and exact CI run |

## Exit criteria

Update this inventory whenever source changes. Every diagram must identify whether it is source-derived, test-derived, runtime-captured, human-authored, or conceptual. Include repository revision and source provenance for generated views. A conceptual target must never be counted as implemented UML coverage.

---

<!-- The Singularity Workshop footer -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
