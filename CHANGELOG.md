# Changelog

> [Русский](CHANGELOG.ru.md)

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **A `gitlab-job` deploy strategy**: deploying runs one named job of the merge request's own latest
  pipeline. Neither existing strategy covers the manual-gate workflow it exists for - the pipeline
  has already run and already built the artefact, and shipping means pressing the one button on it.
  `gitlab-merge` merges, which is a different act, and `gitlab-pipeline` starts a *second* pipeline
  from a ref, rebuilding everything against the branch head rather than the commit that was tested.
  Which verb GitLab wants depends on the job's state and getting it wrong is not a no-op: a job on
  its manual gate is played, one that has already run is retried (a new job with a new id, which is
  what has to be polled), one already running is adopted rather than started twice, and a successful
  one is reported as already done unless the target's `rerun` setting says otherwise. Unlike the
  pipeline strategy this one reconciles: the job is named inside the merge request's own pipeline, so
  a step resumed after a crash re-finds the run it started. A misspelled job name is answered with
  the names the pipeline actually has, since that is the only place an operator can see them.
- **A step-by-step setup guide** ([docs/en/walkthrough.md](docs/en/walkthrough.md),
  [ru](docs/ru/walkthrough.md)): every screen in the order it is configured, with example values in
  each field and what to check before moving on, then the failures by symptom. Getting Started says
  what each screen holds; this says what to type into it.

### Fixed

- **The VCS poll pointed at a screen that no longer exists.** Its success message sent the operator
  to "Merge requests"; the section has been called "What to deploy" for a while.

## [0.2.0] - 2026-08-21

### Added

- **An ingestion screen** (`/admin/ingestion`), because a rollout that never starts is usually not a
  planning problem: it is a task, a merge request or a branch that never arrived, and until now that
  only showed in the log. It reports each background worker - running, idle, off, or held by another
  replica - with its interval, its last pass, how long it took and why it failed if it did; what has
  arrived since the process started, counted by kind and regardless of whether a webhook or a poll
  brought it; and the last poll of every connection, naming the repositories it could not read. It
  refreshes itself, since a sweep on a one-minute timer is invisible on a page you reload by hand.
- **Column filters and a rows-per-page chooser on every paged list** - tasks, work, repositories, both
  connection lists and the request audit. The filters are applied by the server and counted with the
  same query that reads the page, because each screen holds one page: filtering in the browser would
  search the slice and present the answer as though it had searched the list. The grid owns paging as
  well now, so the page number lives in one place instead of two.
- **`ITrackerIssueSource`**, an optional tracker port for listing the open issues of a connection,
  `is`-checked by the poller exactly as `ITrackerDependencySource` is. A tracker that cannot be
  searched degrades to re-reading known tasks instead of failing. Keys only: every task still enters
  the database through the one `TaskSyncRequested` path, so discovery cannot become a second way to
  write one. Yandex.Tracker implements it over the search API, with the queues to sweep (or a whole
  query) as connection settings, because which states count as open is a workflow's decision and not
  the API's.
- **Poll now for a tracker connection**, beside the one VCS connections already had, reporting how
  many syncs were queued and how many of those tasks were new. A tracker that could not be searched is
  named with its own reason - usually the missing queue setting - rather than reported as nothing to
  do.
- **A link to the project in the app bar**, outside the sign-in gate: the source is about the product,
  not about the session.
- **An opt-in test pass against a local SQL Server** (`ECHELON_SQLSERVER_TESTS=1`). Two of the fixes
  below could not fail on SQLite - it has no retrying execution strategy and only one collation - so
  the whole suite passed while the product was broken on every deployment.

### Changed

- **The API's response types are declared once and used by both ends.** The admin client carried its
  own copy of 44 of them - some mirroring a server DTO, most mirroring an anonymous object a
  controller built inline - so nothing but attention kept the two halves in step, and attention had
  already failed once. The copies are gone: the controllers project into the shared records, the
  browser reads those same records, and a mismatch is now a compile error. Paged endpoints answer with
  one `PagedResult<T>` instead of seven hand-built shapes. The wire format is unchanged.
- **Closed sets in those contracts travel as enums**, not spellings: a provider's ingestion mode, a
  setting's kind, a plugin's category, what a work item rides in, the readiness verdict (a new
  `WorkItemReadiness`), a rule's mode, a target's redeploy policy, a merge request's status and an
  ordering rule's type. The admin UI compared them as string literals (`"Poll"`, `"Enum"`,
  `"Branch"`), which no compiler could check against the server's own enum and which a rename would
  leave quietly matching nothing. The wire format is unchanged - enums have always been serialized as
  their names - except `/api/plugins`, whose `category` is now `Vcs`, `Tracker`, `Deploy` or `Action`.
