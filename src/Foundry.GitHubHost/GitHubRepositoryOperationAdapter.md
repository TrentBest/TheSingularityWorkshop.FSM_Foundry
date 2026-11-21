# GitHub Repository Operation Adapter

## Responsibility and trust boundary

`GitHubRepositoryOperationAdapter` implements the host transport port for the closed Foundry repository-operation model. It requires a host-provided token provider, explicit repository allowlist, durable staging store, and an `HttpClient` fixed to `https://api.github.com:443`. Repository names and candidate branch namespace are host policy, not model-controlled values.

The adapter does not issue Foundry capabilities or decide whether a task may act. The dispatcher performs that authorization before calling this adapter. The adapter independently enforces repository and candidate-branch policy and owns the provider credential boundary.

## Implemented operations

- **Create branch:** refuses to adopt an existing candidate branch; fetches and verifies the exact requested base commit before creating the isolated ref; validates the returned ref and SHA.
- **Read file:** verifies the branch tip equals the requested candidate SHA, reads content pinned to that immutable SHA, validates path/type/encoding, caps bytes at 64 KiB, and decodes strict UTF-8.
- **Stage write:** uploads a Git blob and records its identity and content digest in the host staging store. File contents are not persisted in the staging journal.
- **Create commit:** freezes the exact candidate's staged batch, builds a tree over the candidate base tree, creates a single-parent commit, advances the candidate ref without force, then re-reads and verifies ref, tree, parent, and message. The deterministic commit inputs allow safe reconciliation after an ambiguous response.
- **Request CI:** delegates only when a separately configured `GitHubActionsWorkflowDispatcher` exists; otherwise fails closed.

The host candidate namespace is `agents/...`; the host repository allowlist is independent of the Foundry capability path/action scope.

## Staging store

`IGitHubRepositoryStagingStore` is keyed by repository, branch, candidate SHA, and path. The JSONL implementation records blob SHA and SHA-256 content digest, limits each candidate batch to 128 paths, and durably freezes the batch before commit construction. An identical staged retry is idempotent; changed/new content after freeze is rejected and requires a new candidate SHA.

The JSONL file lock coordinates local cooperating processes only. It is not a distributed staging service.

## Credentials and HTTP

`IGitHubAccessTokenProvider` supplies short-lived host-owned tokens per allowlisted repository. Tokens are validated before use and are never returned in operation results or journals. The adapter uses a fixed HTTPS GitHub API origin and explicit API headers; provider errors are surfaced without copying response bodies into audit records.

## Verification boundary

The GitHub host tests use fake HTTP handlers and local JSONL stores. They prove request construction, policy checks, candidate identity validation, staging/freeze behavior, commit/ref reconciliation, and failure handling under deterministic fixtures. They do not prove production GitHub App installation permissions, real credentials, rate-limit behavior, or a live external mutation.
