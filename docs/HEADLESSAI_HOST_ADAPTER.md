<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry HeadlessAi Host Adapter

## Purpose

`Foundry.HeadlessAiHost` is the first concrete in-process adapter between Foundry's lease-governed turn port and HeadlessAi's provider executor. It deliberately does not become a GitHub client, task scheduler, authorization service, or credential store.

The adapter maps a single Foundry request into a versioned `HeadlessAiAgentTurnEnvelope`, creates one immutable `HeadlessAiAgentTurnRequest` snapshot, executes that exact snapshot once, and maps only the normalized result back to Foundry. The raw provider response is not copied into Foundry state.

## Build the source integration

The project references the HeadlessAi source checkout at `../../headlessai`. From the Foundry repository root, place a checkout of HeadlessAi at `headlessai/` using the pinned commit recorded in the CI workflow, then run:

```bash
dotnet test tests/Foundry.Workflow.Tests/Foundry.Workflow.Tests.csproj --configuration Release
dotnet build src/Foundry.HeadlessAiHost/Foundry.HeadlessAiHost.csproj --configuration Release
```

This source integration keeps the repositories independently versioned and avoids requiring an unpublished NuGet package. The CI workflow checks out the exact HeadlessAi commit used for the adapter build. Updating that pin is an explicit compatibility change and must be verified against both repositories' tests.

## Compose the provider executor

The host constructs a `HeadlessAiProfile`, the selected provider adapter, an optional request-time header provider, and a shared `HttpClient`; it then supplies the resulting `HeadlessAiAgentTurnExecutor` to `HeadlessAiAgentTurnClient`.

Provider credentials are resolved by the host-side request-header provider at request time. Do not put secrets in profile settings, source control, prompts, evidence, task records, or model context. GitHub credentials are a separate host-side capability and are not part of this adapter.

## Run one real provider turn

The optional `Foundry.HeadlessAiHost.Console` project is a manual smoke runner. It creates a local durable Foundry task, claims it, transitions it to `Investigating`, and invokes one real OpenAI Responses API turn through the concrete adapter. It does not inspect or modify GitHub repositories, create commits, or dispatch CI.

Set these variables in the host process environment; never put the API key in command-line arguments, task data, prompts, or source control:

- `OPENAI_API_KEY` — provider secret, resolved at request time by the host header provider.
- `OPENAI_MODEL` — the model identifier enabled for the host's provider account.
- `FOUNDRY_AGENT_PROMPT` — bounded task context to send to the provider.
- `FOUNDRY_AGENT_INSTRUCTIONS` — optional host instruction override.
- `FOUNDRY_DATA_DIR` — optional durable journal directory; defaults to `.foundry-host-data` under the current directory.

From the Foundry repository root, with the pinned HeadlessAi checkout at `headlessai/`, run:

```bash
dotnet run --project src/Foundry.HeadlessAiHost.Console/Foundry.HeadlessAiHost.Console.csproj -- \
  <task-id> <correlation-id> <owner/repo> <issue-number> \
  <base-sha> <candidate-sha> <feature-branch> <allowed-path>
```

Use a new task ID for each new task record in the same data directory. Supply full 40-character base and candidate SHAs and an isolated feature branch. The candidate SHA and allowed path are caller-supplied in this smoke runner; they are not yet independently verified by a trusted GitHub adapter. The prompt is the only task context sent to the model in this first provider smoke path, so include only the material the model is meant to reason about.

A successful run prints the normalized, provenance-bound result. It does not mean a patch was applied, CI passed, or a PR was created. The returned proposal remains untrusted and requires the later trusted-host GitHub and CiAi integrations.

## Safety properties

- Foundry must already have validated the active lease and `Investigating` lifecycle before it calls the adapter.
- `Prepare` is local and side-effect-free; it validates and snapshots the exact request.
- `ExecuteAsync` rejects a prepared snapshot created for a different request instance.
- The executor receives the exact immutable snapshot whose fingerprint Foundry records.
- HeadlessAi output is validated and mapped as untrusted proposal data; it grants no repository capability and causes no lifecycle transition.
- The adapter never exposes `RawResponse` to Foundry.
- The adapter performs no retries, repository writes, branch operations, CI dispatch, PR creation, merge, release, deployment, or package publication.

## Current limit

This closes the compile-time Foundry-to-HeadlessAi adapter gap and adds a manual host composition root in `Foundry.HeadlessAiHost.Console`; the console project is built by CI. It does not yet prove a live provider invocation. Provision `OPENAI_API_KEY`, `OPENAI_MODEL`, and the bounded prompt in the host environment, then run the documented smoke command to verify one real turn. The manual provider smoke runner does not itself verify a GitHub candidate or apply a patch. The separate repository-operation feature branch now contains trusted GitHub candidate branch/read/stage/commit and allowlisted Actions dispatch/correlation components; live GitHub credentials/network proof, exact-candidate CiAi evidence delivery, durable repair-decision ingestion, bounded end-to-end repair, and draft-PR handoff remain separate gates.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
