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

## 2. PAT Is Stored as a Named, Versioned Secret

The Personal Access Token (PAT) is stored as a **named, versioned secret encrypted at rest** in the Agent Controller database. Each secret has:

- A unique **name** used to reference it from work source and repository host configurations.
- One or more **versions**, each carrying an encrypted copy of the PAT value.
- Write-only value entry — the plaintext value is never displayed after storage.

### Configuring the PAT Secret

1. Navigate to the **Secrets** management page in the web UI.
2. Create a new secret with a descriptive name (e.g., `azure-devops-pat`) and enter the PAT value.
3. In the work source environment form, select the secret by name from the combobox picker.

The secret value is encrypted at rest using envelope encryption (AES-256-GCM) and is resolved at runtime by the controller when making ADO API calls.

### Rotating the PAT

To rotate a PAT without updating every configuration that references it:

1. Navigate to the secret's detail page.
2. Create a **new version** with the updated PAT value.
3. All configurations referencing the secret by name (without a pinned version) automatically resolve to the latest version.

### What This Means

| Scenario | Result |
|----------|--------|
| Secret is configured with a **valid PAT** | ADO operations (polling, claiming, state projection) work correctly. |
| Secret holds an **invalid/expired PAT** | ADO returns an HTTP error (e.g., 401), surfaced as a connectivity error. |
| Secret name is configured but **no secret exists** with that name | ADO operations fail with a secret resolution error. |

### Key Encryption Key (KEK) Setup

The secret store requires a Key Encryption Key (KEK) to encrypt/decrypt secret values. The KEK can be provisioned via:

- A **user-secrets file** pointed to by `AGENT_CONTROLLER_SECRET_KEK_FILE_PATH` environment variable (the file must contain exactly 32 bytes of key material).
- **systemd-creds** on supported Linux systems.

If the KEK is missing or invalid at startup, the controller fails fast with a clear error message indicating the required configuration.

See the [KEK Setup Guide](./kek-setup.md) for detailed provisioning instructions.

---

## Related Documentation

- [Board Provisioning](./board-provisioning.md) — Tag recipe, eligibility model, and lifecycle state projection.
- [Architecture Document](./arch.md) — §8 (Azure DevOps Boards Integration).
