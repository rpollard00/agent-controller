# Agent Work Controller Architecture

## Status

Draft architecture with prototype and MVP targets.

## Summary

Agent Work Controller is a .NET service that discovers Azure DevOps Boards work items intended for autonomous agent execution, claims and manages those items, provisions a development environment, invokes an agent runtime, tracks lifecycle state, and reports results back to Azure DevOps.

The first supported runtime is `pi` with `pi-materia`. The controller should not be coupled to `pi-materia`; it should treat it as one implementation of a pluggable agent runtime contract.

The prototype should run locally on a developer laptop using SQLite and local workspaces while preserving the same architectural surface as the future MVP service.

The MVP should evolve the same design into a durable internal service using stronger persistence, safer execution environments, Azure DevOps integration, observability, and configurable policy.

---

# 1. Goals

The system should:

1. Discover Azure DevOps Boards work items marked as eligible for autonomous agent execution.
2. Claim eligible work items so only one controller run owns the item at a time.
3. Clone the target Azure DevOps repository into an execution environment.
4. Inject work-item context, repository metadata, and acceptance criteria.
5. Invoke a pluggable agent runtime.
6. Track run status, lifecycle events, logs, and results.
7. Allow `pi-materia` to own PR creation or choose another appropriate completion path.
8. Report status and results back to Azure DevOps Boards.
9. Clean up or retain execution environments according to configuration.
10. Preserve a clean path from local prototype to MVP service.

---

# 2. Non-Goals

The controller should not:

1. Implement the agent reasoning loop.
2. Own the internals of `pi-materia`.
3. Require Kubernetes, Temporal, or Firecracker for the prototype.
4. Merge pull requests automatically.
5. Treat an agent-created PR as automatically safe.
6. Depend permanently on local laptop execution.
7. Depend permanently on one agent runtime.
8. Depend permanently on one environment provider.
9. Become a full project-management system.
10. Hide agent actions from operators.

---

# 3. Key Architecture Decisions

## 3.1 Work Source

The first real work source is:

```text
Azure DevOps Boards
```

The controller should support a local fake work source for development and testing, but the first real integration target is Azure DevOps Boards.

## 3.2 Source-Control Provider

The first source-control provider is:

```text
Azure DevOps Repos
```

The prototype should clone repositories from their remote Azure DevOps Repos origin rather than operating on pre-existing local checkouts.

## 3.3 Prototype Environment

The prototype environment is:

```text
local laptop workspace
```

Each run gets a dedicated local workspace directory. This is not a strong security boundary. It exists to prove the lifecycle and integration model.

## 3.4 First Isolation Upgrade

The first post-prototype isolation upgrade should be:

```text
Docker
```

Docker is the likely MVP environment provider before considering devcontainers, Kubernetes jobs, Firecracker, or host pools.

## 3.5 Repository Profiles

Repository profiles are stored in JSON configuration.

Example:

```json
{
  "repositories": {
    "example-service": {
      "cloneUrl": "https://dev.azure.com/org/project/_git/example-service",
      "defaultBranch": "main",
      "environmentProfile": "local-default",
      "runtimeProfile": "pi-materia-default",
      "allowedPaths": [
        "src/",
        "tests/"
      ]
    }
  }
}
```

## 3.6 Work Item Eligibility

Agent eligibility is configuration-driven using Azure DevOps Boards tags.

Example configuration:

```json
{
  "workSource": {
    "provider": "AzureDevOpsBoards",
    "eligibleTags": [
      "agent-ready"
    ],
    "excludedTags": [
      "agent-blocked",
      "needs-human"
    ],
    "eligibleStates": [
      "New",
      "Approved"
    ],
    "activeState": "Active",
    "completedState": "Resolved"
  }
}
```

The exact tag and status convention remains TBD, but the controller should not hard-code it.

## 3.7 Prototype Concurrency

The prototype should support:

```text
MaxConcurrentRuns = 3
```

Concurrency should be configurable.

## 3.8 Workspace Retention

Workspace retention is configurable.

Default prototype behavior:

```text
leave run workspaces on disk
```

This makes debugging easier during early development. Later, cleanup policies can delete successful runs and retain failed runs for inspection.

## 3.9 PR Ownership

`pi-materia` owns PR creation.

The controller should not force PR creation as the only successful outcome. The runtime may:

1. Open a PR.
2. Push a branch.
3. Produce a patch or artifact.
4. Decide no code change is required.
5. Ask for human input.
6. Fail with a structured reason.

The controller records and reports the result.

---

# 4. High-Level Architecture

