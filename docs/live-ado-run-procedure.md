# Live ADO Run Procedure

This document covers how to run agent router against a live Azure DevOps instance for end-to-end testing of the board → pi → PR lifecycle.

---

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `pi` on PATH with pi-materia installed and a configured model + API key
- `git` and `jj` on PATH
- An Azure DevOps organization with a project and board configured

### 1.1 SSH Key Setup (for SSH clone transport)

The live-ado worker uses SSH transport (`git@ssh.dev.azure.com:...`) to clone repositories. You need:

1. **An SSH key** in one of these locations:
   - `~/.ssh/id_ed25519` (recommended)
   - `~/.ssh/id_rsa`
   - `~/.ssh/id_ecdsa`

   Generate one if needed:
   ```bash
   ssh-keygen -t ed25519 -C "agent-router@$(hostname)"
   ```

2. **The public key added to ADO**: Go to your Azure DevOps organization settings → Personal settings → SSH Public Keys, and add your `~/.ssh/id_ed25519.pub` (or equivalent) key.

3. **`ssh` command on PATH**: The preflight check verifies `ssh` is available.

> The clone preflight (see §5) validates these prerequisites before the worker claims any work item. If the SSH key is missing or the remote is unreachable, the item is skipped with a clear log message and an ADO comment — no claim is pinned.

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
[Debug] LocalGitSourceControlProvider — Clone preflight passed for candidate {id} (transport=Ssh).
[Information] PollingWorker — Claiming work item {id}
[lifecycle] Claim acquired — workerId=live-ado-worker, workItemId={id}, title='...'.
[Information] RunLifecycleService — Run {runId} created for work item {id}
[Information] PollingWorker — Provisioning environment for run {runId}
[lifecycle] Source-control clone starting — runId={runId}, repoKey=test1, transport=Ssh.
[lifecycle] Workspace ready — runId={runId}, envRoot='...', repoPath='...'.
[lifecycle] RPC dispatch to autonomous runtime starting — runId={runId}, workItemId={id}, runtimeType=PiMateriaRuntime.
[Information] RunLifecycleService — Run {runId} transitioned to AwaitingResult
```

#### 6.2.1 Lifecycle Log Markers

Structured lifecycle logs use the `[lifecycle]` prefix and are emitted at `Information` level between the Claimed and AgentStarting states:

| Log Marker | When | Key Fields |
|-----------|------|----------|
| `[lifecycle] Claim acquired` | After ADO claim succeeds | `workerId`, `workItemId`, `title` |
| `[lifecycle] Source-control clone starting` | Before git clone | `runId`, `repoKey`, `transport` |
| `[lifecycle] Workspace ready` | After clone + env provision | `runId`, `envRoot`, `repoPath` |
| `[lifecycle] RPC dispatch ... starting` | Before `pi` process launch | `runId`, `workItemId`, `runtimeType` |
| `[lifecycle] RPC dispatch ... FAILED` | If runtime handoff fails | `runId`, `workItemId`, `reason` |

#### 6.2.2 RPC Logs

When using the `PiMateria` runtime, RPC calls to the autonomous Elene runtime are logged at `Debug` level:

```
[rpc] Prompt sent to pi — runId={runId}, promptId={promptId}, message='...'.
[rpc] Response from pi — runId={runId}, command={command}, success={success}.
```

These are configured in `appsettings.json` under `Logging.LogLevel`:
```json
"AgentController.Infrastructure.PiMateriaRuntime": "Debug"
```

If you don't see any `[rpc]` logs, the RPC dispatch to `pi` never started — check for `[lifecycle]` errors or clone failures earlier in the log.

#### 6.2.3 Preflight Check Logs

Before claiming any work item, the worker runs a clone preflight check. Logs:

| Log Level | Message | Meaning |
|-----------|---------|--------|
| `Debug` | `Clone preflight passed for candidate {id} (transport=Ssh).` | All checks passed, claim will proceed |
| `Warning` | `Skipping candidate {id} ...: clone preflight failed (transport=Ssh): {Reason}` | Preflight failed, item skipped (no claim pinned) |

The preflight validates:
1. Clone URL is present and parseable
2. Transport prerequisites exist (SSH key in `~/.ssh/`, `ssh` command on PATH)
3. A non-interactive `git ls-remote` probe succeeds (with `GIT_TERMINAL_PROMPT=0` and `BatchMode=yes`)

On preflight failure, a clarifying comment is posted on the ADO work item.

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

1. Controller discovers and claims the work item (preflight check runs first).
2. Controller provisions the run workspace and clones the repo (using configured transport).
3. Controller launches `pi` with the configured materia loadout (RPC dispatch logged).
4. Pi-materia processes the work item and emits events back to the controller.
5. Controller updates ADO board state to `Done` on success.
6. PR is opened (if the materia produces one).

**On clone or setup failure** (before the agent starts):
- The worker strips `agent-active` and `agent-worker:*` tags from the ADO work item.
- The work item is reverted to an eligible state (configured in `workSource.activeState` → back to original state).
- The half-built workspace is destroyed.
- The run record transitions to `Failed`.
- The concurrency/lease slot is freed immediately.
- **No `agent-failed` tag is left behind** — a bad runtime environment should not dirty the external ADO record.
- The item is immediately retryable once the underlying issue is fixed.

## 7. Repository Profiles

The `repositories` section in `appsettings.json` (or an environment-specific override) defines which repos the controller can work with. Each profile is referenced by a `repo:{key}` tag on ADO work items.

### 7.1 Profile Options

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `cloneUrl` | string | Yes | Remote URL or local path. Must be non-empty (validated at startup). |
| `transport` | string | No | Clone transport: `"Ssh"`, `"HttpsPat"`, or `"Local"`. When omitted, inferred from the URL pattern (`git@...` → Ssh, `https://...` → HttpsPat, local path → Local). |
| `defaultBranch` | string | No | Branch to check out. Defaults to `"main"`. |
| `environmentProfile` | string | No | Environment profile name for runs targeting this repo. |
| `runtimeProfile` | string | No | Runtime profile name for runs targeting this repo. |
| `allowedPaths` | string[] | No | Paths the agent may modify. Empty array means no restrictions. |

