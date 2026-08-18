# Agent Work Controller

A .NET service that discovers Azure DevOps Boards work items intended for autonomous agent execution, claims and manages those items, provisions a local workspace, clones the target repository, invokes an agent runtime, tracks the run lifecycle, and reports results back to Azure DevOps.

The first supported runtime is `pi` with `pi-materia`, invoked through a pluggable `IAgentRuntime` contract. A fully local work source (`LocalFile`), local git cloning, and a mock runtime allow running the controller end-to-end without Azure DevOps.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) with npm (Web UI)
- `git` (repository cloning)
- For secrets support: a Key Encryption Key (KEK) file — the app fails to start without one:
  ```bash
  openssl rand 32 > ~/.agent-work-controller/kek.key
  chmod 600 ~/.agent-work-controller/kek.key
  export AGENT_CONTROLLER_SECRET_KEK_FILE_PATH=~/.agent-work-controller/kek.key
  ```
- For Azure DevOps integration: an Azure DevOps Personal Access Token (work items: Read & write), provided as the `AZURE_DEVOPS_PAT` environment variable (see `.env.example`).

## Configuration

Start from `appsettings.example.json` (worker, persistence, work source, runtime options) and `.env.example` (environment variables).

## Build

```bash
dotnet tool restore   # restore local dotnet tools (e.g. CSharpier)
dotnet build
```

## Run

With Aspire orchestration (runs migrations, then the API and Web UI; dashboard at the URL printed by the AppHost):

```bash
dotnet run --project src/AgentController.AppHost
```

Without Aspire, start the API and Web UI separately:

```bash
# terminal 1
dotnet run --project src/AgentController.Api --launch-profile http

# terminal 2
cd src/AgentController.WebUi
npm ci
npm run dev
```

## Test

Tests use xUnit and live under `tests/`. The canonical wrapper is:

```bash
./dev/test.sh
```

`dotnet test` works directly; pass any `dotnet test` arguments through to the wrapper (e.g. `./dev/test.sh --filter "FullyQualifiedName~Api"`).

## Solution Layout

```
src/
  AgentController.Api/             ASP.NET Core host (API + background worker)
  AgentController.AppHost/         Aspire orchestration host
  AgentController.Application/     Service ports / interfaces
  AgentController.Domain/          Domain models, records, lifecycle vocabulary
  AgentController.Infrastructure/  Provider implementations
  AgentController.Migrations/      EF Core migration runner (console)
  AgentController.ServiceDefaults/ Shared Aspire conventions
  AgentController.WebUi/           Svelte + TypeScript + Vite + Tailwind Web UI
tests/
  AgentController.Api.Tests/
  AgentController.Application.Tests/
  AgentController.Domain.Tests/
  AgentController.Infrastructure.Tests/
```

## Documentation

See `docs/` for details: `development.md` (development guide), `arch.md` (architecture), `secrets.md` and `kek-setup.md` (secrets management), `pull-request-feedback-workflows.md` (PR revival/assistance), and `dev/integration-test/` + `dev/integration-spike/` (real-`pi` integration harnesses).
