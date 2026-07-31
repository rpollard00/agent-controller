# Board Provisioning — Tag Recipe and Eligibility Model

This document describes how agent router discovers, claims, and processes Azure DevOps Boards work items. It covers the tag-based eligibility model, the `repo:{key}` association convention, lifecycle state projection, exclusion tags, and the full board item lifecycle from creation through PR.

---

## 1. Overview

Agent router polls an Azure DevOps Board on a configurable interval. It discovers work items that match an **eligibility model** (tags + states), validates that the item references a known repository via a `repo:{key}` tag, claims the item for exclusive execution, and drives it through a lifecycle that ends in a PR, branch push, completion, failure, or human escalation.

The controller's internal database is the authoritative source of truth. Azure DevOps Boards state and tags are an **external projection** — best-effort and idempotent.

---

## 2. Tag-Based Eligibility Model

Each enabled managed work-source environment defines a `tagPrefix` (default `agent`). The controller derives its board tags from that prefix:

| Derived tag | Purpose |
|---|---|
| `<prefix>-ready` | Ordinary new work. Dispatches the `NewWork` loadout. |
| `<prefix>-ready-rework` | Generated Assistance story for an existing PR. Requires a durable Pending Assistance cycle and dispatches the `Rework` loadout. |
| `<prefix>-active` | Work item is claimed. |
| `<prefix>-failed` | Exclusion marker when projected by lifecycle policy. |
| `<prefix>-needs-human` | Human intervention is required. |
| `<prefix>-worker:<id>` | Identifies the claiming worker. |

### 2.1 Eligibility Query (WIQL)

Default Azure DevOps discovery requires **either** ready marker, excludes controller lifecycle markers, and excludes the fixed terminal states `Closed`, `Removed`, `Resolved`, and `Completed`:

```sql
SELECT [System.Id] FROM WorkItems
WHERE [System.TeamProject] = 'MyProject'
  AND [System.State] NOT IN ('Closed', 'Removed', 'Resolved', 'Completed')
  AND (
       [System.Tags] CONTAINS 'agent-ready'
       OR [System.Tags] CONTAINS 'agent-ready-rework'
  )
  AND [System.Tags] NOT CONTAINS 'agent-active'
  AND [System.Tags] NOT CONTAINS 'agent-failed'
  AND [System.Tags] NOT CONTAINS 'agent-needs-human'
ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.CreatedDate] DESC
```

The ready conditions are a parenthesized OR. A story is not required to carry both markers. The selected managed environment's prefix is used consistently for discovery, claim, release, and lifecycle projection.

### 2.2 Tagging a Board Item for Agent Pickup

For ordinary new work:

1. Put the item in a nonterminal state accepted by the board process.
2. Add `<prefix>-ready` (for example, `agent-ready`).
3. Add `repo:{key}`, where `{key}` matches an enabled managed repository profile (see §3).
4. Remove the selected prefix's managed exclusion tags (`<prefix>-active`, `<prefix>-failed`, and `<prefix>-needs-human`).

Example:

```
agent-ready; repo:example-service
```

Do not manually use `<prefix>-ready-rework` as a shortcut. The Assistance materializer adds it only after creating the linked story and its durable Pending cycle. An orphaned ready-rework item is skipped rather than executed as new work.

---

## 3. Repository Association — `repo:{key}` Tag

The `repo:{key}` tag associates a work item with a specific managed repository profile. The `{key}` portion must match a profile stored by the controller.

### 3.1 How It Works

1. **Discovery**: The controller reads `System.Tags` from each ADO work item and looks for a tag starting with `repo:`. The remainder (e.g. `repo:example-service` → `example-service`) becomes the `RepoKey`.

2. **Validation**: After discovery, the controller resolves the `RepoKey` against managed repository and runtime-environment profiles. Three outcomes are possible:

   | Scenario | Behavior |
   |----------|----------|
   | No `repo:` tag present | Item is **skipped silently** — treated as not-eligible. No comment is posted. |
   | `repo:` tag present but no enabled managed repository/runtime environment resolves | Item is **skipped** and a **clarifying comment** is posted on the ADO work item. This makes typos and incomplete profiles visible on the board. |
   | `repo:` tag matches a profile | Item proceeds through the lifecycle. The matched profile provides `cloneUrl`, `defaultBranch`, etc. |

### 3.2 Managed Repository Profiles

Repository profiles are stored in the controller database and managed from the **Repositories** page or the `/api/webui/repositories` API. Static `repositories` appsettings entries are not loaded. Each repository must reference an enabled managed runtime environment.

For example, create a profile with key `example-service`, clone URL `https://dev.azure.com/org/project/_git/example-service`, default branch `main`, and a `runtimeEnvironmentKey` selected from the managed runtime environments. The `repo:example-service` tag then resolves to that persisted profile.

