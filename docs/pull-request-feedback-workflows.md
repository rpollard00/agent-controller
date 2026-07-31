# Pull Request Revival and Assistance Workflows

The controller supports two additive ways to continue work on an existing Azure DevOps pull request. Both ultimately run the **Rework** execution loadout against the PR's existing source branch, but they differ in discovery, work-item lineage, and lifecycle ownership.

## 1. Request Markers

| PR label | Request mode | Intended use | Work item used for the run |
|---|---|---|---|
| `agent-rework-requested` | **Revival** | Preserve the original controller-produced PR workflow. | Reactivate the original story associated with the prior controller run. |
| `agent-assistance-requested` | **Assistance** | Continue any active PR in a managed Azure DevOps repository, including a human-submitted PR. | Create a new User Story linked to the existing PR. |

The label names are configurable through `feedback.reworkMarkerTag` and `feedback.assistanceMarkerTag`. They are PR labels, not board work-item tags. Assistance discovery and lifecycle mutations are case-insensitive; operators should still use the configured canonical spelling. The assistance, assistance-in-progress, and revival labels must be distinct.

If a PR has both request labels, **Assistance takes precedence**. The controller suppresses or supersedes an outstanding Revival observation for that PR so one feedback bundle cannot enter both paths.

## 2. Discovery, Filtering, and Soaking

`FeedbackPollingWorker` runs independently from the normal board polling worker.

### Revival discovery

Revival is run-backed. The controller examines PRs reported by eligible prior controller runs and requires the configured revival marker. The request must have at least one qualifying review thread. Reviewer identities come only from the matched managed repository profile; an empty profile therefore fails closed for Revival.

### Assistance discovery

Assistance is repository-wide. On each feedback poll the controller enumerates active PRs from **every managed repository** that has project/repository coordinates and an enabled Azure DevOps connection. It does not limit discovery to PRs created by the controller. Repository profiles are deduplicated by their remote identity, and a failure in one repository does not stop discovery in the others or the established Revival path.

This allows a human-submitted PR to request autonomous cleanup. The PR does not need an originating controller run or a linked story, but it must belong to a managed repository and expose a canonical repository/PR identity, source and target branches, source commit, and URL.

### Thread inclusion and zero-comment requests

For either mode, included review threads must:

1. still have `Active` status;
2. contain at least one comment whose typed author identity matches an identity configured on the PR's managed repository profile; and
3. contain non-whitespace comment content.

Matching is provider-specific and considers all aliases returned for the comment author. Full reply chains for surviving threads are retained. Assistance differs in one important way: the marker itself is a valid request. If there are no comments, no unresolved threads, no matching reviewer comments, or all comments are filtered out, Assistance still creates an empty feedback bundle. The resulting story directs the agent to inspect and clean up the existing PR.

The first Assistance observation starts the soak timer. A changed thread bundle supersedes the prior observation, and a genuinely newer qualifying comment advances the last-feedback timestamp and resets the quiet period. Materialization begins only after `feedback.soakMinutes` has elapsed without newer qualifying feedback. Soak and correlation state are persisted across restarts.

## 3. Revival Lifecycle

Revival preserves the original-story behavior:

1. A controller-produced PR carries `agent-rework-requested` and qualifying unresolved feedback.
2. After soaking, the controller creates a Pending Revival cycle associated with the prior run and original work item.
3. The controller reactivates that work item: it moves it to an eligible state, removes managed active/failed/needs-human/worker tags, and restores `<prefix>-ready`.
4. The board worker claims the original item, checks out the existing PR branch, injects rework context, and dispatches `ExecutionKind.Rework`.
5. Revival cycle numbers remain scoped to the original work item.

Revival requires the originating run, original work item, PR URL, branch, and commit metadata. It is retained for compatibility; Assistance does not route through `ReactivateForReworkAsync`.

## 4. Assistance Story Materialization

After an Assistance request soaks, the controller:

1. allocates the next Assistance cycle number for the **canonical PR**, regardless of which assistance story represented an earlier cycle;
2. renders a bounded title and an HTML-safe description that explicitly says to continue the existing PR and not open another;
3. includes repository, source/target branch, observed source commit, cycle number, and qualifying unresolved threads in file/line order with thread IDs and full reply chains;
4. creates a fresh User Story with `repo:<repository-key>` and a stable `agent-assistance-correlation:<id>` tag;
5. persists the story receipt and a Pending Assistance cycle;
6. only after the cycle is durable, publishes the story with `<prefix>-ready-rework`; and
7. posts an idempotent queued comment on the PR linking to the generated story.

The correlation tag and persisted receipt let the controller reconcile retries after a crash without creating duplicate stories. A story that cannot be published with `<prefix>-ready-rework` is not marked materialized, so publication can be retried.

