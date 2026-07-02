# Board Provisioning — Tag Recipe and Eligibility Model

This document describes how agent router discovers, claims, and processes Azure DevOps Boards work items. It covers the tag-based eligibility model, the `repo:{key}` association convention, lifecycle state projection, exclusion tags, and the full board item lifecycle from creation through PR.

---

## 1. Overview

Agent router polls an Azure DevOps Board on a configurable interval. It discovers work items that match an **eligibility model** (tags + states), validates that the item references a known repository via a `repo:{key}` tag, claims the item for exclusive execution, and drives it through a lifecycle that ends in a PR, branch push, completion, failure, or human escalation.

The controller's internal database is the authoritative source of truth. Azure DevOps Boards state and tags are an **external projection** — best-effort and idempotent.

---

## 2. Tag-Based Eligibility Model

Eligibility is determined by three configuration dimensions in the `workSource` section:

| Setting | Purpose | Default |
|---------|---------|---------|
| `eligibleTags` | Tags that **must** be present for the item to be considered | `["agent-ready"]` |
| `excludedTags` | Tags that **exclude** the item even if it otherwise matches | `["agent-active", "agent-failed", "agent-needs-human"]` |
| `eligibleStates` | Board states the item must be in to be polled | `["New", "Approved"]` |

### 2.1 Eligibility Query (WIQL)

The controller builds a WIQL query that combines these filters:

```sql
SELECT [System.Id] FROM WorkItems
WHERE [System.TeamProject] = 'MyProject'
  AND [System.State] IN ('New', 'Approved')
  AND [System.Tags] CONTAINS 'agent-ready'
  AND [System.Tags] NOT CONTAINS 'agent-active'
  AND [System.Tags] NOT CONTAINS 'agent-failed'
  AND [System.Tags] NOT CONTAINS 'agent-needs-human'
ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.CreatedDate] DESC
```

Each entry in `eligibleTags` adds a `CONTAINS` clause. Each entry in `excludedTags` adds a `NOT CONTAINS` clause.

### 2.2 Tagging a Board Item for Agent Pickup

To make a work item eligible:

1. Ensure the item is in an **eligible state** (e.g. `New` or `Approved`).
2. Add the **eligibility tag** (e.g. `agent-ready`).
3. Add a **repository association tag** in the form `repo:{key}` where `{key}` matches a configured repository profile key (see §3).
4. Ensure no **exclusion tag** is present (e.g. `agent-active`, `agent-failed`, `agent-needs-human`, `agent-blocked`).

Example tag set for an eligible item:

```
agent-ready; repo:example-service
```

---

## 3. Repository Association — `repo:{key}` Tag

The `repo:{key}` tag associates a work item with a specific repository profile. The `{key}` portion must match a key defined in the `repositories` configuration section.

### 3.1 How It Works

1. **Discovery**: The controller reads `System.Tags` from each ADO work item and looks for a tag starting with `repo:`. The remainder (e.g. `repo:example-service` → `example-service`) becomes the `RepoKey`.

2. **Validation**: After discovery, the controller validates the `RepoKey` against configured repository profiles. Three outcomes are possible:

   | Scenario | Behavior |
   |----------|----------|
   | No `repo:` tag present | Item is **skipped silently** — treated as not-eligible. No comment is posted. |
   | `repo:` tag present but key does not match any profile | Item is **skipped** and a **clarifying comment** is posted on the ADO work item: `"Skipped: no repository profile matches the \`repo:xxx\` tag. Configure a matching repository profile or correct the tag."` This makes typos visible on the board. |
   | `repo:` tag matches a profile | Item proceeds through the lifecycle. The matched profile provides `cloneUrl`, `defaultBranch`, `allowedPaths`, etc. |

### 3.2 Repository Profile Configuration

Repository profiles are defined in the `repositories` configuration section:

```json
{
  "repositories": {
    "example-service": {
      "cloneUrl": "https://dev.azure.com/org/project/_git/example-service",
      "defaultBranch": "main",
      "allowedPaths": ["src/", "tests/"]
    }
  }
}
```

The `repo:example-service` tag on a work item resolves to this profile.

---

## 4. Lifecycle State Projection (`activeState` / `completedState`)

The controller maps its internal lifecycle states to Azure DevOps Board states using two configuration values:

