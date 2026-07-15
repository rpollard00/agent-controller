# Azure DevOps Work Source — Setup Flow and PAT Runtime Requirement

This note documents user-visible behaviors for configuring an Azure DevOps Work Source environment via the web UI.

---

## 1. Declarative Terminal-State Policy

The controller excludes terminal board states from new-work discovery using a **fixed, non-configurable constant** (`BoardTerminalStates`). The excluded states are:

| State |
|-------|
| `Closed` |
| `Removed` |
| `Resolved` |
| `Completed` |

These states are matched **case-insensitively** against ADO board item `State` values. This is a declarative policy — there is no per-environment configuration for terminal states, and no UI editor for selecting completed states.

This constant is the single source of truth for the terminal-state filter. Every consumer of the discovery query uses `BoardTerminalStates.Values` for `ExcludedStates`.

### Rework Reactivation Is Unaffected

The rework reactivation flow (e.g., an overriding `rework-requested` tag or state on a pull request) **takes precedence** over the terminal-state filter. When a completed item is moved back to an eligible state for rework, the controller's `ReactivateForReworkAsync` path strips agent lifecycle tags and re-adds `agent-ready`, allowing the item to be re-picked up regardless of its prior terminal state. See [Board Provisioning §6](./board-provisioning.md#6-rework-reactivation---tag-cleanup-guarantee) for the full reactivation contract.

---

## 2. PAT Is a Runtime Environment Variable — Not Stored by the App

The Personal Access Token (PAT) is **not stored as a secret** by the Agent Controller application. Instead:

- The web UI only stores the **name** of the environment variable that holds the PAT (e.g., `AZURE_DEVOPS_PAT`).
- At runtime, the API host process reads the PAT from that environment variable.
- The named env var **must actually be present** in the host process for polling and other ADO operations to work.

### What This Means

| Scenario | Result |
|----------|--------|
| Env var name is configured but the env var is **not set** in the host process | ADO operations fail with: `"Environment variable '{VAR_NAME}' is not set in the host process."` |
| Env var is set but contains an **invalid/expired PAT** | ADO returns an HTTP error (e.g., 401), surfaced as a connectivity error. |
| Env var is set with a **valid PAT** | ADO operations (polling, claiming, state projection) work correctly. |

### Setting the PAT

Ensure the environment variable is set in the process that runs the Agent Controller API. For example:

```bash
# Linux / macOS
export AZURE_DEVOPS_PAT="your-pat-token-here"
dotnet run --project AgentController.Api

# Windows (PowerShell)
$env:AZURE_DEVOPS_PAT="your-pat-token-here"
dotnet run --project AgentController.Api
```

The PAT value is never sent to or stored by the web UI — only the variable name is persisted.

---

## Related Documentation

- [Board Provisioning](./board-provisioning.md) — Tag recipe, eligibility model, and lifecycle state projection.
- [Architecture Document](./arch.md) — §8 (Azure DevOps Boards Integration).
