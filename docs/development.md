# Development Guide

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

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
then the API starts. The Aspire dashboard shows logs, traces, and metrics for
both projects. When running outside Aspire, start the API directly:

```bash
dotnet run --project src/AgentController.Api
```

## Test

```bash
dotnet test
```

Tests use [xUnit](https://xunit.net/). Test projects are under `tests/`.

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