### 3.3 Repository Reviewer Identities

Reviewer identities are authoritative data on the managed repository profile. They are not read from process-level `feedback` configuration and there is no global fallback. Database upgrades initialize existing profiles with an empty list, so Revival fails closed until each profile's identities are configured.

The Repositories create/edit form is provider-tailored. After selecting the repository-host connection, choose one supported identity kind, enter one value, and select **Add** (or press Enter). Entries are shown individually with **Remove** controls; comma-separated or multiline input is not supported. Changing the provider revalidates the configured entries, and unsupported providers cannot use reviewer identities.

Azure DevOps supports these identity kinds:

| Kind | Value and matching |
|------|-------------------|
| Email / `uniqueName` | Email-like Azure DevOps `uniqueName`; normalized and matched case-insensitively. |
| Identity ID | Azure DevOps identity GUID; normalized and matched case-insensitively. |
| Graph descriptor | Opaque Azure DevOps graph descriptor; matched exactly. |

Feedback author aliases are mapped from all available Azure DevOps `uniqueName`, `id`, and `descriptor` fields. A thread qualifies when any comment alias matches a configured profile identity of the same kind.

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

## 6. Revival Rework — Original-Story Reactivation

This section applies only to the **Revival** request mode (`agent-rework-requested`). After qualifying feedback soaks, the controller's `ReactivateForReworkAsync` path returns the original controller story to eligibility. Assistance (`agent-assistance-requested`) creates a new story instead and never calls this path.

Revival reactivation performs a **GET-then-PATCH** flow:

1. **Tag-read GET**: A `GET /workitems/{id}?api-version=7.1` reads the work item's current `System.Tags` and revision (`rev`). This GET is **load-bearing** — if it returns non-success, the entire reactivation aborts and returns `false` (no state-only PATCH is emitted, no false success).
2. **Combined PATCH**: A single `PATCH /workitems/{id}` carries both the state transition and tag operations, using the **freshly-read `rev` from the GET** as the `If-Match` token (not a possibly-stale revision from the work item reference). This prevents 412 errors from stale revision tokens.

### 6.1 Tag Operations (Always Emitted)

The reactivation tag set is **always included** in the PATCH payload, even when the read-back `existingTags` set is empty or contains none of the target tags. Using the selected environment's prefix, this guarantees:
- `<prefix>-active` — removed (via `RemovedTags`).
- `<prefix>-failed` — removed (via `RemovedTags`).
- `<prefix>-needs-human` — removed (via `RemovedTags`).
- `<prefix>-worker:{id}` — removed (exact match, via `RemovedTags`).
- `<prefix>-worker:*` — wildcard pattern that strips any matching worker tag (via `RemovedTags`).
- `<prefix>-ready` — re-added (via `Tags` add operation).

ADO tolerates `RemovedTags` entries for tags that are not present, so this defensive approach ensures re-pickup eligibility regardless of prior tag state.

### 6.2 Fail-Loud Semantics

The reactivation flow uses **fail-loud** semantics at two levels:

| Failure Point | Condition | Behavior |
|---------------|-----------|----------|
| Tag-read GET | Non-success response | **Returns `false`** — aborts entirely, no PATCH is emitted. No false success. |
| PATCH | `412 Precondition Failed` + `RemovedTags` present | **Returns `false`** — reactivation fails, cycle is **not** marked reactivated. `FeedbackPollingWorker` logs `ReworkItemReactivationFailed` and retries on the next poll cycle. |
| PATCH | `412 Precondition Failed` + status-only (no `RemovedTags`) | **Returns `true`** — best-effort retained for status-only projections. |
| PATCH | Any other non-success | **Returns `false`** — fails the operation. |

This scoping ensures that:
- **Reactivation failures are never silently swallowed** — a GET failure or 412 on a tag-strip PATCH means the cleanup did not apply. The cycle remains pending and is retried on the next poll.
- **Status-only projections remain best-effort** — normal lifecycle state transitions (e.g. `RunLifecycleService.BuildExternalProjection`) are not disrupted by concurrent board edits.

### 6.3 Result Contract

`ReactivateForReworkAsync` returns a `ReworkReactivateResult`:

| Field | Value on Success | Value on Failure |
|-------|-----------------|------------------|
| `Success` | `true` | `false` |
| `FailureReason` | `null` | Diagnostic string (e.g. `[rework_tag_strip_failed] Cannot transition work item to 'New' and strip agent lifecycle tags...`) |

When `Success` is `false`, the controller skips `MarkReactivatedAsync` — the rework cycle stays pending and is retried on the next discovery poll.

### 6.4 Observable Log Markers

The reactivation flow emits structured log markers for production diagnosis:

