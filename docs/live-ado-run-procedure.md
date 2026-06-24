# Live ADO Run Procedure

This document covers how to run agent router against a live Azure DevOps instance for end-to-end testing of the board → pi → PR lifecycle.

---

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `pi` on PATH with pi-materia installed and a configured model + API key
- `git` and `jj` on PATH
- An Azure DevOps organization with a project and board configured

## 2. Environment Secrets

### 2.1 Azure DevOps Personal Access Token (PAT)

The controller authenticates to ADO via a PAT. Create one at:

```
https://dev.azure.com/YOUR_ORG/_usersSettings/tokens
```

**Required scopes:**

- **Work items: Read & write** — for discovering, claiming, updating state/tags, and posting comments

### 2.2 Setting the PAT

Copy `.env.example` to `.env` and set the token:

```bash
cp .env.example .env
```

Edit `.env`:

```env
AZURE_DEVOPS_PAT=your_actual_pat_here
```

The `.env` file is git-ignored. The controller reads the PAT via the `ENV:AZURE_DEVOPS_PAT` prefix in `appsettings.Live.ADO.json`.

> **Note:** The `ENV:` prefix is resolved at runtime. If the environment variable is missing or empty, the controller will fail to start with a clear error message.

## 3. Configuration Profiles

The controller supports multiple configuration profiles via ASP.NET Core's configuration layer. Profile files are layered on top of `appsettings.json` using the `DOTNET_ENVIRONMENT` variable.

| Profile File | When Applied | Purpose |
|-------------|-------------|---------|
| `appsettings.json` | Always | Base configuration (LocalFake work source, NoOp runtime) |
| `appsettings.Development.json` | `DOTNET_ENVIRONMENT=Development` | Development overrides (logging) |
| `appsettings.Live.ADO.json` | `DOTNET_ENVIRONMENT=Live.ADO` | Live ADO integration with real project/board references |

### 3.1 Switching Between Mock and Live ADO Providers

**Mock mode (default):**

```bash
# No environment variable needed — uses appsettings.json defaults
dotnet run --project src/AgentController.Api
```

This uses `LocalFake` work source and `NoOp` runtime — fully offline, no ADO needed.

**Live ADO mode:**

```bash
export DOTNET_ENVIRONMENT=Live.ADO
export AZURE_DEVOPS_PAT=your_pat_here
dotnet run --project src/AgentController.Api
```

This loads `appsettings.Live.ADO.json` which configures:
- `AzureDevOpsBoards` work source with real org/project
- `LocalGit` source control provider
- `LocalWorkspace` environment provider
- `MockPiMateria` runtime (swap to `PiMateria` for real agent execution, see §4)

### 3.2 Switching Runtime from Mock to Real Pi-Materia

To run with a real `pi` process instead of the mock runtime, edit `appsettings.Live.ADO.json` or create a local override (`appsettings.Live.ADO.local.json` which is git-ignored):

```json
{
  "runtime": {
    "provider": "PiMateria",
    "controllerBaseUrl": "http://localhost:5103",
    "piExecutablePath": "pi",
    "defaultMateriaLoadout": "Wedge",
    "heartbeatsynthIntervalSeconds": 30
  }
}
```

> **Note:** `PiMateria` requires the controller's HTTP origin in `controllerBaseUrl` so pi-materia can POST runtime events back. The default port is `5103` but verify with `GET /` on the running controller.

## 4. Polling Cadence

The polling interval is configured via `agentController.pollIntervalSeconds` (default: 30 seconds in the Live.ADO profile).

| Scenario | Recommended Interval |
|----------|---------------------|
| Active development / testing | 10–15 seconds |
| Normal operation | 30–60 seconds |
| Low-traffic / cost-conscious | 120–300 seconds |

To change the interval, update `appsettings.Live.ADO.json`:

```json
{
  "agentController": {
    "pollIntervalSeconds": 15
  }
}
```

> **Warning:** Very short intervals (< 10s) may cause rate limiting on the ADO REST API. The controller includes resilience policies, but respect ADO's rate limits.

## 5. Running the Controller

### 5.1 Start the Controller

```bash
# Set environment
export DOTNET_ENVIRONMENT=Live.ADO
export AZURE_DEVOPS_PAT=your_pat_here

# Run
dotnet run --project src/AgentController.Api
```

### 5.2 Verify Startup

Check the logs for:

1. **Configuration loaded**: Look for the work source provider name.
2. **Worker enabled**: `Polling worker started` (not `Polling worker is disabled`).
3. **First poll cycle**: WIQL query execution and work item discovery.

### 5.3 Health Check

```bash
curl http://localhost:5103/health
```

### 5.4 ADO Diagnostic Endpoint

Before running the full poll loop, verify ADO connectivity:

```bash
curl http://localhost:5103/api/azure-devops/diagnostic
```

