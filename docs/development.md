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

## Test

```bash
dotnet test
```

Tests use [xUnit](https://xunit.net/). Test projects are under `tests/`.

## Solution Structure

```
src/
  AgentController.Api/            ASP.NET Core host (API + background worker)
  AgentController.Domain/          Domain models, records, lifecycle vocabulary
  AgentController.Application/     Service ports / interfaces
  AgentController.Infrastructure/  Provider implementations (no-op by default)
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
```