```text
┌────────────────────────────────────────────────────────────┐
│ Agent Work Controller                                      │
│                                                            │
│ - Azure DevOps Boards polling                              │
│ - Work item claiming / leasing                             │
│ - Run lifecycle state machine                              │
│ - Local/Docker environment provisioning                    │
│ - Azure DevOps Repos cloning                               │
│ - pi-materia runtime invocation                            │
│ - Runtime event ingestion                                  │
│ - Status reconciliation                                    │
│ - Event log                                                │
│ - Workspace retention / cleanup                            │
└────────────────────────────────────────────────────────────┘
        │                 │                 │
        ▼                 ▼                 ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ Work Source  │   │ Agent Runtime│   │ Environment  │
│ Provider     │   │ Provider     │   │ Provider     │
└──────────────┘   └──────────────┘   └──────────────┘
        │                 │                 │
        ▼                 ▼                 ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ Azure DevOps │   │ pi-materia   │   │ Local /      │
│ Boards       │   │              │   │ Docker       │
└──────────────┘   └──────────────┘   └──────────────┘
        │
        ▼
┌──────────────┐
│ Azure DevOps │
│ Repos        │
└──────────────┘
```

---

# 5. Project Structure

Suggested .NET solution shape:

```text
src/
  AgentController.Api/
  AgentController.Domain/
  AgentController.Application/
  AgentController.Infrastructure/

tests/
  AgentController.Domain.Tests/
  AgentController.Application.Tests/
  AgentController.Infrastructure.Tests/
```

For the prototype, `AgentController.Api` may host both HTTP endpoints and background workers.

A future MVP can split API and workers into separate deployables without changing the domain model.

---

# 6. Core Interfaces

## 6.1 IWorkSource

```csharp
public interface IWorkSource
{
    Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken);

    Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken);

    Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken);

    Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken);
}
```

First implementation:

```text
AzureDevOpsBoardsWorkSource
```

Development implementation:

```text
LocalFakeWorkSource
```

## 6.2 ISourceControlProvider

```csharp
public interface ISourceControlProvider
{
    Task<RepositoryCheckout> CloneAsync(
        RepositorySpec spec,
        EnvironmentHandle environment,
        CancellationToken cancellationToken);

    Task<SourceControlStatus> GetStatusAsync(
        SourceControlRef sourceControlRef,
        CancellationToken cancellationToken);
}
```

First implementation:

```text
AzureDevOpsReposSourceControlProvider
```

Important: because `pi-materia` owns PR creation, the source-control provider does not need to create PRs in the prototype. It should be able to clone repositories and inspect branches/PRs later if needed for reconciliation.

## 6.3 IEnvironmentProvider

```csharp
public interface IEnvironmentProvider
{
    Task<EnvironmentHandle> CreateAsync(
        EnvironmentSpec spec,
        CancellationToken cancellationToken);

    Task<CommandResult> ExecuteAsync(
        EnvironmentHandle handle,
        CommandSpec command,
        CancellationToken cancellationToken);

    Task DestroyAsync(
        EnvironmentHandle handle,
        CancellationToken cancellationToken);
}
```

Prototype implementation:

```text
LocalWorkspaceEnvironmentProvider
```

First MVP isolation implementation:

```text
DockerEnvironmentProvider
```

## 6.4 IAgentRuntime

```csharp
public interface IAgentRuntime
{
    Task<AgentRunHandle> StartAsync(
        AgentRunSpec spec,
        CancellationToken cancellationToken);

    Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken);

    Task CancelAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken);
}
```

First implementation:

```text
PiMateriaRuntime
```

---

# 7. Prototype Architecture

## 7.1 Prototype Stack

```text
Runtime: .NET
App shape: ASP.NET Core app with BackgroundService
Database: SQLite
Work source: Azure DevOps Boards
Dev/test work source: local fake source
Source control: Azure DevOps Repos
Environment: local workspace on laptop
Runtime: pi + pi-materia
Max concurrency: 3
Workspace retention: configurable, default keep
```

## 7.2 Prototype Runtime Flow

```text
1. Worker wakes up.
2. Worker queries Azure DevOps Boards for eligible work items.
3. Worker filters by configured tags, states, priority, and repo mapping.
4. Worker attempts to claim a work item.
5. Controller creates an AgentRun record.
6. Controller creates a local workspace.
7. Controller clones the Azure DevOps Repo into the workspace.
8. Controller writes context files for pi-materia.
9. Controller invokes pi-materia.
10. pi-materia runs the autonomous development loop.
11. pi-materia emits runtime events and/or exits.
12. pi-materia may open a PR, push a branch, ask for help, or fail.
13. Controller records the result.
14. Controller comments and/or updates the Azure DevOps Boards item.
15. Controller retains or cleans workspace according to configuration.
```

## 7.3 Local Workspace Layout

Each run gets a directory:

```text
~/.agent-work-controller/runs/{runId}/
```

Suggested structure:

```text
{runId}/
  repo/
  context/
    work-item.md
    acceptance-criteria.md
    controller-run.json
    repository.json
  logs/
    runtime.stdout.log
    runtime.stderr.log
    controller.log
  artifacts/
  result/
```

## 7.4 Prototype State Machine

The prototype should use explicit persisted states:

```text
queued
claimed
environment_provisioning
environment_ready
repository_cloning
repository_ready
context_injected
agent_starting
agent_running
awaiting_result
result_received
pr_opened
branch_pushed
needs_human
completed
failed
cancelled
cleanup_pending
cleaned_up
```