| Log Marker | When | Key Fields |
|-----------|------|----------|
| `[rework_reactivate_get]` | Tag-read GET succeeds | HTTP status, `System.Tags` presence, parsed `rev`, tag count |
| `[rework_reactivate_get_failed]` | Tag-read GET fails | HTTP status, error body excerpt |
| `[rework_reactivate_patch]` | PATCH submitted | `rev` used for `If-Match`, `RemovedTags` set, `Tags` set, target state |
| `[rework_reactivate_patch_failed]` | PATCH fails | HTTP status code, error body excerpt |

These markers let an operator confirm whether the GET succeeded, whether the tag operations are present in the PATCH payload, and whether ADO accepted the request.

### 6.5 Local File Source Alignment

`LocalFileWorkSource.ReactivateForReworkAsync` applies the same tag-cleanup logic:
- Strips `<prefix>-active`, `<prefix>-failed`, `<prefix>-needs-human`, and any tag matching the `<prefix>-worker:` prefix.
- Re-adds `<prefix>-ready`.
- Transitions to the first eligible state.

Both ADO and local work sources maintain consistent rework tag-cleanup semantics.

### 6.6 Assistance Stories — New Work Item, Existing PR

After an `agent-assistance-requested` PR finishes soaking, the controller creates a fresh User Story with `repo:<key>`, a stable correlation tag, and a direct PR artifact relation. Source stories are related when available, and a parent is inherited only when all source stories share one unambiguous parent. Human-submitted PRs with no linked stories are supported.

The story receives `<prefix>-ready-rework` only after its Pending Assistance cycle is durable. It then follows the normal claim and board-state lifecycle, while the runtime checks out the PR source branch and uses `ExecutionKind.Rework`. Assistance cycle numbers are per canonical PR, not per generated story. See [Pull Request Revival and Assistance Workflows](./pull-request-feedback-workflows.md) for the complete lifecycle and PR-label failure policy.

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
│    - repo: tag resolves to managed repository/runtime profiles      │
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

Azure DevOps connection, work-source environment, and repository profiles are managed through the Web UI/API and persisted in the controller database. The work-source environment supplies `connectionKey`, `project`, `tagPrefix`, `activeState`, and `completedState`; the repository profile supplies the `repo:{key}` identity and points to that environment.

The process-level feedback and loadout settings are:

```jsonc
{
  "feedback": {
    "provider": "AzureDevOpsRepos",
    "enabled": true,
    "pollIntervalSeconds": 60,
    "maxConcurrentPolls": 2,
    "soakMinutes": 5,
    "reworkMarkerTag": "agent-rework-requested",
    "assistanceMarkerTag": "agent-assistance-requested",
    "assistanceInProgressTag": "agent-assistance-in-progress",
    "maxReviewThreadsPerBundle": 50
  },
  "runtime": {
    "loadouts": {
      "NewWork": "ADO-Build-NewWork",
      "Rework": "ADO-Build-Rework"
    }
  }
}
```

PR labels are independent of `tagPrefix`. Assistance label names are required and all three request/progress names must be distinct case-insensitively. Reviewer identities are configured per managed repository profile as described in §3.3; the process-level `feedback` section has no reviewer allowlist. See [Pull Request Revival and Assistance Workflows §7](./pull-request-feedback-workflows.md#7-configuration).

### 10.1 Switching Between Mock and Live Providers

| Provider | When to Use | Notes |
|----------|-------------|-------|
| `AzureDevOpsBoards` | Live ADO integration | The managed connection's PAT needs `Work Items: Read & write`; Assistance also needs `Code: Read & write`. |
| `LocalFake` | Offline testing | Uses `POST /work-items` to seed work items |
| `LocalFile` | Declarative local testing | Reads work item definitions from `localWork.definitions` in config |

### 10.2 Stale Run Recovery

If a run in `AwaitingResult` state exceeds `agentController.staleTimeoutSeconds` (default: 1800s / 30min) without a heartbeat, the controller transitions it to `NeedsHuman` and posts an `agent-needs-human` tag. This prevents orphaned runs from occupying concurrency slots indefinitely.

---

## 11. Related Documentation

- [Boards Setup Flow and PAT Runtime Requirement](./boards-setup.md) — Connect-first flow for web UI setup and PAT-as-env-var runtime model.
- [Architecture Document](./arch.md) — §3.6 (Work Item Eligibility), §8 (Azure DevOps Boards Integration), §10 (Runtime Event Contract).
- [Pull Request Revival and Assistance Workflows](./pull-request-feedback-workflows.md) — Marker semantics, soaking, generated-story lineage, PR labels, permissions, and loadouts.
- [Runtime Event Contract](./runtime-events.md) — Event types, state transitions, and API contract.
- [Development Guide](./development.md) — Local setup, running tests, and integration harnesses.
- [create-ado-story.sh](./create-ado-story-script.md) — Dev script for creating pre-tagged test stories.
- `appsettings.example.json` — Full configuration reference with examples.