| Setting | Purpose | Example |
|---------|---------|---------|
| `activeState` | Board state when the controller is actively working on the item | `"Active"` |
| `completedState` | Board state when the controller completes the item successfully | `"Resolved"` |

### 4.1 State Mapping Table

| Controller Internal State | ADO Board State | ADO Tags Added | Comment Posted |
|--------------------------|-----------------|----------------|----------------|
| `Claimed` | `activeState` (e.g. `Active`) | `agent-active`, `agent-worker:{workerId}` | "Agent controller claimed this work item and started processing." |
| `AgentRunning` | `activeState` | — | "Agent runtime is now executing." |
| `AwaitingResult` | `activeState` | — | "Agent runtime is working; awaiting result." |
| `PrOpened` | `completedState` (e.g. `Resolved`) | — | "Pull request opened: {url}" |
| `BranchPushed` | `completedState` | — | "Branch pushed: {branchName}" |
| `Completed` | `completedState` | — | "Run completed: {summary}" |
| `Failed` | *(unchanged — stays in `activeState`)* | `agent-failed` | "Run failed: {error}" |
| `NeedsHuman` | *(unchanged — stays in `activeState`)* | `agent-needs-human` | "Run requires human input: {summary}" |
| `Cancelled` | *(unchanged)* | — | "Run cancelled." |

### 4.2 Design Notes

- **Failed and NeedsHuman items stay in `activeState`** so they remain visible on the active board columns. They are excluded from re-pickup by their exclusion tags (see §5).
- **Projection is idempotent**: re-projecting the same state is a no-op at the ADO API level (PATCH with the same value is harmless).
- **Projection is best-effort**: failures in external projection do not prevent the controller's internal state transition from completing. The next poll cycle may retry.

---

## 5. Exclusion Tags — Preventing Re-Pickup

Exclusion tags prevent the controller from re-picking up work items it has already acted on. They are implemented as `NOT CONTAINS` clauses in the WIQL discovery query.

### 5.1 Default Exclusion Tags

| Tag | When Added | Effect |
|-----|-----------|--------|
| `agent-active` | On claim (via `TryClaimAsync`) | Prevents another worker from claiming the same item. Also serves as a secondary guard in `TryClaimWorkItemAsync` which rejects items already tagged `agent-active`. |
| `agent-failed` | When run transitions to `Failed` | Prevents re-pickup after a failure. Item stays visible in `activeState` for human review. |
| `agent-needs-human` | When run transitions to `NeedsHuman` | Prevents re-pickup when the agent requested human input. |

### 5.2 Custom Exclusion Tags

Additional exclusion tags can be configured in `workSource.excludedTags`. For example, adding `"agent-blocked"` lets operators manually block items:

```json
{
  "workSource": {
    "excludedTags": [
      "agent-active",
      "agent-failed",
      "agent-needs-human",
      "agent-blocked"
    ]
  }
}
```

### 5.3 Manual Retry

To retry a failed or needs-human item:

1. Remove the exclusion tag (`agent-failed` or `agent-needs-human`) from the work item in Azure DevOps.
2. Ensure the item is in an eligible state (move it back to `New` if needed).
3. Re-add `agent-ready` if it was removed.
4. The next discovery cycle will pick it up.

---

## 6. Rework Reactivation — Tag-Cleanup Guarantee

When a completed work item is moved back to an eligible state for rework (e.g. the PR was rejected and the item is moved back to `New`), the controller's reactivation path (`ReactivateForReworkAsync`) performs an **atomic single-PATCH** operation that:

1. **Transitions the work item** to the first configured `eligibleStates` (e.g. `New`).
2. **Strips agent lifecycle tags** in the same PATCH:
   - `agent-active` — removes the active-claim marker.
   - `agent-failed` — removes any prior failure marker.
   - `agent-needs-human` — removes any prior escalation marker.
   - `agent-worker:*` — wildcard pattern that strips the concrete `agent-worker:{workerId}` tag (e.g. `agent-worker:live-ado-worker`).
3. **Re-adds `agent-ready`** so the item is immediately eligible for re-pickup.

All of this is carried in a **single ADO PATCH request** with one `If-Match` revision token. This eliminates the stale-revision race where a revision bump between two separate PATCHes would cause the tag-strip to be silently skipped.