### 7.2 SSH Transport Example (live-ado)

```json
{
  "repositories": {
    "test1": {
      "cloneUrl": "git@ssh.dev.azure.com:v3/rpollard0630/Projecto/test1",
      "transport": "Ssh",
      "defaultBranch": "main",
      "environmentProfile": "local-default",
      "runtimeProfile": "pi-materia-default"
    }
  }
}
```

### 7.3 HTTPS+PAT Transport Example

```json
{
  "repositories": {
    "my-repo": {
      "cloneUrl": "https://<pat>@dev.azure.com/org/project/_git/repo",
      "transport": "HttpsPat",
      "defaultBranch": "main"
    }
  }
}
```

### 7.4 Local Path Example

```json
{
  "repositories": {
    "local-test": {
      "cloneUrl": "/home/reese/projects/my-repo",
      "transport": "Local",
      "defaultBranch": "main"
    }
  }
}
```

### 7.5 Adding a New Repository Profile

1. Add a profile entry with a unique key to `appsettings.json` under `repositories`.
2. Tag ADO work items with `repo:{key}` (e.g., `repo:test1`).
3. The controller validates the `cloneUrl` at startup — a missing or empty URL fails fast with a clear error.

The controller will post a clarifying comment on any work item whose `repo:` tag doesn't match a configured profile.

## 8. Troubleshooting

### Controller won't start

- Verify `AZURE_DEVOPS_PAT` is set and non-empty.
- Check `DOTNET_ENVIRONMENT=Live.ADO` is set.
- Run the diagnostic endpoint: `GET /api/azure-devops/diagnostic`.
- Check for config validation errors — malformed or missing `cloneUrl` in repository profiles will fail fast at startup with a clear message.

### Work item discovered but skipped (preflight failure)

The clone preflight runs before any claim is pinned. Check logs for:

```
[Warning] Skipping candidate {id} (...): clone preflight failed (transport=Ssh): {Reason}
```

Common preflight failures:
- **No SSH key found**: Generate a key with `ssh-keygen` and ensure it's in `~/.ssh/` (e.g., `id_ed25519`).
- **SSH command not found**: Ensure `ssh` is on PATH.
- **`git ls-remote` failed**: The remote URL is unreachable or the SSH key isn't authorized. Verify the key is added to ADO.
- **Clone URL missing**: The repository profile's `cloneUrl` is empty or malformed.

The preflight failure reason is also posted as a comment on the ADO work item for remote sources.

### No work items discovered

- Verify the work item is in an `eligibleStates` state.
- Verify `agent-ready` tag is present.
- Verify `repo:{key}` matches a configured repository profile.
- Check excluded tags are not present (`agent-active`, `agent-failed`, `agent-needs-human`).

### Work item claimed but no execution (silent stall)

This was the root cause of the original issue: an interactive SSH prompt was blocking the clone indefinitely. This is now prevented by:

1. **`GIT_TERMINAL_PROMPT=0`** — prevents git from opening a terminal for credentials.
2. **`GIT_SSH_COMMAND="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new"`** — forces SSH into non-interactive batch mode.
3. **Clone preflight** — validates reachability before claiming.
4. **Clone-failure release path** — if a clone fails after a claim, the worker strips `agent-active` and `agent-worker:*` tags, destroys the workspace, transitions the run to `Failed`, and frees the concurrency slot immediately (no `agent-failed` tag is left behind).

If you still see a stall:
- Check for `[lifecycle]` logs — if you see `clone starting` but nothing after, the clone is hanging.
- Check for `[rpc]` logs — if you see no `[rpc]` logs, the RPC dispatch to `pi` never started (clone or environment provisioning failed).
- Verify there are no interactive SSH prompts by running the clone manually: `GIT_TERMINAL_PROMPT=0 GIT_SSH_COMMAND="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new" git clone <url>`.

### Runtime events not received (PiMateria mode)

- Verify `controllerBaseUrl` in runtime config matches the controller's actual URL.
- Check `pi` is on PATH and pi-materia is installed.
- Verify the configured model/API key is valid.
- Check for `[rpc]` logs at Debug level — `Prompt sent to pi` and `Response from pi` confirm RPC is working.

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
