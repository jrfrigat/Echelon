# Changelog

> [Русский](CHANGELOG.ru.md)

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- **A poll-mode tracker connection never produced a single task.** The sweep read the open tasks out of
  the local database and asked the tracker to re-read each one, which cannot bootstrap: a fresh install
  has no tasks, a polled connection receives no webhook, and nothing else creates one - so the sweep ran
  over an empty set on every tick and reported success. A poll now asks the tracker which issues are
  open and re-reads what is already known: the first half is what finds work at all, the second is what
  notices an issue closed while nobody was looking, since a closed issue is absent from the answer.

### Added

- **`ITrackerIssueSource`**, an optional tracker port for listing the open issues of a connection,
  `is`-checked by the poller exactly as `ITrackerDependencySource` is. A tracker that cannot be searched
  degrades to re-reading known tasks instead of failing. Keys only: every task still enters the database
  through the one `TaskSyncRequested` path, so discovery cannot become a second way to write one.
  Yandex.Tracker implements it over the search API, with the queues to sweep (or a whole query) as
  connection settings, because which states count as open is a workflow's decision and not the API's.
- **Poll now for a tracker connection**, beside the one VCS connections already had, reporting how many
  syncs were queued and how many of those tasks were new. A tracker that could not be searched is named
  with its own reason - usually the missing queue setting - rather than reported as nothing to do.

### Changed

- **Closed sets that no user types now travel as enums**, end to end: a provider's ingestion mode, a
  setting's kind, a plugin's category and what a work item rides in. The admin UI compared string
  literals against them (`"Poll"`, `"Enum"`, `"Branch"`), which no compiler could check against the
  server's own enum, and which a rename would leave quietly matching nothing. The wire format is
  unchanged - enums have always been serialized as their names - except `/api/plugins`, whose
  `category` is now `Vcs`, `Tracker`, `Deploy` or `Action`.

## [0.1.1] - 2026-08-17

### Added

- **Where to get it**, in the README: NuGet version, downloads and Docker pulls badges, and a table
  linking the three packages and both images (Docker Hub and GHCR).
- **A Docker Hub overview for the ingress image**, which had none, and a note in CONTRIBUTING on what
  a release needs configured.

### Fixed

- **The NuGet package icon was a malformed PNG.** Its `IDAT` chunk declared 4096 bytes while carrying
  4550, so a lenient decoder stopped at row 71 of 128 - the third rank and the bottom of the plate
  were missing - and a strict one refused the file outright. Nothing rejected it on the way in: the
  file passes a header check, and `dotnet pack` and nuget.org both embed an icon without decoding it.
  Rebuilt from the geometry in `assets/logo.svg`, and every shipped PNG is now walked chunk by chunk
  in the test suite, where a length that disagrees with its payload fails the build.
- **The Docker Hub overviews were never published.** The sync step answered 403 Forbidden - an access
  token may push an image but not edit a repository description - and being `continue-on-error` it
  reported a green release over two empty pages. It now covers both repositories and stays off until
  `DOCKERHUB_DESCRIPTION_SYNC` is set, rather than failing on every release.
- **Three links in the documentation index** pointed at design notes deleted in the pre-release
  cleanup.

## [0.1.0] - 2026-08-17

First public release. Everything below describes the state the repository is published in.

### Added

- **Per-task rollout planning.** A task's dependency closure - subtasks, linked tasks and the
  repository ordering rules - is ordered into deploy waves, and the plan records the order it
  decided rather than deriving it again on every read.
- **A YAML ordering-rule language** with groups and `needs`, generalising "repository A after
  repository B" across tasks, connectors and repositories. A visual editor builds the same document
  through the same reader the planner uses.
- **Plan import and validation.** `POST /api/tasks/{id}/plan/validate` and `.../plan/import` accept
  the export schema, so a plan can be exported, edited and posted back. Import stores the wave
  assignment as ordering deltas, which every later rebuild replays.
- **Membership overrides.** A merge request can be forced into or out of a rollout, stored against
  the task so the decision survives the next ingestion event.
- **Retention for rollout and plan history**, which is what lets the archive move a task that has
  ever been deployed out of the operational database.
- **API tests against the real host**: routes, authorization policies, status codes and response
  bodies, with the database, authentication, bus and background workers stubbed and nothing else.

### Fixed

- **The launch built its own deploy order.** Waves were recomputed at launch from Entity Framework
  navigations, ignoring the wait policy, the operator's edge overrides and the ordering-rule
  document - so a rollout could deploy in an order nobody had seen. The plan now records its waves
  and the launch reads them.
- **Overriding a mandatory constraint produced a silent plan.** Dropping a task dependency or a hard
  repository link left no cycle, so nothing was reported and the plan came back looking clean.
- **Non-Latin task keys were invisible.** The default branch pattern matched `[A-Z]` only, so a
  Cyrillic key such as a Yandex Tracker project code linked nothing at all: no task, no plan, no
  rollout, and no message saying why.
- **The PostgreSQL migrations assembly was not deployed with the host**, so a PostgreSQL deployment
  with startup migration died with `FileNotFoundException`. The assembly name is resolved at
  runtime, so nothing at compile time could see it.
- **`appsettings.json` disabled startup migration** while the code, the configuration reference and
  getting-started all documented it as on by default. Anything started outside compose came up
  against a database with no schema, reported itself ready, and answered 500 to every data request.
- **Readiness checked connectivity, not usability.** `/health/ready` passed while migrations were
  pending; it now fails until they are applied.
- **Plan versions could collide.** The version was a `yyyyMMddHHmmss` label, and every ingestion
  event rebuilds every active plan, so two rebuilds inside one second shared a name. It is an
  ordinal per task now, with a unique index.
- **Task reconciliation starved.** The sweep always restarted from the first page, so tasks beyond
  it were never reconciled. It now advances a cursor and wraps.
- **GitLab timestamps were parsed with the local kind**, which PostgreSQL rejects and SQL Server
  stores as the wrong instant.
- **Branch names were compared case-insensitively on SQL Server**, so two branches differing only in
  case collided on the unique index.

[Unreleased]: https://github.com/jrfrigat/echelon/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/jrfrigat/echelon/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jrfrigat/echelon/releases/tag/v0.1.0
