# Dev Script: `create-ado-story.sh`

A helper script for creating Azure DevOps work items pre-configured for agent router end-to-end testing. It creates a story with the `agent-ready` eligibility tag, a `repo:{key}` repository association tag, and acceptance criteria — so the full lifecycle (discovery → pi job → PR) can be exercised without manual board setup.

## Location

```
dev/create-ado-story.sh
```

## Prerequisites

- `curl` on PATH
- `python3` on PATH (for JSON parsing in the response)
- `AZURE_DEVOPS_PAT` environment variable set (Work items: Read & write scope)

## Usage

```bash
./dev/create-ado-story.sh --title "Add greeting endpoint"
```

### Options

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--title` | `-t` | *(required)* | Work item title |
| `--repo-key` | `-r` | `agent-router` | Repository profile key for the `repo:` tag |
| `--project` | `-p` | `ADO_PROJECT` env or `Projecto` | ADO project name |
| `--org` | `-o` | `ADO_ORG` env or `rpollard0630` | ADO organization name |
| `--work-item-type` | `-w` | `User Story` | ADO work item type (e.g. `Bug`, `Task`) |
| `--description` | `-d` | auto-generated | Description text |
| `--acceptance` | `-a` | `Verify the feature works as described` | Acceptance criteria (semicolon-separated) |
| `--state` | `-s` | `New` | Initial board state |
| `--dry-run` | — | `false` | Print the API call without executing |

### Examples

**Minimal — create a test story with defaults:**

```bash
./dev/create-ado-story.sh --title "Add greeting endpoint"
```

**Custom repo key and acceptance criteria:**

```bash
./dev/create-ado-story.sh \
  --title "Implement user authentication" \
  --repo-key my-service \
  --acceptance "Handles valid tokens; Rejects expired tokens; Logs auth failures"
```

**Create a bug in a different project:**

```bash
./dev/create-ado-story.sh \
  --title "Fix null reference in parser" \
  --work-item-type Bug \
  --project AnotherProject \
  --repo-key parser-lib
```

**Dry run to inspect the API call:**

```bash
./dev/create-ado-story.sh --title "Test story" --dry-run
```

## What the Script Does

1. Validates that `--title` is provided and `AZURE_DEVOPS_PAT` is set.
2. Builds a JSON-Patch body with these fields:
   - `System.Title` — the story title
   - `System.Description` — provided or auto-generated
   - `System.Tags` — `agent-ready; repo:{repo-key}`
   - `System.State` — initial state (default: `New`)
   - `Microsoft.VSTS.Common.AcceptanceCriteria` — semicolon-separated criteria as a JSON object
3. POSTs to the ADO Work Items REST API (`_apis/wit/workitems/{type}`).
4. On success, prints the work item ID and URL.

## Tags Applied

The script always adds two tags:

| Tag | Purpose |
|-----|---------|
| `agent-ready` | Eligibility tag — signals the controller to pick up this item |
| `repo:{key}` | Repository association — maps the item to a configured repository profile |

These match the [board provisioning model](./board-provisioning.md) so the item is immediately eligible for discovery by the agent router polling cycle.

## End-to-End Testing Workflow

1. **Start the controller** (in one terminal):
   ```bash
   export DOTNET_ENVIRONMENT=Live.ADO
   export AZURE_DEVOPS_PAT=your_pat_here
   dotnet run --project src/AgentController.Api
   ```

2. **Create a test story** (in another terminal):
   ```bash
   ./dev/create-ado-story.sh --title "Add feature X"
   ```

3. **Watch the controller logs** for discovery, claim, provisioning, and execution.

4. **Check the ADO board** at the printed URL to verify state transitions and comments.

5. **Repeat** — create more stories to test different scenarios:
   ```bash
   ./dev/create-ado-story.sh --title "Add feature Y" --repo-key agent-router
   ./dev/create-ado-story.sh --title "Fix bug Z" --work-item-type Bug
   ```

## Environment Variables

| Variable | Purpose |
|----------|---------|
| `AZURE_DEVOPS_PAT` | **Required.** Personal Access Token with "Work items: Read & write" scope. |
| `ADO_ORG` | Default ADO organization name (override with `--org`). |
| `ADO_PROJECT` | Default ADO project name (override with `--project`). |

## Related Documentation

- [Board Provisioning](./board-provisioning.md) — Tag recipe, eligibility model, and lifecycle diagram.
- [Live ADO Run Procedure](./live-ado-run-procedure.md) — Full controller startup and testing guide.