This returns configuration validation results and a test ADO API call.

## 6. End-to-End Testing Workflow

### 6.1 Create a Test Story in ADO

**Option A: Use the dev script (recommended)**

```bash
./dev/create-ado-story.sh --title "Add greeting endpoint"
```

This creates a User Story pre-tagged with `agent-ready; repo:agent-router` and default acceptance criteria. See [create-ado-story.sh docs](./create-ado-story-script.md) for full options.

**Option B: Create manually in the ADO UI**

1. Navigate to your ADO project board.
2. Create a new **User Story** (or Bug/Task).
3. Set state to `New` (or whatever is in `eligibleStates`).
4. Add tags: `agent-ready; repo:agent-router`
5. Add a description and acceptance criteria.

### 6.2 Observe the Lifecycle

Watch the controller logs for these phases:

```
[Information] PollingWorker — Starting discovery cycle
[Debug] AzureDevOpsBoardsClient — Executing WIQL query
[Information] PollingWorker — Discovered work item {id}
[Information] PollingWorker — Validating repo key for work item {id}
[Information] PollingWorker — Claiming work item {id}
[Information] RunLifecycleService — Run {runId} created for work item {id}
[Information] PollingWorker — Provisioning environment for run {runId}
[Information] PollingWorker — Invoking agent runtime for run {runId}
[Information] RunLifecycleService — Run {runId} transitioned to AwaitingResult
```

### 6.3 Monitor Run Progress

```bash
# List all runs
curl http://localhost:5103/api/runs

# Get a specific run
curl http://localhost:5103/api/runs/{runId}
```

### 6.4 Check ADO Board State

After the controller processes an item, verify on the ADO board:

- State changed from `New` → `In Progress` (on claim)
- Tags include `agent-active` and `agent-worker:live-ado-worker`
- Comments show the lifecycle progression

### 6.5 Complete Lifecycle (with PiMateria runtime)

When using the real `PiMateria` runtime:

1. Controller discovers and claims the work item.
2. Controller provisions the run workspace and clones the repo.
3. Controller launches `pi` with the configured materia loadout.
4. Pi-materia processes the work item and emits events back to the controller.
5. Controller updates ADO board state to `Done` (or `In Progress` + `agent-failed` on error).
6. PR is opened (if the materia produces one).

## 7. Repository Profiles

The `repositories` section in `appsettings.Live.ADO.json` defines which repos the controller can work with. Each profile is referenced by a `repo:{key}` tag on ADO work items.

```json
{
  "repositories": {
    "agent-router": {
      "cloneUrl": "/home/reese/projects/agent_router",
      "defaultBranch": "main",
      "environmentProfile": "local-default",
      "runtimeProfile": "pi-materia-default"
    }
  }
}
```

To add a new repo:

1. Add a profile entry with a unique key.
2. Tag ADO work items with `repo:{key}` (e.g., `repo:agent-router`).

The controller will post a clarifying comment on any work item whose `repo:` tag doesn't match a configured profile.

## 8. Troubleshooting

### Controller won't start

- Verify `AZURE_DEVOPS_PAT` is set and non-empty.
- Check `DOTNET_ENVIRONMENT=Live.ADO` is set.
- Run the diagnostic endpoint: `GET /api/azure-devops/diagnostic`.

### No work items discovered

- Verify the work item is in an `eligibleStates` state.
- Verify `agent-ready` tag is present.
- Verify `repo:{key}` matches a configured repository profile.
- Check excluded tags are not present (`agent-active`, `agent-failed`, `agent-needs-human`).

### Work item discovered but skipped

- Check ADO work item comments for clarifying messages (e.g., repo key mismatch).
- Check controller logs for `ValidateRepoKeyAsync` output.

### Runtime events not received (PiMateria mode)

- Verify `controllerBaseUrl` in runtime config matches the controller's actual URL.
- Check `pi` is on PATH and pi-materia is installed.
- Verify the configured model/API key is valid.

### Stale runs

- Runs in `AwaitingResult` without a heartbeat for `staleTimeoutSeconds` (default: 1800s) are marked `NeedsHuman` with `agent-needs-human` tag.
- To retry: remove `agent-needs-human` tag, set state back to an eligible state, and re-add `agent-ready`.

## 9. Stopping the Controller

Press `Ctrl+C` to stop. The controller handles graceful shutdown:

- In-progress runs are marked as cancelled.
- ADO board items in `activeState` with `agent-active` tag may need manual cleanup (remove `agent-active` tag to make them eligible again).

---

## 10. Related Documentation

- [Board Provisioning](./board-provisioning.md) — Tag recipe, eligibility model, and lifecycle diagram.
- [Architecture Document](./arch.md) — System design and integration points.
- [Development Guide](./development.md) — Local setup and testing.
- `appsettings.example.json` — Full configuration reference with examples.