The controller database is authoritative. Azure DevOps Boards state is an external projection.

## 7.5 Prototype SQLite Data Model

### WorkItems

```text
Id
ExternalSource
ExternalId
ExternalUrl
RepoKey
Title
Body
AcceptanceCriteriaJson
Priority
Status
TagsJson
LeaseOwner
LeaseExpiresAt
CreatedAt
UpdatedAt
```

### AgentRuns

```text
Id
WorkItemId
RuntimeType
RuntimeRunId
EnvironmentId
Status
BranchName
PullRequestUrl
ResultSummary
StartedAt
FinishedAt
LastHeartbeatAt
Error
CreatedAt
UpdatedAt
```

### Environments

```text
Id
ProviderType
RunId
RootPath
RepoPath
Status
CreatedAt
DestroyedAt
MetadataJson
```

### LifecycleEvents

```text
Id
RunId
EventId
EventType
Severity
Message
PayloadJson
CreatedAt
```

### Repositories

Repository profiles are loaded from JSON config, but effective resolved repository metadata may be cached in the database for auditability.

```text
Key
CloneUrl
DefaultBranch
EnvironmentProfile
RuntimeProfile
AllowedPathsJson
CreatedAt
UpdatedAt
```

---

# 8. Azure DevOps Boards Integration

## 8.1 Eligibility

Eligibility is configuration-driven.

Candidate filters may include:

```text
Project
Area path
Iteration path
Work item type
State
Priority
Tags
AssignedTo
CreatedDate
ChangedDate
```

Initial eligibility should be tag-driven.

Example:

```text
tag contains "agent-ready"
tag does not contain "agent-blocked"
state is "New" or "Approved"
```

## 8.2 Claiming

Claiming should prevent duplicate autonomous runs.

Prototype claim strategy:

1. Add or update a controller-owned tag such as `agent-active`.
2. Optionally assign the item to a service identity.
3. Add a comment indicating the controller has claimed the item.
4. Record the lease internally in SQLite.

MVP claim strategy:

1. Use Azure DevOps update operations with optimistic concurrency where possible.
2. Maintain internal lease state in Postgres.
3. Reconcile board state if the internal lease expires.
4. Avoid relying on board tags alone as the source of truth.

## 8.3 Status Projection

Internal controller state should map to Azure DevOps comments, tags, or states.

Example projection:

```text
claimed               → add comment, add agent-active tag
agent_running          → add/update comment
needs_human            → add agent-needs-human tag
pr_opened              → comment with PR URL
completed              → move to configured completed state if enabled
failed                 → add agent-failed tag and comment
cancelled              → add comment
```

The exact board state transitions should be configuration-driven.

---

# 9. Azure DevOps Repos Integration

## 9.1 Clone Behavior

The prototype should clone the repository from Azure DevOps Repos for each run.

The controller should not operate directly on a developer’s existing local checkout.

Clone target:

```text
~/.agent-work-controller/runs/{runId}/repo/
```

## 9.2 Branch and PR Ownership

`pi-materia` owns branch creation, commits, pushes, and PR creation.

The controller should provide enough context for `pi-materia` to do that safely:

```text
repo clone path
base branch
suggested branch name
work item reference
callback URL
acceptance criteria
repository profile
```

The controller should record whatever `pi-materia` reports:

```text
branch name
commit SHA
PR URL
summary
artifacts
failure reason
```

## 9.3 Reconciliation

The controller may later inspect Azure DevOps Repos to verify:

```text
branch exists
PR exists
PR status
build validation status
```

This should be treated as reconciliation, not as the primary completion mechanism.

---

# 10. pi-materia Runtime Event Contract

## 10.1 Event Contract Principles

The runtime event contract should be small but sufficient.

Principles:

1. The controller owns lifecycle state.
2. `pi-materia` reports observations and outcomes.
3. Events should be idempotent.
4. Events should include a stable `eventId`.
5. Events should include the controller `runId`.
6. Events should not assume PR creation is the only successful outcome.
7. The controller maps runtime events to internal state transitions.
8. The controller should still support polling/process exit as a fallback.

## 10.2 Event Envelope

All runtime events should use a common envelope:

```json
{
  "eventId": "evt_01HV...",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "sequence": 12,
  "occurredAt": "2026-06-11T21:00:00Z",
  "eventType": "runtime.status",
  "severity": "info",
  "message": "Running tests",
  "payload": {}
}
```

Required fields:

```text
eventId
runId
occurredAt
eventType
```

Recommended fields:

```text
runtimeRunId
sequence
severity
message
payload
```

## 10.3 Minimal Event Types

### runtime.accepted

The runtime accepted the run and has started work.

Controller effect:

```text
agent_starting or agent_running
```

Example:

```json
{
  "eventId": "evt_001",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:00:00Z",
  "eventType": "runtime.accepted",
  "message": "pi-materia accepted run",
  "payload": {
    "pid": 12345
  }
}
```