- **The admin client is one class per area of the API** - tasks, work, VCS, tracker, repositories,
  planning, environments, readiness, deploy, rollouts, actions, audit, permissions, plugins and
  ingestion - each registered in the container and injected where it is used. It was one class with
  seventy methods, which every page took whole. The shared plumbing (settings, failure handling, query
  building) lives in the base they all derive from.
- **One type per file** through the contracts and the client: sixty-odd files that each hold the thing
  they are named after, instead of eight that held everything.
- **Dependencies brought up to date**: ASP.NET Core and EF Core 10.0.11, Rebus 8.9.3, OpenTelemetry
  1.18.0, StackExchange.Redis 3.1.31, the xunit runner 4.0.0 and the rest. `Microsoft.OpenApi` stays
  on 2.12.2 deliberately - `Microsoft.AspNetCore.OpenApi` 10.0.11 refuses anything 3.x, and a build
  that resolves it anyway fails restore rather than misbehaving later.

### Fixed

- **The admin UI could not read a plan at all**: "DeserializeUnableToConvertValue ... Path: $.version",
  with the API answering correctly. The browser kept its own copy of every response type, and one had
  drifted - the plan version became an ordinal `int` on the server while the copy still said `string`.
- **Recalculating a plan failed on every real database.** Both providers are configured with
  `EnableRetryOnFailure`, and a retrying execution strategy refuses a transaction it did not open:
  "the configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support
  user-initiated transactions". The planner opened its own, so `POST /api/tasks/{id}/plan/recalculate`
  answered 500 - as did import, which nests a second transaction around it. Both now open their
  transaction inside the strategy's delegate, as one retriable unit, and detach what a rolled-back
  attempt left tracked so a retry writes each row once.
- **Exporting, validating or importing a plan failed on SQL Server** with "Cannot resolve collation
  conflict ... in add operator". The merge request key was built in SQL by concatenating
  `Repository.ExternalId`, which carries a case-sensitive collation, with the connection name, which
  inherits the server's. The three columns are read out and joined in memory now.
- **A poll-mode tracker connection never produced a single task.** The sweep read the open tasks out
  of the local database and asked the tracker to re-read each one, which cannot bootstrap: a fresh
  install has no tasks, a polled connection receives no webhook, and nothing else creates one - so the
  sweep ran over an empty set on every tick and reported success. A poll now asks the tracker which
  issues are open and re-reads what is already known: the first half is what finds work at all, the
  second is what notices an issue closed while nobody was looking.
- **The app flashed white on every refresh, and again in the wrong theme.** Two causes. The boot
  script was left on its own defaults (`md3-expressive`, `md3-violet`), so a first paint used a theme
  the app then replaced; and nothing painted a background before Blazor started - the VisualStudio
  theme injects its stylesheet at runtime, so every `--flare-*` token silently fell back to white,
  which in dark mode is the one colour the app never shows. The boot script is given this app's
  defaults now, `<html>` carries the theme's own surface colour from the first frame, and the splash
  sits beside the app root instead of inside it, where Blazor used to replace it before the theme was
  ready.
- **The PWA still called itself Release Orchestrator.** The installed app's short name was
  `ReleaseOrch`, which is what a phone or a task switcher shows under the icon. Every page title also
  ends in the product name now, so a tab or an installed window says which app it is.
- **Sixteen API error messages were English whatever the language.** They were written as literals
  while the rest of the API answered through its resource file, so the same form could refuse you in
  Russian on one field and English on the next. Also: Blazor's own unhandled-error bar, which sits
  outside the component tree and cannot read a resource file - it carries both languages now and
  `<html lang>` picks one, which the language service sets along with the culture.
- **The action-type endpoint sent five of its ten schema fields**, so a bounded number arrived without
  its bounds and rendered as a plain text box. It sends the schema whole now, like every other
  provider does.
- **Two API test classes could not run at once.** WebApplicationFactory intercepts the host through
  static machinery, so a second one starting in parallel lost the race and failed with "The entry
  point exited without ever building an IHost" - an error about nothing the test did. They share a
  collection now, which serializes them.

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

[Unreleased]: https://github.com/jrfrigat/echelon/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/jrfrigat/echelon/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/jrfrigat/echelon/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jrfrigat/echelon/releases/tag/v0.1.0
