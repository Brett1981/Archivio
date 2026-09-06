# Metaroq

**Media that finally makes sense.**

Metaroq is a personal media-intelligence application for safely cataloguing, understanding, and eventually organising complex collections.

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

This milestone adds the first complete user-facing Metaroq workflow:

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

Metaroq stores only the selected folder metadata during this milestone. It does not scan, rename, move, delete, or otherwise modify files inside a library source.

## Audiobook batch dry run

Metaroq can turn saved audiobook organisation proposals into a whole-library dry-run queue. The queue expands each plan into file-level source and destination paths, detects missing sources and destination conflicts, and stores bulk or selected-plan approval and deferral decisions in SQLite. Bulk approval is deliberately limited to clean, single-source-file audiobook plans at 100% confidence, including plans already in the correct location that require no file operation. Multi-file plans always require an individual user decision because grouping and track order remain separate risks even when the metadata match is exact. Review-required and conflicting plans cannot be approved until their safety checks pass.

Before building that queue, Metaroq forms logical audiobook plans from local metadata and folder evidence. Books whose embedded genre is missing or only describes the format are looked up online once per logical book rather than once per chapter or source file. A result is accepted only when the existing conservative title and author thresholds pass, and the suggestion is then shared by every source file in that audiobook plan.

The review workspace separates plans that are already organised from those needing metadata review or conflict resolution. A reviewer can compare every original filename, library path, and embedded title, author, album, track, and duration beside the correction controls, and can open a selected source in the Windows default audio player. They can then confirm the complete author, audiobook title, and a supported genre. These durable corrections immediately regenerate and revalidate the plan without changing any media files. The automatic Metaroq identity or genre suggestion can be restored at any time.

Metaroq distinguishes multipart chapter sets from collections of complete books before online enrichment. Multiple long-form `.m4b` files with distinct embedded titles or explicit labels such as `Series, Book 6` are searched and planned as independent audiobooks. A reviewer can also explicitly split an ambiguous collection and assign an optional series name and book number. Series books are proposed beneath a shared series folder while retaining separate book folders and files.

## Guarded audiobook execution

Approved, conflict-free move and rename plans can now be executed after an explicit confirmation. Immediately before execution, Metaroq revalidates the library boundary, source existence, source size and modified time, unique targets, and destination availability. Existing destination files are never overwritten.

Every run and file operation is written to a SQLite execution journal before media changes begin. The original embedded tags are journalled before Metaroq writes the approved title, author, album, genre, year, series, and track order. Progress is checkpointed after each operation. If an operation fails or the user cancels, completed moves and metadata changes are rolled back in reverse order where that can be done safely. Interrupted runs are detected when the source is reopened and must be recovered before another execution can start.

This milestone supports move, rename, and journalled metadata updates. Audio combining, deletion, and unattended execution remain disabled.

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

Application data remains under `%LOCALAPPDATA%\Archivio` during the first Metaroq rebrand phase. This compatibility path intentionally preserves existing catalogues, saved analysis, organisation proposals, and dry-run decisions. A later technical migration can move the data to `%LOCALAPPDATA%\Metaroq` with an explicit backup and rollback path.

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
- [ ] A library source remains after restarting Metaroq
- [ ] A library source can be edited and enabled or disabled
- [ ] Duplicate normalized paths are rejected
- [ ] Missing folders are rejected
- [ ] A library source can be deleted
- [ ] No files inside a selected source are modified

## Patch history

- 1.0.1: Kept the unit-test project isolated from the Windows-only WPF project.
- 1.0.2: Added `Microsoft.Extensions.Options.DataAnnotations` for startup options validation.
- 1.0.4: Added EF Core migration discovery metadata and strengthened migration integration coverage.
