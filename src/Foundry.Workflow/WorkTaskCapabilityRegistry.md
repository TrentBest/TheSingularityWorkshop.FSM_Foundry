# Work Task Capability Registry

## Responsibility

`WorkTaskCapabilityRegistry` is the host-owned, in-memory authority for narrowly scoped repository-operation grants. It issues opaque capability IDs only from a current `WorkTaskSnapshot`; model output, workflow envelopes, and task requests cannot create or expand grants.

The registry is not a credential store, repository transport, durable permission database, or substitute for the host's GitHub permissions.

## Grant boundary

`Grant` requires:

- A task request and event collection.
- A live lease with a non-empty agent ID and lease ID.
- A task status that is not Queued, Blocked, ReviewReady, HandedOff, Stopped, or Failed.
- A Git-valid isolated feature branch. `master` and `development` are explicitly rejected.
- A full 40-character hexadecimal candidate SHA.
- At least one safe repository-relative path, all contained within the task request's allowed paths.
- At least one recognized action.
- A positive lifetime no longer than one hour.

The effective expiry is the earlier of the requested lifetime and lease expiry. Paths and actions are deduplicated and stored as read-only collections. A CODE RED task can receive only a read-only grant.

## Demand boundary

Every `Demand` checks the current snapshot against the exact grant:

- capability exists and has not been revoked;
- current time is within the grant interval;
- task remains active and its lease remains live;
- task ID, repository, agent ID, lease ID, base SHA, branch, and candidate SHA match;
- action is recognized and explicitly allowed;
- read/write paths are safe and within the grant's path scope; non-path operations cannot smuggle a path.

Any event after grant issuance that transitions the task to CODE RED permanently invalidates that grant, including after recovery. A fresh host grant is required after the authorized recovery path. While CODE RED is current, only repository reads are permitted.

## Revocation and lifetime

`Revoke` removes the capability ID from this process's registry; repeated revocation is harmless. Expiry is checked at use time. The registry is in-memory and process-local: restart loses grants, and this implementation does not provide distributed revocation or cross-host coordination.

## Action vocabulary versus implemented operations

The enum currently includes ReadRepository, WriteFiles, CreateBranch, CreateCommit, RequestCiRun, and CreateDraftPullRequest. The closed `RepositoryOperation` hierarchy currently implements read-file, write-file, create-branch, create-commit, and request-CI operations. The draft-pull-request enum value is vocabulary only here; it is not an implemented operation or completed PR handoff.

Merge, release, deployment, branch deletion, package publication, shell execution, and arbitrary tool invocation are not exposed by this dispatcher.

## Tests

`WorkTaskCapabilityRegistryTests` cover exact task/repository/agent/lease/branch/SHA/action/path binding, CODE RED invalidation across recovery, path traversal and scope expansion, expiry, explicit revocation, lease-bounded lifetime, missing leases, canonical branch rejection, unknown actions, and excessive lifetime.

These are deterministic unit tests; they do not establish live GitHub authorization or distributed-host behavior.
