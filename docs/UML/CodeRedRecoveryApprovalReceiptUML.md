# CODE RED Recovery Authorization UML

**Evidence class:** source-derived contracts and test-derived sequences.  
**Source revision:** \`e0f6cbe7a4b0f23397a08a0778b30bee56400d1a\` (before this documentation-only commit).

## Type neighborhood

\`\`\`mermaid
classDiagram
    class CodeRedRecoveryApprovalReceipt {
      +int SchemaVersion
      +string ReceiptId
      +string Issuer
      +string Principal
      +string TaskId
      +string IncidentId
      +long TaskEventSequence
      +long IncidentSequence
      +CodeRedRecoveryAction Action
      +DateTimeOffset IssuedAt
      +DateTimeOffset ExpiresAt
      +string Signature
      +Validate()
      +GetSigningPayload()
    }
    class ICodeRedRecoveryReceiptVerifier {
      <<host-configured trust boundary>>
      +IsTrustedIssuer(issuer) bool
      +VerifySignature(issuer,payload,signature) bool
    }
    class PersistentCodeRedRecoveryCoordinator {
      +RecoverFromCodeRed(taskId,leaseId,incidentId,receipt,now,reason)
    }
    class JsonlCodeRedIncidentStore {
      +Get(incidentId)
      +ExecuteWithActiveIncident(incidentId,operation)
      +Create(incident)
      +Update(incident)
      +Clear(incident)
    }
    class PersistentWorkTaskCoordinator {
      +RecoverFromCodeRed(taskId,leaseId,incident,receipt,now,reason)
    }
    class WorkTaskCoordinator {
      +RecoverFromCodeRed(...)
    }
    PersistentCodeRedRecoveryCoordinator --> JsonlCodeRedIncidentStore : locks latest active incident
    PersistentCodeRedRecoveryCoordinator --> PersistentWorkTaskCoordinator : delegates recovery
    PersistentWorkTaskCoordinator --> WorkTaskCoordinator : validates lifecycle and receipt
    WorkTaskCoordinator --> ICodeRedRecoveryReceiptVerifier : trusted issuer and signature
    WorkTaskCoordinator --> CodeRedRecoveryApprovalReceipt : validates exact signed scope
    JsonlCodeRedIncidentStore --> CodeRedRecoveryApprovalReceipt : supplies current incident sequence
\`\`\`

## Successful recovery sequence

\`\`\`mermaid
sequenceDiagram
    participant Host as Host / human approval
    participant Recovery as PersistentCodeRedRecoveryCoordinator
    participant Incidents as JsonlCodeRedIncidentStore
    participant Tasks as PersistentWorkTaskCoordinator
    participant Core as WorkTaskCoordinator
    participant Verifier as ICodeRedRecoveryReceiptVerifier
    Host->>Host: Authenticate approver and issue signed, short-lived receipt
    Host->>Recovery: RecoverFromCodeRed(task, lease, incidentId, receipt)
    Recovery->>Incidents: ExecuteWithActiveIncident(incidentId)
    Incidents->>Incidents: Hold local incident gate; select latest active snapshot
    Incidents->>Tasks: callback(current incident)
    Tasks->>Core: RecoverFromCodeRed(task, lease, incident, receipt)
    Core->>Verifier: Check trusted issuer and signature
    Core->>Core: Check expiry, task/incident IDs and sequences, lease, action, replay
    Core-->>Tasks: Investigating snapshot + receipt-bound audit event
    Tasks->>Tasks: Append task snapshot to local journal
    Tasks-->>Incidents: Return persisted snapshot
    Incidents-->>Recovery: Return
    Recovery-->>Host: Recovery result
\`\`\`

## Fail-closed branches

- Untrusted issuer or invalid signature: reject; task remains \`CodeRed\`.
- Expired receipt or stale task-event / incident sequence: reject; task remains \`CodeRed\`.
- Replayed receipt: reject; durable event history is the single-use audit source.
- Incident update/clear racing recovery in the same store instance: update waits for the recovery callback to finish; the receipt cannot authorize a newer incident sequence.
- Journal persistence failure: persistent task coordinator fails closed for that process; restart and restore from durable state before accepting more work.

## Boundaries and limitations

The host owns human authentication, approval policy, receipt issuance, signing keys, and trusted-issuer configuration. The receipt authorizes only \`ResumeInvestigation\`; it is not a general capability and does not authorize repository mutation. Recovery serialization is local to one incident-store instance and one process. Incident and task journals are separate files and are not one cross-file atomic transaction. This diagram does not imply distributed locking, live GitHub integration, or automatic human approval.

## Test traceability

- \`CodeRedRecoveryAuthorizationTests.PersistentRecoveryVerifiesReceiptAndPersistsSingleUseAuditEvidence\`
- \`CodeRedRecoveryAuthorizationTests.InvalidSignatureAndUntrustedIssuerLeaveTaskInCodeRed\`
- \`CodeRedRecoveryAuthorizationTests.ExpiredOrSequenceMismatchedReceiptIsRejectedWithoutMutation\`
- \`CodeRedRecoveryAuthorizationTests.PersistentRecoveryReadsTheLatestDurableIncidentAndRejectsStaleApproval\`
- \`CodeRedRecoveryAuthorizationTests.IncidentUpdatesAreSerializedUntilPersistentRecoveryCommits\`
- \`JsonlCodeRedIncidentStoreTests\` covers replay, clear, identity immutability, and corrupt journal rejection.
