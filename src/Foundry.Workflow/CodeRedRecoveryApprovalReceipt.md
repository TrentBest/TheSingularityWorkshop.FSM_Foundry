# CodeRedRecoveryApprovalReceipt

## Responsibility

\`CodeRedRecoveryApprovalReceipt\` is a short-lived, host-issued authorization for exactly one CODE RED recovery transition. Foundry validates the receipt and records its use in the task event history; it does not authenticate the human approver or issue approvals.

## Signed scope

The version-one receipt binds:

- receipt ID, trusted issuer, and approving principal;
- task ID and incident ID;
- exact task-event sequence and incident sequence;
- the sole supported action, \`ResumeInvestigation\`;
- issue and expiry timestamps.

The maximum validity window is ten minutes. Validation rejects missing/oversized/control-character fields, unsupported schema/action, invalid sequence values, default issue time, non-positive validity, and longer lifetimes.

\`GetSigningPayload\` creates a canonical UTF-8 payload. Variable-length fields are UTF-8-byte-length-prefixed; schema/action/sequence/timestamps use invariant decimal representations; timestamps are UTC ticks. The signature is excluded from its own payload.

## Trust boundary and consumption

\`ICodeRedRecoveryReceiptVerifier\` is configured by the host. It must recognize configured trusted issuers and verify the supplied signature against the exact canonical payload. A valid signature alone is insufficient: the task coordinator also checks task/incident identity, current task-event sequence, current incident sequence, lease and recovery state, expiry, and single-use receipt identity.

The persistent path is \`PersistentCodeRedRecoveryCoordinator\` → \`JsonlCodeRedIncidentStore.ExecuteWithActiveIncident\` → \`PersistentWorkTaskCoordinator.RecoverFromCodeRed\`. The incident store's in-process lock prevents a local incident update/clear from racing the active snapshot check and task-journal append. The task coordinator persists the recovery event, including receipt and approval provenance; replay preserves that audit and prevents receipt reuse.

This is single-process serialization across two local journals, not a distributed lock or cross-file atomic transaction. Host authentication, human approval policy, signing-key custody, trusted-issuer configuration, and secure receipt delivery remain host responsibilities.

## Failure behavior

Invalid, untrusted, expired, replayed, or sequence-stale receipts are rejected without moving the task out of \`CodeRed\`. Recovery does not grant repository-operation capabilities; after the task resumes, the host must issue a fresh capability under current policy.

## Verification

See [CodeRedRecoveryAuthorizationTests](../../tests/Foundry.Workflow.Tests/CodeRedRecoveryAuthorizationTests.cs), [CodeRedIncidentTests](../../tests/Foundry.Workflow.Tests/CodeRedIncidentTests.cs), and [JsonlCodeRedIncidentStoreTests](../../tests/Foundry.Workflow.Tests/JsonlCodeRedIncidentStoreTests.cs). These cover signed receipt verification, issuer rejection, expiry, stale task/incident sequence, durable single-use audit, restart replay, latest-incident selection, and serialization against concurrent incident updates.