### runtime.heartbeat

The runtime is still alive.

Controller effect:

```text
update LastHeartbeatAt
```

Example:

```json
{
  "eventId": "evt_002",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:05:00Z",
  "eventType": "runtime.heartbeat",
  "payload": {
    "phase": "implementation"
  }
}
```

### runtime.status

Human-readable status update.

Controller effect:

```text
append lifecycle event
optionally update visible status/comment
```

Example:

```json
{
  "eventId": "evt_003",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:07:00Z",
  "eventType": "runtime.status",
  "message": "Running unit tests",
  "payload": {
    "phase": "validation"
  }
}
```

### runtime.branch_created

The runtime created or selected a branch.

Controller effect:

```text
record branch name
```

Example:

```json
{
  "eventId": "evt_004",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:12:00Z",
  "eventType": "runtime.branch_created",
  "payload": {
    "branchName": "agent/123-add-retry-handling"
  }
}
```

### runtime.pr_created

The runtime opened a pull request.

Controller effect:

```text
record PR URL
transition to pr_opened or result_received
comment on work item
```

Example:

```json
{
  "eventId": "evt_005",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:20:00Z",
  "eventType": "runtime.pr_created",
  "payload": {
    "pullRequestUrl": "https://dev.azure.com/org/project/_git/repo/pullrequest/123",
    "branchName": "agent/123-add-retry-handling",
    "targetBranch": "main"
  }
}
```

### runtime.needs_human

The runtime cannot proceed without human input or review.

Controller effect:

```text
needs_human
add board tag/comment
```

Example:

```json
{
  "eventId": "evt_006",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:25:00Z",
  "eventType": "runtime.needs_human",
  "severity": "warning",
  "message": "Acceptance criteria conflict with existing behavior",
  "payload": {
    "reason": "ambiguous_acceptance_criteria",
    "questions": [
      "Should 429 responses be retried?"
    ]
  }
}
```

### runtime.completed

The runtime completed its work.

Controller effect:

```text
completed, pr_opened, branch_pushed, or result_received depending on outcome
```

Example:

```json
{
  "eventId": "evt_007",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:30:00Z",
  "eventType": "runtime.completed",
  "message": "Run completed with PR opened",
  "payload": {
    "outcome": "pull_request_opened",
    "pullRequestUrl": "https://dev.azure.com/org/project/_git/repo/pullrequest/123",
    "branchName": "agent/123-add-retry-handling",
    "summary": "Implemented retry handling for transient 5xx responses and added tests."
  }
}
```

Supported completion outcomes:

```text
pull_request_opened
branch_pushed
patch_created
no_changes_needed
needs_human
failed
```

### runtime.failed

The runtime failed.

Controller effect:

```text
failed
add board comment/tag
retain workspace by default
```

Example:

```json
{
  "eventId": "evt_008",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:40:00Z",
  "eventType": "runtime.failed",
  "severity": "error",
  "message": "Tests failed after implementation",
  "payload": {
    "reason": "tests_failed",
    "summary": "Three retry tests failed due to timeout behavior.",
    "logPath": "logs/runtime.stdout.log"
  }
}
```

### runtime.cancelled

The runtime acknowledged cancellation.

Controller effect:

```text
cancelled
cleanup_pending if configured
```

Example:

```json
{
  "eventId": "evt_009",
  "runId": "run_123",
  "runtimeRunId": "pi_456",
  "occurredAt": "2026-06-11T21:45:00Z",
  "eventType": "runtime.cancelled",
  "message": "Runtime cancelled by controller request",
  "payload": {}
}
```

## 10.4 Minimal Required Contract for First Prototype

For the first usable prototype, `pi-materia` only needs to support:

```text
runtime.accepted
runtime.status
runtime.heartbeat
runtime.completed
runtime.failed
```

PR-specific events are useful but not strictly required if `runtime.completed` includes `pullRequestUrl` and `branchName`.

Minimum final result:

```json
{
  "eventId": "evt_final",
  "runId": "run_123",
  "occurredAt": "2026-06-11T21:30:00Z",
  "eventType": "runtime.completed",
  "payload": {
    "outcome": "pull_request_opened",
    "summary": "Implemented requested change.",
    "branchName": "agent/123-example",
    "pullRequestUrl": "https://dev.azure.com/org/project/_git/repo/pullrequest/123"
  }
}
```

---

# 11. MVP Architecture

## 11.1 MVP Stack

```text
Runtime: company-standard .NET LTS
App shape: ASP.NET Core API + worker, split only when needed
Database: Postgres
Work source: Azure DevOps Boards
Source control: Azure DevOps Repos
Environment: Docker first
Runtime: pi-materia first, pluggable runtime interface
Max concurrency: configurable globally and per repo
Workspace retention: configurable with TTL
Observability: structured logs, event table, metrics, traces
```

## 11.2 MVP Environment Provider

The first MVP environment provider should use Docker.

