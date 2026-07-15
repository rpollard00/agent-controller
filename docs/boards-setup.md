# Azure DevOps Work Source — Setup Flow and PAT Runtime Requirement

This note documents two user-visible behaviors for configuring an Azure DevOps Work Source environment via the web UI.

---

## 1. Connect-First Flow — Board Policy Requires a Saved Environment

When creating a new Azure DevOps Work Source environment, the **board policy section** (board states selection, tag prefix, active/completed state) is **not available until the environment is saved**.

**Create flow:**

1. Enter the connection details: **Organization URL**, **Project**, and **PAT environment variable name**.
2. Save the environment. This persists the connection configuration.
3. After save, the board policy section becomes active and can query ADO for available board states.

The board states query requires a valid, persisted connection (URL + Project + PAT env var name) to call the ADO Process API. The UI prevents querying ADO before these values are saved.

**Edit flow (after save):**

- The board policy section is fully visible and interactive.
- Board state suggestions are fetched from ADO and displayed grouped by work item type (Bug, Task, User Story, etc.).

---

## 2. PAT Is a Runtime Environment Variable — Not Stored by the App

The Personal Access Token (PAT) is **not stored as a secret** by the Agent Controller application. Instead:

- The web UI only stores the **name** of the environment variable that holds the PAT (e.g., `AZURE_DEVOPS_PAT`).
- At runtime, the API host process reads the PAT from that environment variable.
- The named env var **must actually be present** in the host process for board-state queries and other ADO operations to work.

### What This Means

| Scenario | Result |
|----------|--------|
| Env var name is configured but the env var is **not set** in the host process | Board-state query returns a connectivity error: `"Environment variable '{VAR_NAME}' is not set in the host process."` |
| Env var is set but contains an **invalid/expired PAT** | ADO returns an HTTP error (e.g., 401), surfaced as a connectivity error. |
| Env var is set with a **valid PAT** | Board states are fetched successfully via the ADO Process API. |

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
