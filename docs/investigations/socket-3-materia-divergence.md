# Investigation: Socket-3 Auto-Plana → Interactive-Plani Materia Resolution Divergence

## Summary

The "Biggs" loadout (user copy of `default:full-auto`) has Socket-3 bound to `Interactive-Plani` (multiTurn=true) instead of a single-turn planner like `Auto-Plan`. This causes autonomous agent-controller runs to stall because the controller never sends `/materia continue`.

## Evidence Trail

### 1. Project Config (`.pi/pi-materia.json`)

```json
{
  "activeLoadoutId": "user:rude-copy:15d29129-5e29-4bb2-8562-0356fc3ebc2f",
  "activeLoadout": "Biggs"
}
```

The project selects the "Biggs" loadout.

### 2. Config Source Chain

From `config.resolved.json`:

```
/home/reese/.pi/agent/git/github.com/rpollard00/pi-materia/config/default.json
  < /home/reese/.config/pi/pi-materia/materia.json
  < /home/reese/projects/agent_router/.pi/pi-materia.json
```

Three layers: default shipped config → user global config → project-local config.

### 3. Loadout Comparison (all from `config.resolved.json`)

| Loadout | Source | Origin | Socket-3 Materia | multiTurn |
|---------|--------|--------|------------------|-----------|
| Full-Auto | `default` | — | `Auto-Plan` | false |
| **Biggs** | `user` | `default:full-auto` | **`Interactive-Plani`** | **true** |
| Reno | `user` | `default:full-auto` | `Auto-Plan` | false |

**Biggs** is the outlier — it's a user copy of Full-Auto but Socket-3 was changed from `Auto-Plan` (single-turn) to `Interactive-Plani` (multi-turn).

### 4. Cast Runtime Events (`events.jsonl`)

From cast `2026-06-25T15-44-35-220Z`:

```json
{"type":"cast_start","data":{"pipeline":{"sockets":{"Socket-3":{"materia":"Interactive-Plani","empty":false}}},"loadout":"Biggs"}}
{"type":"socket_start","data":{"socket":"Socket-3","materia":"Interactive-Plani","materiaLabel":"Interactive-Plani","visit":1}}
{"type":"materia_model_settings","data":{"socket":"Socket-3","materia":"Interactive-Plani","materiaModel":{"model":"glm-5.2","provider":"zai","thinking":"high"}}}
```

At cast start, pi-materia resolved Socket-3 to `Interactive-Plani` — exactly matching the Biggs loadout config. The resolution itself is **correct**; the loadout is **misconfigured**.

### 5. Cast Manifest (`manifest.json`)

```json
{"phase":"Socket-3","socket":"Socket-3","materia":"Interactive-Plani","materiaModel":{"model":"glm-5.2","provider":"zai","thinking":"high"}}
```

Confirms `Interactive-Plani` was the materia that executed.

## Root Cause

**The "Biggs" loadout's Socket-3 was manually changed from `Auto-Plan` to `Interactive-Plani` at some point after the loadout was created as a copy of `default:full-auto`.**

The `user:rude-copy:15d29129-5e29-4bb2-8562-0356fc3ebc2f` ID indicates the loadout was created via a "rude copy" mechanism (a user-initiated copy of the default loadout). At copy time or during subsequent refinement, Socket-3's materia binding was changed.

The `config.resolved.json` shows that `Interactive-Plani` is defined in the `materia` section with `"multiTurn": true`. This is the interactive planning materia that requires human `/materia continue` input to finalize its plan — it can never complete under autonomous agent-controller eventing.

### Why the divergence happened

1. **Loadout copy**: Biggs was created as a copy of Full-Auto (`originDefaultId: "default:full-auto"`)
2. **Materia name remapping**: The copy process (or subsequent manual edit) changed materia names to use the `-a` suffixed variants (e.g., `Build` → `Builda`, `Auto-Eval` → `Auto-Evala`, `Narrate` → `Narrata`). These are custom materia definitions with different model configurations.
3. **Socket-3 exception**: While most sockets got remapped to `-a` variants (which are single-turn), Socket-3 was bound to `Interactive-Plani` — the `-i` suffixed interactive variant with `multiTurn: true`
4. **No validation**: Neither the loadout copy mechanism nor the pi-materia runtime validated that a multiTurn materia on a planning socket would be incompatible with autonomous execution

### The `-a` / `-i` suffix pattern

The resolved config reveals a naming convention for custom materia variants:

| Base Materia | `-a` variant (single-turn) | `-i` variant (multi-turn) |
|---|---|---|
| `Auto-Plan` | `Auto-Plana` | — |
| `Interactive-Plan` | — | `Interactive-Plani` |
| `Build` | `Builda` | `Buildi` |
| `Auto-Eval` | `Auto-Evala` | `Auto-Evali` |
| `Narrate` | `Narrata` | `Narrati` |

The Biggs loadout correctly uses `-a` variants for Build, Eval, and Narrate sockets. But Socket-3 (planning) was bound to `Interactive-Plani` (`-i` variant, multiTurn) instead of `Auto-Plana` (`-a` variant, single-turn).

## Resolution Steps

1. **Immediate fix**: Change Biggs Socket-3 materia from `Interactive-Plani` to `Auto-Plana` (or `Auto-Plan`)
2. **Defense-in-depth**: The agent-controller's multiTurn fail-fast guard (implemented in a related work item) prevents token-sinking by detecting multiTurn agent sockets at cast_start and aborting the run
3. **Prevention**: Add validation in the loadout copy/refinement UI to warn when a multiTurn materia is bound to a socket that will be used in autonomous mode

## Artifacts Examined

- `.pi/pi-materia.json` — project-level active loadout selection
- `.pi/pi-materia/2026-06-25T15-44-35-220Z/config.resolved.json` — full resolved config with all loadouts and materia definitions
- `.pi/pi-materia/2026-06-25T15-44-35-220Z/events.jsonl` — runtime event log showing cast_start with Socket-3 → Interactive-Plani
- `.pi/pi-materia/2026-06-25T15-44-35-220Z/manifest.json` — cast manifest confirming Interactive-Plani execution
- `.pi/pi-materia/2026-06-24T20-48-08-314Z/manifest.json` — earlier cast also showing Socket-3 → Interactive-Plani (confirming the issue predates the E2E run)