### 6.1 Fail-Loud Semantics

The reactivation PATCH uses **fail-loud** semantics for tag-removal-bearing operations:

| HTTP Status | `RemovedTags` present? | Behavior |
|-------------|------------------------|----------|
| `412 Precondition Failed` | Yes (reactivation path) | **Returns `false`** — reactivation fails, cycle is **not** marked reactivated. `FeedbackPollingWorker` logs `ReworkItemReactivationFailed` and retries on the next poll cycle. |
| `412 Precondition Failed` | No (status-only projection) | **Returns `true`** — best-effort, concurrent modification is not fatal for status-only updates. |
| Any other non-success | Yes or No | **Returns `false`** — fails the operation. |

This scoping ensures that:
- **Reactivation failures are never silently swallowed** — a 412 on a tag-strip PATCH means the item was modified concurrently and the cleanup did not apply. The cycle remains pending and is retried on the next poll.
- **Status-only projections remain best-effort** — normal lifecycle state transitions (e.g. `RunLifecycleService.BuildExternalProjection`) are not disrupted by concurrent board edits.

### 6.2 Result Contract

`ReactivateForReworkAsync` returns a `ReworkReactivateResult`:

| Field | Value on Success | Value on Failure |
|-------|-----------------|------------------|
| `Success` | `true` | `false` |
| `FailureReason` | `null` | Diagnostic string (e.g. `[rework_tag_strip_failed] Cannot transition work item to 'New' and strip agent lifecycle tags...`) |

When `Success` is `false`, the controller skips `MarkReactivatedAsync` — the rework cycle stays pending and is retried on the next discovery poll.

### 6.3 Local File Source Alignment

`LocalFileWorkSource.ReactivateForReworkAsync` applies the same tag-cleanup logic:
- Strips `agent-active`, `agent-failed`, `agent-needs-human`, and any tag matching `agent-worker:` prefix.
- Re-adds `agent-ready`.
- Transitions to the first eligible state.

Both ADO and local work sources maintain consistent rework tag-cleanup semantics.

---

## 7. Full Board Item Lifecycle