Docker provider responsibilities:

1. Create a per-run container or container-backed workspace.
2. Mount or clone the repository into the environment.
3. Provide only approved environment variables and secrets.
4. Capture stdout/stderr.
5. Enforce CPU and memory limits where practical.
6. Destroy containers after run completion according to policy.
7. Retain failed workspaces or artifacts according to policy.

## 11.3 MVP Persistence

Move from SQLite to Postgres.

Postgres enables:

```text
safe concurrent workers
stronger lease acquisition
better reporting
better operational tooling
future API/worker split
```

## 11.4 MVP Security

The MVP should add:

1. Service identity for Azure DevOps access.
2. Per-run or least-privilege credentials where possible.
3. Configurable secret allowlists.
4. Docker isolation.
5. No automatic PR merge.
6. Per-repository allowed path policy.
7. Global kill switch.
8. Per-repository concurrency limits.
9. Audit log of lifecycle events and operator actions.
10. Configurable retention and cleanup TTLs.

## 11.5 MVP Observability

The MVP should expose:

```text
active runs
queued work
failed runs
runs needing human input
PRs opened
runtime failure rate
average run duration
workspace cleanup failures
Azure DevOps API failures
per-repo concurrency
```

Operator dashboard should show:

```text
Run ID
Azure DevOps work item
Repository
Current state
Runtime
Environment
Started at
Duration
Last heartbeat
Last event
Branch
PR URL
Error summary
Actions: cancel, retry, cleanup, mark needs-human
```

---

# 12. Configuration

## 12.1 Prototype Example

```json
{
  "agentController": {
    "workerId": "reese-laptop",
    "pollIntervalSeconds": 30,
    "maxConcurrentRuns": 3,
    "runRoot": "~/.agent-work-controller/runs",
    "retainSuccessfulRuns": true,
    "retainFailedRuns": true
  },
  "persistence": {
    "provider": "Sqlite",
    "connectionString": "Data Source=agent-controller.db"
  },
  "workSource": {
    "provider": "AzureDevOpsBoards",
    "organizationUrl": "https://dev.azure.com/example-org",
    "project": "ExampleProject",
    "eligibleTags": [
      "agent-ready"
    ],
    "excludedTags": [
      "agent-blocked"
    ],
    "eligibleStates": [
      "New",
      "Approved"
    ],
    "activeState": "Active",
    "completedState": "Resolved"
  },
  "sourceControl": {
    "provider": "AzureDevOpsRepos"
  },
  "environmentProvider": {
    "provider": "LocalWorkspace"
  },
  "runtime": {
    "provider": "PiMateria",
    "piExecutablePath": "pi",
    "defaultMateriaLoadout": "autonomous-dev"
  },
  "repositories": {
    "example-service": {
      "cloneUrl": "https://dev.azure.com/example-org/ExampleProject/_git/example-service",
      "defaultBranch": "main",
      "environmentProfile": "local-default",
      "runtimeProfile": "pi-materia-default",
      "allowedPaths": [
        "src/",
        "tests/"
      ]
    }
  }
}
```

---

# 13. Initial Implementation Plan

## Phase 0: Skeleton

1. Create .NET solution.
2. Add API project.
3. Add Domain/Application/Infrastructure projects or folders.
4. Add SQLite persistence.
5. Add lifecycle event table.
6. Add JSON config loading.
7. Add repository profile config.

## Phase 1: Local Lifecycle (No Azure DevOps, No pi-materia)

Phase 1 builds a fully testable local lifecycle on top of the Phase 0 skeleton. It uses a
local fake work source, SQLite-backed controller state, and mock runtime event ingestion so
the entire controller lifecycle is exerciseable without any external dependency. Azure DevOps
Boards, Azure DevOps Repos, Docker, and pi-materia are not involved.

### 1a. Design Rules for Phase 1

1. **API and worker code must depend on application abstractions, not EF Core.**
   The `AgentController.Api` project references `AgentController.Application` and
   `AgentController.Infrastructure` (for DI registration only). It never references
   `Microsoft.EntityFrameworkCore` directly. All persistence access goes through
   application-layer interfaces defined in `AgentController.Application`.

2. **EF Core lives only in Infrastructure.** The `AgentController.Infrastructure` project
   contains the `DbContext`, entity type configurations, and repository implementations.
   No other project references EF Core packages.

3. **The worker advances controller-owned states only until `awaiting_result`, then waits
   for runtime events.** The worker owns everything up to and including
   `RunLifecycleState.AwaitingResult`. After that, the runtime (or mock runtime endpoint)
   drives state transitions through event ingestion. The worker never transitions a run
   out of `AwaitingResult` except for stale-timeout recovery.

4. **Migrations run from a dedicated console app, never from API or worker startup.**
   The API and worker register the `DbContext` in DI for query/command use but never call
   `Database.MigrateAsync()` or `Database.EnsureCreated()`. A separate
   `AgentController.Migrations` console project is the sole owner of schema evolution.

