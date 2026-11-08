# GitHub Host Adapter UML

## Component neighborhood

```mermaid
classDiagram
    class AuthorizedRepositoryOperationDispatcher
    class GitHubRepositoryOperationAdapter {
      +ExecuteAsync(operation, cancellationToken)
    }
    class GitHubRepositoryOperationPolicy {
      +DemandRepository(repository)
      +DemandCandidateBranch(branch)
    }
    class IGitHubAccessTokenProvider {
      <<interface>>
      +GetTokenAsync(repository, cancellationToken)
    }
    class IGitHubRepositoryStagingStore {
      <<interface>>
      +Stage(blob)
      +GetStaged(repository, branch, sha)
      +FreezeBatch(repository, branch, sha)
    }
    class GitHubActionsWorkflowDispatcher {
      +DispatchAsync(task, repository, workflow, branch, sha)
      +FindCorrelatedRunAsync(receipt)
      +VerifyRequiredJobsAsync(receipt, correlation, requiredJobs)
    }
    class IGitHubActionsDispatchJournal
    class JsonlGitHubRepositoryStagingStore
    class JsonlGitHubActionsDispatchJournal
    AuthorizedRepositoryOperationDispatcher --> GitHubRepositoryOperationAdapter : host adapter port
    GitHubRepositoryOperationAdapter --> GitHubRepositoryOperationPolicy : allowlist / namespace
    GitHubRepositoryOperationAdapter --> IGitHubAccessTokenProvider : credentials
    GitHubRepositoryOperationAdapter --> IGitHubRepositoryStagingStore : stage / freeze
    GitHubRepositoryOperationAdapter --> GitHubActionsWorkflowDispatcher : optional CI dispatch
    GitHubActionsWorkflowDispatcher --> IGitHubActionsDispatchJournal : intent / correlation / verification
    JsonlGitHubRepositoryStagingStore ..|> IGitHubRepositoryStagingStore
    JsonlGitHubActionsDispatchJournal ..|> IGitHubActionsDispatchJournal
```

## Candidate commit sequence

```mermaid
sequenceDiagram
    participant Dispatcher as Foundry Dispatcher
    participant Adapter as GitHub Repository Adapter
    participant Policy as Host Policy
    participant Tokens as Token Provider
    participant Stage as Staging Store
    participant GitHub

    Dispatcher->>Adapter: Execute(typed operation)
    Adapter->>Policy: Demand repository and agents branch
    Adapter->>Tokens: Get short-lived host token
    alt create branch
        Adapter->>GitHub: Read exact base commit; refuse existing branch
        Adapter->>GitHub: Create ref at exact base SHA
    else read file
        Adapter->>GitHub: Verify candidate tip and read path at immutable SHA
    else write file
        Adapter->>GitHub: Create blob
        Adapter->>Stage: Record blob SHA + content digest
    else create commit
        Adapter->>Stage: Freeze exact candidate batch
        Adapter->>GitHub: Create tree and single-parent commit
        Adapter->>GitHub: Non-force ref update
        Adapter->>GitHub: Re-read and verify ref/tree/parent/message
    end
    Adapter-->>Dispatcher: Validated operation reference and candidate SHA
```

## CI dispatch and evidence sequence

```mermaid
sequenceDiagram
    participant Host
    participant Actions as Actions Dispatcher
    participant Journal as Dispatch Journal
    participant GitHub
    participant Workflow as Trusted development Workflow
    participant Evidence as Foundry Host

    Host->>Actions: Dispatch(allowlisted key, candidate ref/SHA)
    Actions->>GitHub: Verify candidate tip and pinned development SHA
    Actions->>Journal: Flush Dispatching intent
    Actions->>GitHub: workflow_dispatch ref=development + fixed request/candidate inputs
    GitHub-->>Actions: Accepted
    Actions->>Journal: Acknowledge
    Workflow->>Workflow: Verify trusted source and detached candidate checkout
    Workflow->>Workflow: Publish candidate-checkout provenance artifact
    Actions->>GitHub: Discover exact request-ID run
    Actions->>Journal: Correlate exact run ID/attempt
    Actions->>GitHub: Re-fetch exact attempt and required jobs
    Actions->>Journal: Verified only when all required jobs succeed
    Evidence->>GitHub: Retrieve and validate candidate provenance artifact (host integration pending)
```

## Boundaries

The workflow run's branch/SHA describe trusted `development` workflow source; the candidate checkout has separate provenance. Dispatch acceptance is not CI evidence. Fake HTTP tests are not live GitHub proof. JSONL staging and dispatch journals are local-host persistence, not distributed coordination. The host still needs to retrieve and validate the candidate provenance artifact and ingest verified evidence into the Foundry task lifecycle.
