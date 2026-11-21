# AgentOnboarding host admission

## Ownership and dependency

CiAi owns the deterministic \`AgentOnboardingValidator\` contract. Foundry does not copy or reimplement its Markdown rules.

The Foundry host compiles the authoritative CiAi source files from this immutable source revision:

- Repository: \`TrentBest/TheSingularityWorkshop.CiAi\`
- Commit: \`18b6bb8b912e80d446b8423ada2061a2deaa7bf6\`
- Files: \`AgentOnboardingValidator.cs\` and \`RepositoryIdentity.cs\`

\`Foundry.CiAi.Contracts.csproj\` links those source files into a small contract assembly. The Foundry proof workflow checks out that exact CiAi commit into \`external/CiAi\` (without initializing CiAi's unrelated submodules), verifies the checkout SHA, and builds the linked-source project. This avoids a duplicate parser and avoids pretending that the CiAi alpha package is already a published dependency.

For a local build, check out the same CiAi commit into \`external/CiAi\` before building Foundry. Do not silently advance the pin: changing it is a dependency-contract update that must be reviewed and tested.

## Exact-source retrieval

\`GitHubAgentOnboardingAdmissionVerifier\` is the trusted source adapter. For each governed admission it:

1. Applies the host's repository allowlist and validates the full target SHA.
2. Retrieves \`refs/heads/development\` and requires it to resolve to the requested target SHA.
3. Reads only the root \`AgentOnboarding.md\` using the immutable commit SHA in the GitHub Contents API request. It never reads the mutable branch name, default branch, README, \`AGENTS.md\`, or a fallback path.
4. Supplies the exact requested SHA, resolved commit SHA, path, read status, and retrieved content to CiAi's \`AgentOnboardingValidator.Evaluate\`.
5. Maps the complete CiAi result, including its failure code and content digest, into Foundry's bounded \`AgentOnboardingAdmissionEvidence\` record.

A missing file is passed to CiAi as \`Missing\`. Invalid or unsupported API content is treated as unreadable. Non-404 GitHub failures abort the operation; they do not create eligible evidence. The validator remains the only owner of document contract interpretation.

The current-development-head requirement is intentionally strict. If \`development\` advances after a task's base SHA was selected, that task cannot be newly admitted or resumed against the stale SHA. A new task must bind to the new exact head.

## Enforcement points

The host exposes \`OnboardingAdmissionVerifier\` and two convenience operations:

- \`InitializeAsync\` revalidates every restored governed task before the host resumes lifecycle work. A failed check leaves restored governed work unavailable.
- \`SubmitTaskAsync\` routes task creation through \`PersistentWorkTaskCoordinator.SubmitAsync\`, which verifies before in-memory or durable task creation.

The same verifier must be supplied to the following workflow components:

- \`FoundryHeadlessAiTurnCoordinator\` verifies persisted evidence before a model turn and revalidates immediately before the provider call.
- \`AuthorizedRepositoryOperationDispatcher\` verifies before writing audit intent and revalidates again inside the per-task reservation immediately before repository transport.

For governed repositories, a missing verifier, missing persisted evidence, changed result, stale development head, unreadable source, or ineligible CiAi result is a hard denial. The evidence must match the task's stored repository, base SHA, contract version, root path, outcome, and content digest.

## Independent gates remain independent

Onboarding admission is necessary but never sufficient. It does not grant a capability, lease, repository allowlist membership, CI workflow permission, retry budget, recovery receipt, human approval, or permission to merge, publish, release, deploy, delete branches, or dispatch privileged work.

The repository-operation dispatcher still demands the exact host-issued capability, validates task/repository/branch/candidate scope, serializes against task lifecycle mutations, and audits the operation. The GitHub adapter still owns its repository/branch/workflow allowlists and immutable candidate operations. CiAi onboarding content is untrusted data and cannot modify any of those host policies.

## Verification scope

The verifier tests cover:

- Current \`development\` ref resolution and immutable SHA-pinned content retrieval.
- Rejection when the requested target is not the current development head.
- Missing root file rejection without fallback.
- Malformed UTF-8 and over-size content rejection through CiAi's validator.
- Final pre-transport rejection when admission changes after audit intent.

Ordinary CI proves compilation and fake-transport behavior only. It does not prove production GitHub App credential provisioning, real host initialization against live repositories, or an end-to-end privileged dispatch. Those remain explicit operational verification gates.