5. **Runtime events are idempotent.** Every inbound runtime event carries a unique
   `eventId`. The controller must check for duplicate `eventId` values before processing
   and reject duplicates with a clear response.

### 1b. SQLite Persistence Setup

Add EF Core with SQLite to `AgentController.Infrastructure`. Core tables map to the
prototype data model defined in §7.5:

| Table | Purpose |
|-------|---------|
| `WorkItems` | Persisted fake work items and claimed real work items |
| `AgentRuns` | One row per controller-orchestrated run |
| `Environments` | Per-run environment metadata |
| `LifecycleEvents` | Controller-authoritative event log |
| `Repositories` | Cached repository profiles from JSON config |

Key mapping decisions:
- JSON-like columns (`Tags`, `AcceptanceCriteria`, `Metadata`, `Payload`) are stored as
  `TEXT` columns. The repository layer handles serialization; domain types remain
  dictionary/list-based.
- All tables carry `CreatedAt` and `UpdatedAt` timestamps.
- `LifecycleEvents` has a **unique index on `(RunId, EventId)` WHERE `EventId IS NOT NULL`**
  for runtime event idempotency.
- `WorkItems` has an index on `(Status, LeaseExpiresAt)` for efficient polling queries.
- `AgentRuns` has an index on `(Status, LastHeartbeatAt)` for stale-run detection.

### 1c. Persistence Abstractions (Application Layer)

Define storage-agnostic contracts in `AgentController.Application` that API and worker
code consume. These interfaces must not leak EF Core types:

```csharp
// IWorkItemStore — CRUD for local fake work items + claim/lease
Task<WorkCandidate> CreateAsync(CreateWorkItemRequest request, CancellationToken ct);
Task<IReadOnlyList<WorkCandidate>> ListAsync(WorkItemListQuery query, CancellationToken ct);
Task<WorkCandidate?> GetByIdAsync(string id, CancellationToken ct);
Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(WorkQuery query, CancellationToken ct);
Task<ClaimResult> TryClaimAsync(string workItemId, ClaimRequest claim, CancellationToken ct);
Task UpdateStatusAsync(string workItemId, string status, CancellationToken ct);

// IAgentRunStore — run lifecycle persistence
Task<AgentRunHandle> CreateAsync(CreateRunRequest request, CancellationToken ct);
Task<AgentRunHandle?> GetByIdAsync(string runId, CancellationToken ct);
Task UpdateStatusAsync(string runId, RunLifecycleState status, CancellationToken ct);
Task UpdateRuntimeFieldsAsync(string runId, RuntimeFieldUpdate update, CancellationToken ct);
Task<IReadOnlyList<AgentRunHandle>> ListAsync(RunListQuery query, CancellationToken ct);
Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(TimeSpan staleTimeout, CancellationToken ct);

// ILifecycleEventStore — append-only event log
Task AppendAsync(LifecycleEvent evt, CancellationToken ct);
Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(string runId, CancellationToken ct);
Task<bool> ExistsByEventIdAsync(string runId, string eventId, CancellationToken ct);

// IEnvironmentStore — environment metadata
Task<EnvironmentHandle> CreateAsync(CreateEnvironmentRequest request, CancellationToken ct);
Task UpdateStatusAsync(string environmentId, string status, CancellationToken ct);

// IRepositoryStore — cached repository profiles
Task<RepositoryProfile?> GetByKeyAsync(string key, CancellationToken ct);
Task UpsertAsync(RepositoryProfile profile, CancellationToken ct);
```

### 1d. EF Core Infrastructure Implementation

Implement the above interfaces in `AgentController.Infrastructure` using EF Core with
SQLite. Register implementations via `AddScoped` (or `AddDbContext` + scoped repositories)
in `AgentControllerServiceCollectionExtensions`.

Claim/lease behavior:
- `TryClaimAsync` uses a transaction: read the work item, verify it is unclaimed
  (`LeaseOwner IS NULL OR LeaseExpiresAt < NOW`), set `LeaseOwner` and
  `LeaseExpiresAt`, and commit. Return failure if the row changed between read and write.
- SQLite's serialized write transactions make this safe for single-process use. A future
  Postgres provider would use `SELECT ... FOR UPDATE SKIP LOCKED`.

### 1e. Dedicated Migration Runner

Add `src/AgentController.Migrations/` as a console app:

```csharp
// Program.cs (sketch)
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAgentControllerOptions(builder.Configuration);
builder.Services.AddAgentControllerDbContext(builder.Configuration); // DbContext only

var app = builder.Build();
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
await db.Database.MigrateAsync();
```

The initial migration creates all five Phase 1 tables with indexes and constraints.
Neither `AgentController.Api` nor any worker startup path calls `MigrateAsync()` or
`EnsureCreated()`.

### 1f. Local Fake Work Source

Implement `LocalFakeWorkSource : IWorkSource` in `AgentController.Infrastructure`.
Unlike the no-op stub, this implementation:

