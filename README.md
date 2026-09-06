# Metaroq

**Media that finally makes sense.**

Metaroq is a Windows media-intelligence application for safely cataloguing, understanding, reviewing, and organising complex personal collections.

## Current capabilities

Version 0.2 provides:

- a .NET 10 WPF application using Generic Host, dependency injection, and MVVM
- persistent library sources and media catalogues in SQLite
- background, cancellable scans for local and network folders
- type-specific discovery for movies, television, music, audiobooks, documents, and photos
- resilient audiobook metadata analysis with saved checkpoints
- embedded-tag, filename, and folder evidence with visible provenance
- rate-limited Open Library enrichment with positive and negative caching
- item-first review, manual corrections, collection splitting, and series handling
- whole-library dry runs with conflict detection and approval controls
- guarded move, rename, and embedded-metadata execution
- Open Library cover artwork embedded into audio files with a non-overwriting Plex-compatible `cover.jpg` or `cover.png` alongside each audiobook
- execution journalling, rollback, and interrupted-run recovery
- automatic SQLite safety backups before migrations and media execution

Scanning and analysis are catalogue-only operations. Media files are changed only through an explicitly confirmed, approved execution plan.

Each library source can optionally use a separate organisation destination. Leaving it blank keeps the existing behaviour and organises within the scanned folder; setting it creates an intake-to-library workflow in which Metaroq scans the source but moves approved files into the chosen destination. Source and destination roots are validated independently and recorded in the execution journal so rollback remains safe across folders or drives.

## Automation principle

Metaroq is automation-first across every supported library type. It should combine all available evidence—embedded tags, filenames, folder structure, technical media properties, saved decisions, and suitable online sources—to establish identity and enrich missing information. High-confidence, internally consistent plans proceed without routine manual decisions. Review is reserved for genuinely low-confidence identity, contradictory evidence, incomplete ordering, or an unsafe file destination. Optional enrichment that cannot be found is reported transparently but never becomes a mandatory form field or blocks an otherwise reliable plan.

## Audiobook batch dry run

Metaroq can turn saved audiobook organisation proposals into a whole-library dry-run queue. The queue expands each plan into file-level source and destination paths, detects missing sources and destination conflicts, and stores automatic or selected-plan approval and deferral decisions in SQLite. High-confidence plans are approved automatically when their identity evidence and destinations are safe. This includes multipart books with a complete, unique track order. Explicitly deferred plans remain deferred, while low-confidence, review-required, and conflicting plans cannot proceed until their safety checks pass.

Before building that queue, Metaroq forms logical audiobook plans from local metadata and folder evidence. Books whose embedded genre is missing, whose genre only describes the format, or whose files have no embedded artwork are looked up online once per logical book rather than once per chapter or source file. This includes clean books that are already organised, allowing a normal analysis and approved execution to backfill missing covers across the existing library. A result is accepted only when the existing conservative title and author thresholds pass. Cover artwork is carried into the execution plan only while the matched title and author still equal the final reviewed identity, preventing a stale suggestion from supplying artwork after an identity correction.

Author and title are the only metadata required for an automation-ready identity. Genre, publication year, series, subjects, and cover artwork are optional enrichment: Metaroq applies reliable online or embedded values when available, but a missing optional value does not force review. Execution preserves existing optional tags when enrichment has no replacement and never writes `Uncategorised` into the media file.

The review workspace separates plans that are already organised from those needing identity review or conflict resolution. A reviewer can compare every original filename, library path, embedded title, author, album, track, duration, and available online cover beside the correction controls, and can open a selected source in the Windows default audio player. High-confidence plans are approved automatically, including multipart books when Metaroq verifies a complete unique order, consistent identity, and conflict-free destination; the author/title fields are corrections, not mandatory confirmations. Missing parts, duplicate ordering, mixed identities, uncertain author or title, and destination conflicts still require review. Durable corrections immediately regenerate and revalidate the plan without changing any media files. The automatic Metaroq identity or genre suggestion can be restored at any time.

Metaroq distinguishes multipart chapter sets from collections of complete books before online enrichment. Multiple long-form `.m4b` files with distinct embedded titles or explicit labels such as `Series, Book 6` are searched and planned as independent audiobooks. A reviewer can also explicitly split an ambiguous collection and assign an optional series name and book number. Series books are proposed beneath a shared series folder while retaining separate book folders and files.

## Guarded audiobook execution

Approved, conflict-free move and rename plans can now be executed after an explicit confirmation. Immediately before execution, Metaroq revalidates the library boundary, source existence, source size and modified time, unique targets, and destination availability. Existing destination files are never overwritten.

Before execution, Metaroq creates a consistent SQLite backup. Every run and file operation is then written to an execution journal before media changes begin. The original embedded tags and artwork are journalled before Metaroq writes the approved title, author, album, genre, year, series, track order, and selected Open Library cover. After successful execution, a `cover.jpg` or `cover.png` is added to each audiobook folder for Plex and other local-media scanners; an existing cover is never overwritten. Progress is checkpointed after each operation. If an operation fails or the user cancels, completed moves and embedded metadata changes are rolled back in reverse order where that can be done safely. Interrupted runs are detected when the source is reopened and must be recovered before another execution can start.

This milestone supports move, rename, and journalled metadata updates. Audio combining, deletion, and unattended execution remain disabled.

## Requirements

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022/2026 with .NET desktop development, or Rider

## Build

```powershell
.\build.ps1
```

The validation script restores packages, checks formatting, builds Release with warnings treated as errors, and runs every unit, integration, and view-model test. The same sequence runs in GitHub Actions.

## Run

```powershell
.\run.ps1
```

Application data remains under `%LOCALAPPDATA%\Archivio` during the first Metaroq rebrand phase. This compatibility path intentionally preserves existing catalogues, saved analysis, organisation proposals, dry-run decisions, and execution journals. Safety backups are stored under `%LOCALAPPDATA%\Archivio\backups`; the newest 20 are retained. A later technical migration can move the data to `%LOCALAPPDATA%\Metaroq` with an explicit backup and rollback path.

## Architecture

- `Archivio.App`: WPF composition root and views
- `Archivio.Domain`: domain entities and rules
- `Archivio.Application`: use-case contracts and application services
- `Archivio.Infrastructure`: operating-system and external-service adapters
- `Archivio.Persistence`: EF Core and SQLite
- `Archivio.Media`: safe file discovery and media scanning adapters
- `Archivio.Metadata`: embedded metadata reading, inference, and guarded tag writing
- `Archivio.Workers`: background scan queue and cancellation orchestration
- `Archivio.Shared`: cross-cutting primitives only

Internal assembly and namespace names remain `Archivio` while the product is rebranded incrementally. This is intentional until the application-data migration and compatibility plan are complete.

## Current boundaries

- audio combining is not implemented
- deletion is disabled
- unattended media execution is disabled
- no installer or signed desktop package is currently produced
- a public-distribution licence has not yet been selected

## Development status

The authoritative branch is `main`; feature work is developed on short-lived branches and merged after `build.ps1` passes. The next product milestone is stronger content identification followed by separately reviewed support for combining compatible multipart audiobooks and carefully controlled unattended organisation.