```
┌─────────────────────────────────────────────────────────────────────┐
│ 1. CREATE                                                           │
│    Human creates a work item in ADO with:                           │
│    - State: New (or other eligible state)                           │
│    - Tags: agent-ready; repo:example-service                        │
│    - Title, Description, Acceptance Criteria                        │
└───────────────────────┬─────────────────────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 2. DISCOVER                                                         │
│    PollingWorker queries ADO via WIQL. Item matches:                │
│    - State IN eligibleStates                                        │
│    - Tags CONTAINS "agent-ready"                                    │
│    - Tags NOT CONTAINS any excludedTag                              │
│    - repo: tag resolves to a configured repository profile          │
└───────────────────────┬─────────────────────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 3. CLAIM                                                            │
│    Controller claims the item:                                      │
│    - Adds tags: agent-active, agent-worker:{workerId}               │
│    - Sets state to activeState (e.g. "Active")                      │
│    - Posts comment: "Agent controller claimed..."                   │
│    - Records lease in internal database                             │
└───────────────────────┬─────────────────────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 4. PROVISION                                                        │
│    Controller creates a run workspace:                              │
│    - Creates environment directory                                  │
│    - Clones repository from repo profile                            │
│    - Writes context files:                                          │
│      • work-item.md (title, description, metadata)                  │
│      • acceptance-criteria.md (from ADO acceptance criteria field)  │
│      • comments.md (discussion history, bounded to MaxComments)     │
│      • controller-run.json (run metadata)                           │
│      • repository.json (repo metadata)                              │
└───────────────────────┬─────────────────────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 5. EXECUTE                                                          │
│    Controller invokes agent runtime (pi-materia):                   │
│    - Runtime receives full context directory                        │
│    - Runtime emits events via POST /runs/{runId}/events             │
│    - Controller tracks: heartbeats, status, branch, PR              │
└───────────────────────┬─────────────────────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 6. RESOLVE (one of four outcomes)                                   │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────┐       │
│  │ A. SUCCESS — PR Opened                                   │       │
│  │    - State → completedState (e.g. "Resolved")            │       │
│  │    - Comment: "Pull request opened: {url}"               │       │
│  └──────────────────────────────────────────────────────────┘       │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────┐       │
│  │ B. SUCCESS — Completed (no PR)                           │       │
│  │    - State → completedState                               │       │
│  │    - Comment: "Run completed: {summary}"                  │       │
│  └──────────────────────────────────────────────────────────┘       │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────┐       │
│  │ C. FAILED                                                │       │
│  │    - State unchanged (stays in activeState)               │       │
│  │    - Tag added: agent-failed                              │       │
│  │    - Comment: "Run failed: {error}"                       │       │
│  └──────────────────────────────────────────────────────────┘       │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────┐       │
│  │ D. NEEDS HUMAN                                           │       │
│  │    - State unchanged (stays in activeState)               │       │
│  │    - Tag added: agent-needs-human                         │       │
│  │    - Comment: "Run requires human input: {summary}"       │       │
│  └──────────────────────────────────────────────────────────┘       │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 8. Clarifying-Comment Behavior on Association Mismatch

When the controller discovers a work item with a `repo:{key}` tag but no matching repository profile, it posts a clarifying comment to the ADO work item:

> Skipped: no repository profile matches the `repo:xxx` tag. Configure a matching repository profile or correct the tag.

This ensures that:

- **Typos are visible**: If someone types `repo:exampl-service` instead of `repo:example-service`, the comment surfaces the issue on the board.
- **Missing profiles are actionable**: The comment tells the operator exactly what to fix.
- **No `repo:` tag is silent**: Items without a `repo:` tag are skipped without a comment — they are simply not eligible for agent processing.

---

## 9. Acceptance Criteria and Comments in Agent Context

### 9.1 Acceptance Criteria

The controller extracts acceptance criteria from ADO work items using this precedence:

1. **Dedicated field**: `Microsoft.VSTS.Common.AcceptanceCriteria` (highest priority).
2. **Markdown checklists**: `[-]` / `[x]` patterns in `System.Description`.
3. **HTML checkbox lists**: ADO rich-text format `<input type="checkbox">` elements in description HTML.

The extracted criteria are written to `context/acceptance-criteria.md` in the agent's runtime workspace.

### 9.2 Discussion Comments

The controller fetches ADO work item thread history (discussion comments) during the `ContextInjected` lifecycle phase and writes them to `context/comments.md`. This is bounded by `workSource.maxComments` (default: 50) to keep context manageable.

---

## 10. Configuration Reference

Complete `workSource` configuration with defaults:

```json
{
  "workSource": {
    "provider": "AzureDevOpsBoards",
    "organizationUrl": "https://dev.azure.com/YOUR_ORG",
    "project": "YOUR_PROJECT",
    "eligibleTags": ["agent-ready"],
    "excludedTags": ["agent-active", "agent-failed", "agent-needs-human"],
    "eligibleStates": ["New", "Approved"],
    "activeState": "Active",
    "completedState": "Resolved",
    "maxComments": 50
  }
}
```

### 10.1 Switching Between Mock and Live Providers

| Provider | When to Use | Notes |
|----------|-------------|-------|
| `AzureDevOpsBoards` | Live ADO integration | Requires `azureDevOps.personalAccessToken` with "Work items: Read & write" scope |
| `LocalFake` | Offline testing | Uses `POST /work-items` to seed work items |
| `LocalFile` | Declarative local testing | Reads work item definitions from `localWork.definitions` in config |

### 10.2 Stale Run Recovery

If a run in `AwaitingResult` state exceeds `agentController.staleTimeoutSeconds` (default: 1800s / 30min) without a heartbeat, the controller transitions it to `NeedsHuman` and posts an `agent-needs-human` tag. This prevents orphaned runs from occupying concurrency slots indefinitely.

---

## 11. Related Documentation

- [Architecture Document](./arch.md) — §3.6 (Work Item Eligibility), §8 (Azure DevOps Boards Integration), §10 (Runtime Event Contract).
- [Runtime Event Contract](./runtime-events.md) — Event types, state transitions, and API contract.
- [Development Guide](./development.md) — Local setup, running tests, and integration harnesses.
- [create-ado-story.sh](./create-ado-story-script.md) — Dev script for creating pre-tagged test stories.
- `appsettings.example.json` — Full configuration reference with examples.