1. Calls `IWorkItemStore.FindEligibleAsync` to discover persisted fake work items matching
   configured eligible/excluded tags and states (from `WorkSourceOptions`).
2. Calls `IWorkItemStore.TryClaimAsync` atomically to claim a candidate.
3. Calls `IWorkItemStore.UpdateStatusAsync` to project controller status back.
4. Never contacts Azure DevOps.

The `WorkSourceOptions.Provider` value `"LocalFake"` selects this implementation at DI
registration time.

### 1g. Local Fake Work Item Endpoints

Add minimal API endpoints to `AgentController.Api`:

```text
POST   /work-items          — create a local fake work item
GET    /work-items          — list local fake work items (with optional status/tag filters)
GET    /work-items/{id}     — get a single work item by ID
```

Creation accepts `repoKey`, `title`, `body`, `acceptanceCriteria`, `priority`, `status`,
and `tags`, with sensible defaults (`status = "New"`, `priority = 0`). Responses expose
all persisted fields so operators can seed work and confirm worker eligibility.

All endpoints consume `IWorkItemStore` — never EF Core directly.

### 1h. Controller Run Lifecycle Service

Add an application service in `AgentController.Application` (or a new
`AgentController.Application/Services/` folder) that owns AgentRun lifecycle transitions:

```csharp
public interface IRunLifecycleService
{
    Task<AgentRunHandle> CreateRunForWorkItemAsync(
        string workItemId, string workerId, CancellationToken ct);
    Task TransitionAsync(string runId, RunLifecycleState targetState, CancellationToken ct);
    Task AppendControllerEventAsync(string runId, string eventType, string message,
        IReadOnlyDictionary<string, object?>? payload, CancellationToken ct);
    Task IngestRuntimeEventAsync(RuntimeEvent evt, CancellationToken ct);
    Task<IReadOnlyList<AgentRunHandle>> FindStaleRunsAsync(
        TimeSpan staleTimeout, CancellationToken ct);
    Task RecoverStaleRunAsync(string runId, CancellationToken ct);
    bool IsTerminal(RunLifecycleState state);
}
```

The service coordinates `IAgentRunStore`, `ILifecycleEventStore`, and `IWorkItemStore`
to ensure consistent state transitions. Every transition appends a controller lifecycle
event. The service validates that transitions follow the legal state graph.

### 1i. Worker Polling Loop (Phase 1 Behavior)

Update `PollingWorker.ExecuteAsync` to implement a real Phase 1 lifecycle when
`WorkerEnabled = true`:

1. **Guard concurrency.** Count runs in non-terminal states. If at or above
   `MaxConcurrentRuns`, skip this poll cycle.
2. **Discover eligible work.** Call `IWorkSource.FindEligibleAsync` with configured
   tag/state filters.
3. **Claim.** For each candidate, call `IWorkSource.TryClaimAsync`. On success,
   proceed; on failure, skip to next candidate.
4. **Create run.** Call `IRunLifecycleService.CreateRunForWorkItemAsync` — this creates
   an `AgentRun` in `Claimed` state and appends a lifecycle event.
5. **Advance controller-owned states.** Transition through each controller-owned state,
   appending a lifecycle event at each step:
   ```text
   Claimed → EnvironmentProvisioning → EnvironmentReady →
   RepositoryCloning → RepositoryReady → ContextInjected →
   AgentStarting → AgentRunning → AwaitingResult
   ```
   In Phase 1, environment provisioning, repository cloning, and agent starting are
   **simulated**: the worker records the lifecycle event and immediately transitions to
   the next state. No real environments, clones, or runtime invocations occur.
6. **Stop at `AwaitingResult`.** After transitioning to `AwaitingResult`, the worker
   releases the run. It does not poll for completion or invoke any runtime. The mock
   runtime event endpoint (1j) or stale recovery (1k) will advance it further.
7. **Stale recovery pass.** Each poll cycle, after attempting new work, check for runs
   stuck in `AwaitingResult` past the configured stale timeout and recover them.

### 1j. Mock Runtime Event Ingestion Endpoint

Add a `POST /runs/{runId}/events` endpoint to `AgentController.Api`. The endpoint:

- Accepts a `RuntimeEvent` JSON body matching the envelope in §10.2.
- Supports these event types:
  - `runtime.accepted` — runtime acknowledged the run
  - `runtime.status` — human-readable status update
  - `runtime.heartbeat` — runtime is still alive (updates `LastHeartbeatAt`)
  - `runtime.completed` — runtime finished (transitions to `Completed`/`PrOpened`
    depending on `payload.outcome`)
  - `runtime.failed` — runtime failed (transitions to `Failed`)
  - `runtime.needs_human` — runtime needs human input (transitions to `NeedsHuman`)
  - `runtime.cancelled` — runtime acknowledged cancellation (transitions to `Cancelled`)
- Checks idempotency via `eventId`. Returns `409 Conflict` if the event was already
  processed.
- Delegates to `IRunLifecycleService.IngestRuntimeEventAsync`.
- Rejects events for runs in terminal states (`Completed`, `Failed`, `Cancelled`,
  `CleanedUp`).
