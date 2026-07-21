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

## Milestone 2 — Library sources

This milestone adds the first complete user-facing Archivio workflow:

- persistent library-source records in SQLite
- source types for mixed media, movies, television, music, audiobooks, documents, and photos
- add, edit, enable/disable, and delete operations
- native Windows folder selection
- folder-existence validation
- normalized duplicate-path protection
- deterministic source ordering
- WPF library-source management UI
- application-service unit tests
- SQLite repository integration tests

Archivio stores only the selected folder metadata during this milestone. It does not scan, rename, move, delete, or otherwise modify files inside a library source.

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

## Milestone 2 review

- [ ] Restore succeeds with existing machine NuGet feeds
- [ ] Debug build succeeds with zero warnings
- [ ] Release build succeeds with warnings treated as errors
- [ ] All 23 unit tests pass
- [ ] All 6 integration tests pass
- [ ] WPF application opens
- [ ] A library source can be added using the folder picker
- [ ] A library source remains after restarting Archivio
- [ ] A library source can be edited and enabled or disabled
- [ ] Duplicate normalized paths are rejected
- [ ] Missing folders are rejected
- [ ] A library source can be deleted
- [ ] No files inside a selected source are modified

## Patch history

- 1.0.1: Kept the unit-test project isolated from the Windows-only WPF project.
- 1.0.2: Added `Microsoft.Extensions.Options.DataAnnotations` for startup options validation.
- 1.0.4: Added EF Core migration discovery metadata and strengthened migration integration coverage.
