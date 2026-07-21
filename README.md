# Archivio

Archivio is a personal digital archivist for safely cataloguing, understanding, and eventually organising media collections.

## Milestone 1 — Production foundation

This milestone provides:

- .NET 10 WPF shell
- Generic Host dependency injection
- MVVM with CommunityToolkit.Mvvm
- structured file logging with Serilog
- SQLite through EF Core
- an initial EF migration
- clean project boundaries
- unit and integration test projects
- repository-level NuGet isolation
- fail-fast PowerShell scripts

No user media is scanned or modified in this milestone.

## Requirements

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022/2026 with .NET desktop development, or Rider

## Build

```powershell
.\build.ps1
```

## Run

```powershell
.\run.ps1
```

Application data is stored under `%LOCALAPPDATA%\Archivio`.

## Architecture

- `Archivio.App`: WPF composition root and views
- `Archivio.Domain`: domain entities and rules
- `Archivio.Application`: use-case contracts and application services
- `Archivio.Infrastructure`: operating-system and external-service adapters
- `Archivio.Persistence`: EF Core and SQLite
- `Archivio.Media`: future media processing implementation
- `Archivio.Metadata`: future metadata provider implementation
- `Archivio.Workers`: future background orchestration
- `Archivio.Shared`: cross-cutting primitives only

## Milestone review

- [ ] Restore succeeds with existing machine NuGet feeds
- [ ] Release build succeeds with warnings treated as errors
- [ ] Unit tests pass
- [ ] Integration tests create and migrate a temporary SQLite database
- [ ] WPF shell opens
- [ ] `%LOCALAPPDATA%\Archivio\logs` contains a log file
- [ ] `%LOCALAPPDATA%\Archivio\data\archivio.db` exists

## Patch 1.0.1

The unit-test project intentionally references only non-UI projects. This avoids a target-framework mismatch between `net10.0` tests and the `net10.0-windows` WPF application.

## Patch 1.0.2

Added `Microsoft.Extensions.Options.DataAnnotations` to `Archivio.Application` so `ValidateDataAnnotations()` compiles during options registration.

## Patch history

- 1.0.4: Added the missing EF Core migration discovery metadata and strengthened the migration integration test.