`<prefix>` comes from the managed work-source environment's `tagPrefix` (default `agent`). The board worker discovers either `<prefix>-ready` or `<prefix>-ready-rework`; these are OR alternatives, not tags that must both be present. A ready-rework story without a matching Pending Assistance cycle is skipped with operator guidance instead of being executed as new work.

### Story relationships

Relationships are resolved in this order:

- If the PR came from a controller run and its originating work item can be resolved, that item is the source story.
- Otherwise, all PR-linked work items of type `User Story` are used as source stories.
- Every source story receives a `Related` relation from the assistance story.
- A parent relation is copied only when every source story has exactly one parent and all of those parents are the same.
- The assistance story always receives a direct Azure DevOps PR artifact link.

A human PR with no linked work items is valid: its assistance story simply has the direct PR artifact link and no source-story or parent relation.

## 5. Dispatch and Loadouts

Board state, claiming, lifecycle events, retries, and terminal projection for a generated assistance story use the same work-item lifecycle as other stories. Execution intent is different:

| Eligibility/context | `ExecutionKind` | Default loadout | Branch/PR behavior |
|---|---|---|---|
| `<prefix>-ready` with no rework cycle | `NewWork` | `ADO-Build-NewWork` | Start from the managed repository default branch; the runtime may create a branch and PR. |
| Pending Revival or Assistance cycle | `Rework` | `ADO-Build-Rework` | Check out the existing PR source branch, update it, and do not open another PR. |

Configure these independently in `runtime.loadouts`. Do not point `Rework` at a loadout that creates a new PR. The durable cycle is consumed only after rework context has been injected; persisted claimed runs and retry runs recover the same PR context after restart.

## 6. Assistance PR Labels and Failure Handling

`feedback.assistanceInProgressTag` defaults to `agent-assistance-in-progress`. Label mutations are case-insensitive and idempotent.

| Milestone | PR label behavior |
|---|---|
| Request observed, soaking, or story queued | Keep `agent-assistance-requested`; do not add in-progress yet. |
| `runtime.accepted` | Add `agent-assistance-in-progress`. |
| Successful branch push or completion | Remove `agent-assistance-requested` and `agent-assistance-in-progress`; also remove a conflicting `agent-rework-requested`. |
| Failed, needs-human, or cancelled | Remove only `agent-assistance-in-progress`; retain `agent-assistance-requested`. |

Retaining the request marker on unsuccessful outcomes keeps the PR visibly open for retry. Controller retry lineage remains attached to the same Assistance cycle; newer qualifying feedback can form a later, per-PR cycle. The generated story follows normal board failure/needs-human handling and remains the auditable unit of work.

External projection failures are recorded as warnings and are safe to retry. Managed-PR discovery failures do not disable Revival. Materialization is restart-safe, and an orphaned ready-rework marker is never allowed to fall through to `NewWork`.

## 7. Configuration

```jsonc
{
  "workSource": {
    // Managed work-source profiles may override this. Produces agent-ready,
    // agent-ready-rework, agent-active, and other lifecycle tags.
    "tagPrefix": "agent"
  },
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

Reviewer identities are configured on each managed repository profile, not under process-level `feedback` settings. The Repositories editor loads the selected provider's supported identity kinds and accepts one normalized identity at a time; use **Add** (or Enter) for each entry and **Remove** to delete one. Upgraded profiles start with an empty reviewer identity list and have no global fallback, so configure the profile before expecting Revival feedback to qualify.

For Azure DevOps, configure any of the provider's supported identity forms: email / `uniqueName`, identity ID (GUID), or graph descriptor. The feedback source maps all three aliases from each comment author, and matching is performed only between entries of the same kind. Email and identity ID values are normalized case-insensitively; graph descriptors are matched exactly.

## 8. Azure DevOps Permissions

The PAT or service identity referenced by each managed Azure DevOps connection needs both:

- **Code: Read & write** — enumerate active PRs; read labels, threads, linked work items, branches, and PR identity; add/remove PR labels; and post lifecycle comments.
- **Work Items: Read & write** — query and reactivate source stories; create and tag assistance stories; add relations and PR artifact links; claim stories; update state/tags; and post work-item history/comments.

PAT scopes do not replace project/repository authorization. The identity must also be allowed to read each managed repository and contribute to the existing PR source branch. If the runtime pushes with SSH or a separate Git credential, that credential needs the corresponding repository permissions as well.

## Related Documentation

- [Architecture](./arch.md)
- [Board Provisioning](./board-provisioning.md)
- [Live ADO Run Procedure](./live-ado-run-procedure.md)
- [Runtime Event Contract](./runtime-events.md)
- `appsettings.example.json`
