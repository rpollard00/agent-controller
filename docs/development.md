# Development Guide

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) with npm

## Tooling

This project uses a local dotnet tool manifest (`.config/dotnet-tools.json`).

Restore tools after cloning or when `dotnet-tools.json` changes:

```bash
dotnet tool restore
```

### CSharpier (C# formatter)

CSharpier is a zero-config, opinionated C# code formatter.

```bash
# format all source files in the solution
dotnet csharpier format .

# check formatting without changing files (CI / pre-commit)
dotnet csharpier check .
```

## Build

```bash
dotnet build
```

## Run (Aspire)

```bash
dotnet run --project src/AgentController.AppHost
```

The Aspire AppHost orchestrates startup: migrations run to completion first,
then the API and the Vite development server start. The Web UI waits for and
proxies `/api` requests to the API through its Aspire service reference. The
Aspire dashboard shows logs, traces, and metrics for all three resources.

To run without Aspire, start the API and Web UI in separate terminals:

```bash
# terminal 1 (the Vite proxy defaults to this HTTP endpoint)
dotnet run --project src/AgentController.Api --launch-profile http

# terminal 2
cd src/AgentController.WebUi
npm ci
npm run dev
```

Set `API_URL` before `npm run dev` to proxy `/api` to a different API endpoint.

## Production Web UI

Publishing the API performs a clean Web UI dependency install and production
build, then includes the generated Vite assets in the API's `wwwroot` output:

```bash
dotnet publish src/AgentController.Api -c Release -o ./publish
dotnet ./publish/AgentController.Api.dll
```

The published host serves static assets and falls back to `index.html` for
client-side routes. Existing controller routes take precedence, and unknown
`/api` routes remain 404 responses. Set the MSBuild property
`SkipWebUiBuild=true` only when intentionally publishing the API without Web UI
assets.

For a standalone frontend build or preview:

```bash
cd src/AgentController.WebUi
npm ci
npm run check
npm test
npm run build
npm run preview
```

## Test

The canonical way to run tests is the quiet wrapper:

```bash
./dev/test.sh
```

This runs `dotnet test` with `-v minimal`, suppressing xUnit adapter chatter
(Discovering/Discovered/Starting/Finished/version lines) while keeping
per-project pass/fail summaries and full failure detail.

You can also run `dotnet test` directly — the `Directory.Build.props` default
sets minimal verbosity for test projects. The script is the guaranteed-quiet
fallback.

Pass any `dotnet test` arguments through:

```bash
./dev/test.sh --filter "FullyQualifiedName~Api"
./dev/test.sh -v normal          # override verbosity
```

Tests use [xUnit](https://xunit.net/). Test projects are under `tests/`.

`PiMateriaRuntimeTests` covers the real pi runtime with a deterministic
fake-`pi` process and a real HTTP listener — no LLM, no network, CI-friendly.

## Integration harnesses (real `pi` + real controller)

Two harnesses under `dev/` exercise real `pi` and pi-materia. They require
per-machine prereqs that `dotnet test` does not (`.NET 10 SDK`, `pi` on PATH
with pi-materia installed, a configured model + API key, `git`, `jj`).

- **`dev/integration-test/` (Tier B — full controller-driven workflow).** Boots
  the real `AgentController.Api` (PollingWorker + `PiMateriaRuntime`), scaffolds
  a clean widget repo, seeds one `LocalFile` work item, and runs a complete Wedge
  cast through the real `POST /runs/{runId}/events` endpoint. Polls `GET /runs/{id}`
  to terminal and asserts `runtime.completed`. This is the only harness that
  exercises the real poll loop, runtime, and pi against the real endpoint.
  Run: `./dev/integration-test/run_test.sh`.

- **`dev/integration-spike/` (real pi, stand-in listener).** Runs a single real
  `/materia cast` against a throwaway repo with a minimal Python HTTP listener
  in place of the controller. Faster to iterate on when you only want to verify
  the pi-materia → controller webhook contract. Run: `./dev/integration-spike/run_spike.sh`.

See each directory's `README.md` for options and prerequisites.

## Solution Structure

```
src/
  AgentController.Api/             ASP.NET Core host (API + background worker)
  AgentController.AppHost/         Aspire orchestration host
  AgentController.Application/     Service ports / interfaces
  AgentController.Domain/          Domain models, records, lifecycle vocabulary
  AgentController.Infrastructure/  Provider implementations (no-op by default)
  AgentController.Migrations/      EF Core migration runner (console)
  AgentController.ServiceDefaults/ Shared Aspire conventions (OTel, health, resilience)
  AgentController.WebUi/           Svelte, TypeScript, Vite, and Tailwind Web UI
tests/
  AgentController.Api.Tests/
  AgentController.Application.Tests/
  AgentController.Domain.Tests/
  AgentController.Infrastructure.Tests/
```

Project references flow one way:

```
  Domain           (no project references)
    ^
  Application      -> Domain
    ^
  Infrastructure   -> Application, Domain
    ^
  Api              -> Application, Infrastructure
  Migrations       -> Infrastructure

  AppHost          -> Api, Migrations (orchestration only)
  ServiceDefaults  (shared library, referenced by Api and Migrations)
```
