<p align="center">
  <img src="assets/banner.svg" alt="Echelon - deploy a task, not a branch" width="760">
</p>

<p align="center"><b>English</b> - <a href="README.ru.md">Русский</a></p>

<p align="center">
  <a href="https://github.com/jrfrigat/echelon/actions/workflows/ci.yml"><img src="https://github.com/jrfrigat/echelon/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4.svg" alt=".NET 10">
</p>

# Echelon

**Echelon** plans and runs releases around the unit people actually work in: the
**task**. It reads tasks from an issue tracker and merge requests from one or more VCS connections,
works out everything a task waits on, orders the work into deploy waves, and drives the rollout into
each environment.

A task is rarely one repository. Echelon takes the subtasks, the linked tasks and the
repository ordering rules, and answers the question a release engineer asks every day: what has to
ship, in what order, and what is not ready yet.

> **Status: not production-proven.** The code is covered and both database providers are verified
> against live servers, but no rollout has yet been executed against a real GitLab. See
> [Maturity](#maturity) before you rely on it.

## Principles

- **A plan is a projection, never a stored opinion.** Every ingestion event rebuilds it from the
  atlas, so it cannot drift from reality. Operator decisions survive as deltas that are replayed on
  each build, not as a frozen result the next webhook would overwrite.
- **One derivation.** Recalculation, a hand edit, an imported YAML document and the launch itself all
  reach the deploy order through the same code, and the plan records the order it decided. Three
  copies of that logic is three chances to deploy in an order nobody approved.
- **Never a silent plan.** A rollout may deploy against a declared constraint - sometimes it must -
  but it can never look clean while doing so. Every broken constraint is recorded on the plan.
- **The core knows no provider.** GitLab and Yandex Tracker vocabularies, status dictionaries and key
  formats live in adapters. The domain sees normalised values only.
- **Deploy order is one thing, deploy method is another.** The order is the same everywhere; only
  how a repository ships to a given environment, and what counts as ready there, vary by environment.

## What it does

| | |
| :-- | :-- |
| **Ingestion** | Webhooks and polling from VCS and tracker connections, deduplicated through an inbox |
| **Planning** | The task's dependency closure, ordered into waves by task links, hierarchy and repository rules |
| **Ordering rules** | A YAML document (groups and `needs`) that generalises "repository A after repository B", with a visual editor |
| **Execution** | Per-environment rollouts, one claim per (merge request, environment), pluggable deploy strategies |
| **Readiness** | Rules per environment, overridable per repository, with a per-merge-request pin as the escape hatch |
| **Retention** | Rollout and plan history age out, so archived tasks can actually leave the operational database |

## Solution layout

Onion, with dependencies pointing inwards only:

```
Core                    enums, pure parsing; no dependencies at all
  <- Application        ports, message contracts, the planning algorithm (no EF)
      <- Infrastructure EF models, DbContext, adapters (Rebus, Redis, DataProtection)
      <- Providers.Abstractions <- Providers.GitLab / Providers.YandexTracker
          <- Web (composition root, API, hosted PWA) / Ingress.Webhooks
```

The planning algorithm is pure and unit-tested without a database. Providers are registered at
compile time through keyed services, not discovered at runtime.

## Documentation

| Doc | Description |
| :-- | :-- |
| [User guide](docs/en/user-guide.md) | What each part is for and how to use it |
| [Getting started](docs/en/getting-started.md) | Local setup with Docker Compose |
| [Architecture](docs/en/architecture.md) | System design and data flow |
| [Configuration](docs/en/configuration.md) | Every setting and environment variable |
| [Providers](docs/en/providers.md) | Adding a VCS or tracker provider |
| [Operations](docs/en/operations.md) | Deployment, monitoring, archiving |
| [Localization](docs/en/localization.md) | How i18n works |

Russian versions live in [docs/ru](docs/ru); the index is [docs/README.md](docs/README.md).
Design notes that are still open are in [docs/issues](docs/issues/README.md).

## Install

Requires .NET 10 SDK and Docker.

```bash
git clone https://github.com/jrfrigat/echelon.git
cd echelon
cp .env.example .env      # set the passwords it names
docker compose up -d
# Open http://localhost:8081
```

PostgreSQL instead of SQL Server, and a single instance without Redis:

```bash
docker compose -f docker-compose.yml -f docker-compose.postgres.yml -f docker-compose.single-instance.yml up -d
```

## Build and run

```bash
dotnet build Echelon.slnx    # 0 errors, 0 warnings; warnings are errors
dotnet test Echelon.slnx
dotnet run --project src/Echelon.Web
```

Migrations are applied at startup by default. For more than one replica, turn that off
(`Database__MigrateOnStartup=false`) and apply them from CI or an init container, or the replicas
race.

## Maturity

Verified:

- 667 tests, 0 warnings, warnings-as-errors in CI
- migrations applied from empty on live SQL Server 2022 and PostgreSQL 16, including rollback cycles
- the application starts, migrates and reports healthy against both
- the HTTP API is covered against the real host: routes, authorization policies, status codes, bodies

Not verified:

- **no rollout has been executed against a real GitLab** - no deploy strategy has ever run for real
- container images are not built in the development environment (the registry is filtered)
- behaviour under load and with several replicas is reasoned about, not measured

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

MIT - see [LICENSE](LICENSE).
