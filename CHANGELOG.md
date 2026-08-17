# Changelog

> [Русский](CHANGELOG.ru.md)

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-08-17

### Fixed

- **The NuGet package icon was a malformed PNG.** Its `IDAT` chunk declared 4096 bytes while carrying
  4550, so a lenient decoder stopped at row 71 of 128 - the third rank and the bottom of the plate
  were missing - and a strict one refused the file outright. Nothing rejected it on the way in: the
  file passes a header check, and `dotnet pack` and nuget.org both embed an icon without decoding it.
  Rebuilt from the geometry in `assets/logo.svg`, and every shipped PNG is now walked chunk by chunk
  in the test suite, where a length that disagrees with its payload fails the build.

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