- Returns validation errors for unsupported event types or missing required fields.

### 1k. Stale Runtime Recovery

Add configurable stale-timeout recovery:

- `AgentControllerOptions.StaleRunTimeout` (default: 30 minutes).
- Each poll cycle, after attempting new work, the worker calls
  `IRunLifecycleService.FindStaleRunsAsync`.
- A run is stale if it is in `AwaitingResult` and its `LastHeartbeatAt` is older than
  `NOW - StaleRunTimeout` (or `StartedAt` if no heartbeat was ever received).
- Stale runs are transitioned to `NeedsHuman` (not `Failed`). The rationale: the runtime
  may still be running but unable to report; a human should decide whether to retry or
  cancel.
- A controller lifecycle event is appended with severity `Warning` documenting the
  timeout.

### 1l. Run List and Detail Endpoints

Add API endpoints to inspect runs:

```text
GET /runs              — list runs (supports ?status= and ?workItemId= filters)
GET /runs/{runId}      — run detail with full lifecycle
```

Run detail response includes:
- The associated `WorkCandidate` (work item)
- Current `RunLifecycleState`
- Runtime fields: `RuntimeRunId`, `LastHeartbeatAt`, `StartedAt`, `FinishedAt`, `Error`
- Environment record when present
- Ordered `LifecycleEvent[]` list

This makes the full local controller lifecycle inspectable end-to-end.

## Phase 2: Azure DevOps Boards Provider

1. Query work items by configured tags and states.
2. Resolve repo key from work item fields or tags.
3. Claim work item using configured status/tag/comment behavior.
4. Project controller status back to the work item.
5. Add comments for run start, run result, failure, and PR link.

## Phase 3: Azure DevOps Repos Clone

1. Resolve repository profile from JSON config.
2. Clone repo into per-run workspace.
3. Checkout default branch.
4. Write repository metadata into context files.
5. Record clone result in lifecycle events.

## Phase 4: Local Environment Provider

1. Create per-run local workspace.
2. Write context files.
3. Expose paths to runtime adapter.
4. Implement configurable retention.
5. Default to retaining all workspaces.

## Phase 5: Pi-Materia Runtime Adapter

1. Generate `controller-run.json`.
2. Invoke `pi` with selected loadout/profile.
3. Capture stdout/stderr.
4. Track process status.
5. Receive runtime webhook events.
6. Support cancellation.
7. Record final outcome.

## Phase 6: Result Reporting

1. Handle `runtime.completed`.
2. Handle PR URL if provided.
3. Handle branch-only result.
4. Handle no-change result.
5. Handle needs-human result.
6. Handle failure result.
7. Update Azure DevOps Boards item with status and comments.

## Phase 7: Reconciliation

1. Detect runs with stale heartbeats.
2. Detect local processes that exited without final event.
3. Detect failed cleanup attempts.
4. Reconcile PR URL or branch info if runtime provided partial result.
5. Mark unresolved runs as `needs_human` after timeout.

---

# 14. Prototype Defaults

```text
Runtime: .NET
Persistence: SQLite
Work source: Azure DevOps Boards
Dev work source: local fake source
Source control: Azure DevOps Repos
Repository handling: clone remote repo per run
Environment: local laptop workspace
Runtime: pi-materia
Max concurrency: 3
Workspace cleanup: configurable
Default workspace behavior: retain workspaces
Eligibility: configuration-driven Azure DevOps tags
PR creation: owned by pi-materia
Merge behavior: never automatic
```

---

# 15. MVP Defaults

```text
Runtime: company-standard .NET LTS
Persistence: Postgres
Work source: Azure DevOps Boards
Source control: Azure DevOps Repos
Repository handling: clone remote repo per run
Environment: Docker
Runtime: pi-materia first, pluggable interface
Concurrency: configurable globally and per repository
Workspace cleanup: configurable with TTL
Eligibility: configuration-driven Azure DevOps tags/states
PR creation: owned by runtime
Merge behavior: human-controlled
Observability: OpenTelemetry plus lifecycle event log
```

---

# 16. Remaining Open Questions

1. What exact Azure DevOps tag marks work as agent-eligible?
2. Which Azure DevOps field or tag maps a work item to a repository key?
3. Which work item states are eligible for autonomous pickup?
4. Should the controller assign active work items to a service identity?
5. Should the controller move board state, add tags/comments only, or both?
6. What branch naming convention should be suggested to `pi-materia`?
7. What timeout should mark a run as stale?
8. Should stale runs be retried automatically or marked `needs_human`?
9. Should successful retained workspaces expire after a TTL?
10. What minimum Azure DevOps permissions should the service identity have?

---

# 17. Architectural Principle

The controller should be boring, durable, and inspectable.

`pi-materia` can be experimental.

The controller’s role is not to make the agent smart. Its role is to make autonomous software work governable, auditable, cancellable, and safe enough to adopt.
