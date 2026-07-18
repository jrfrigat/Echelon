# Release Orchestrator

Enterprise release planning and orchestration platform for heterogeneous VCS and issue tracker environments.

Release Orchestrator помогает автоматически выстраивать последовательность merge request'ов, управлять ручными правками плана и архивировать завершённые задачи.

---

## Quick Start

**👉 Start here:** [Comprehensive Documentation](docs/README.md)

- **[Getting Started](docs/en/getting-started.md)** — Local setup with Docker Compose
- **[Architecture](docs/en/architecture.md)** — System design and data flow
- **[Configuration](docs/en/configuration.md)** — All environment variables
- **[Providers](docs/en/providers.md)** — How to add new VCS or tracker providers
- **[Operations](docs/en/operations.md)** — Deployment and monitoring
- **[Localization](docs/en/localization.md)** — How i18n works

**Русский:**
- **[Начало работы](docs/ru/getting-started.md)**
- **[Архитектура](docs/ru/architecture.md)**
- **[Конфигурация](docs/ru/configuration.md)**
- **[Провайдеры](docs/ru/providers.md)**
- **[Эксплуатация](docs/ru/operations.md)**
- **[Локализация](docs/ru/localization.md)**

---

## ⚠️ Critical Note: Not Yet Production-Ready

**The application has never been run in a live environment.** The following have not been tested:

- Application startup against a live database (SQL Server or PostgreSQL), RabbitMQ, Redis
- ~~Database migrations (on real instance)~~ — done for SQL Server on 2026-07-17, see Known
  Limitations. Not for PostgreSQL
- Docker image builds (registry blocked in dev environment)
- Behavior against real GitLab or Yandex Tracker instances
- Concurrency and load testing

**See [Current State Audit](docs/issues/001-current-state.md) §4 for full details.**

Before deploying to production:
1. Test locally with Docker Compose (see [Getting Started](docs/en/getting-started.md))
2. Verify migrations apply cleanly to your database — `Database__Provider` picks the assembly
3. Build and test Docker images
4. Test against staging instances of your VCS/tracker
5. Load testing with expected traffic volume

---

## What It Does

Release Orchestrator transforms merge requests into a **staged deployment plan**:

1. **Input:** Merge requests from multiple VCS systems + task dependencies from trackers
2. **Processing:** Combines VCS and task dependencies into a graph, resolves cycles, topologically sorts
3. **Output:** Stages (sets of MRs that can deploy in parallel) with conflict logging

```
VCS webhooks → Ingress → RabbitMQ → Core → Release Plan
                           ↓
                        Database
```

**Key features:**
- Automatic plan generation from dependencies
- Manual editing (drag-and-drop stages, override order)
- YAML export/import for version control
- Multi-repository support (GitLab, Yandex Tracker, extensible to others)
- Permission-based access control (claim-based, integrates with OIDC)
- 90-day archival for data retention
- Prometheus metrics at `/metrics`, plus OpenTelemetry OTLP for traces and metrics

---

## Architecture Layers

```
Core                        enums and pure parsing; zero dependencies, not one
  ↑
Providers.Abstractions      provider contracts + normalized models
  ↑                    ↖
Application            Providers.GitLab / Providers.YandexTracker
  ↑                    ↗       (adapters: see only Abstractions + Core)
Infrastructure              EF models, DbContext, adapters — the composition root
  ↑
Web / Ingress.Webhooks      HTTP
```

**Dependencies point inward only.** Two consequences worth stating, because both were absent
before and both cost real defects:

- The planning algorithm lives in `Application` and touches no EF. It used to be a private method
  on a class built around `DbContext` — unreachable from a test, which is exactly why its
  dependency edges were inverted for as long as they were. It now takes `PlanMergeRequest`, so
  the data it needs is a type rather than a comment asking callers to remember an `Include`.
- No provider name appears in `Core`, and neither does an EF model. Adding a VCS, a tracker or a
  database is a project and a line of registration, not a domain change.

---

## Tech Stack

- **.NET 10** (C# 14)
- **Database:** Microsoft SQL Server 2019+ or PostgreSQL 13+ (EF Core 10) — see §7.2
- **Message Queue:** RabbitMQ 3.8+ (Rebus)
- **Coordination:** Redis 6.0+ (StackExchange.Redis), or none — see §7.1
- **Frontend:** Blazor WebAssembly (PWA, .NET 10)
- **Authentication:** OpenID Connect (external provider)
- **Observability:** Prometheus `/metrics` (on by default) + OpenTelemetry OTLP (optional)

---

## Build

```bash
dotnet build
# 0 errors, 0 warnings (checked in CI)
```

## Test

```bash
dotnet test ReleaseOrchestrator.slnx
```

Unit tests only. They cover the pure logic — the graph algorithm, the EF model's shape, the
provider registry, the composition root — and deliberately touch no database, broker or cache.
There are no integration tests: the environment this was built in has no database server, and no EF
in-memory provider is available offline, so the paths that talk to one are **unverified**.

## Run Locally

```bash
cd docs/en
# or
cd docs/ru

# See getting-started.md for Docker Compose setup
```

---

## Key Architectural Decisions

### Why Modular Monolith, Not Microservices?

Tasks, MRs, and stack definitions are tightly coupled. Building a plan requires simultaneous access to all three. A monolith with message queue allows future service extraction without coupling.

### Why Compile-Time Provider Registration?

Providers are registered explicitly in `InfrastructureExtensions.cs`, not discovered at runtime:

- **Fail-fast** — an unknown provider type is rejected when the connection is saved, naming the
  ones that would have worked, rather than becoming a stored row that fails on first use
- **Visible** — `grep AddGitLabProvider` finds every provider this deployment has
- **Checked by the compiler** — a contract change is a build error, not a runtime surprise

Dynamic loading was considered and rejected. It and container deployment cancel each other out:
the only payoff is "add a provider without rebuilding", and the image rebuilds anyway. See
[docs/issues/002](docs/issues/002-provider-independence.md) §4 for the full argument, including
what Orchard Core and Backstage did about the same question.

### Why Multiple Databases?

Operational DB holds current MRs and plans. Archive DB (same server or separate) holds closed tasks >90 days old. This keeps operational queries fast and allows archival deletion without impacting backups.

### Why a Lease, Not Consensus?

Archiving and reconciliation are registered in every replica, so both are gated on a lease —
one run per cycle across the deployment rather than one per replica. It is deliberately not a
consensus algorithm: one Redis is one point of failure, and under a partition two replicas could
briefly both believe they hold it. That is acceptable here because both jobs are idempotent, so
the worst case is the double run that used to be the *normal* case. A job where a double run were
a correctness bug would need fencing tokens instead.

### 7.1 Running Without Redis

Redis carries two things here, and neither is a source of truth:

| | What it holds | What losing it costs |
|---|---|---|
| Permission cache | Computed permissions, keyed by a hash of the stored rules | Three reads. The database decides who may do what; a stamp that is evicted recomputes to the same value. |
| Job lease | Which replica runs tonight's archive pass | The double run described above. Both jobs are idempotent. |

So the backend is selectable, and a deployment that runs **one replica of `core`** can drop Redis:

```bash
docker compose -f docker-compose.yml -f docker-compose.single-instance.yml up -d
```

| Setting | Values | Meaning |
|---|---|---|
| `Coordination__Provider` | `redis` (default), `memory` | Which backend carries the cache and the lease. |
| `Coordination__SingleInstance` | `true` / `false` | Required for `memory`. The operator's statement that the deployment is one replica. |
| `Redis__ConnectionString` | connection string | Required for `redis`; unread otherwise. |

### 7.2 SQL Server or PostgreSQL

Unlike the coordination backend, the database *is* the source of truth — so this choice is only
about which one you already run, never about doing without.

```bash
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up -d
```

| Setting | Values | Meaning |
|---|---|---|
| `Database__Provider` | `sqlserver` (default), `postgresql` | Which database, and therefore which migrations assembly. |
| `ConnectionStrings__Default` | connection string | Operational store, in that provider's own format. |
| `ConnectionStrings__Archive` | connection string | Archive store. |

Both are first-class: the same model, the same tests, the same CI checks. Migrations live in one
assembly per provider, because a migration is generated SQL rather than a description of intent —
`nvarchar(200)` and `character varying(200)` cannot share a file. The application picks the
assembly from `Database__Provider` at startup, so a mismatch is a startup failure and not a
half-applied schema.

Two mappings genuinely differ, and both live in `ProviderSpecificMapping` where they are named
rather than left to a convention:

| | SQL Server | PostgreSQL | Why it matters |
|---|---|---|---|
| Concurrency token | `rowversion` | the `xmin` system column | Npgsql accepts `[Timestamp] byte[]` and maps it to a `bytea` PostgreSQL never fills. The model builds, the migration scaffolds, and the concurrency check **silently never fires**. |
| One-active-plan index | `WHERE [IsActive] = 1` | `WHERE "IsActive"` | A filter is a fragment of SQL passed through verbatim. SQL Server's brackets are not quoting in PostgreSQL, and a boolean does not equal 1. |

The override files compose, in any order:

```bash
# PostgreSQL, single replica, no Redis
docker compose -f docker-compose.yml -f docker-compose.postgres.yml \
               -f docker-compose.single-instance.yml up -d
```

**Neither database has ever been run.** See Known Limitations — this is the same "never started"
that applies to SQL Server, and PostgreSQL does not change it in either direction.

### 7.3 The single-instance caveat

`memory` is correct for one process and **wrong for two**, and it cannot tell the difference: each
process would hold its own lease and believe it leads, so every replica would run its own archive
cycle, every one would sweep the tracker, and a revoked permission would linger in the others until
the stamp expires. That is why the assertion is separate from the provider name — an unconfigured
Redis fails at startup rather than quietly becoming a single-instance deployment, the same way
`DataProtection:AllowUnprotectedKeys` makes an operator say the risky thing out loud. Scale `core`
past one and Redis comes back.

---

## Security Model

- **Credentials encrypted at rest** — VCS and tracker tokens are encrypted with ASP.NET Core Data
  Protection. The key ring lives in the same database, so outside Development a certificate to
  encrypt the keys themselves is **required**: without one, a database dump yields both the
  ciphertext and the key. See [Configuration](docs/en/configuration.md)
- **Authentication:** OpenID Connect (delegated to external provider)
- **Authorization:** Claim-based permissions (no roles)
- **HTTPS:** Assumed at reverse proxy (Nginx/Traefik)
- **Redis cache:** Caches computed permissions; must be password-protected when used. Write access
  to it is equivalent to granting yourself permissions, since a cache hit never reaches the
  database. Not deployed under `Coordination__Provider=memory`, which removes the exposure along
  with the service (§7.1)

---

## Known Limitations

- **Never run at all.** Not "untested under load" — the application has never started against a
  live SQL Server, PostgreSQL, RabbitMQ or Redis.

  Migrations are the one part of that which is no longer true. On 2026-07-17 all six applied
  cleanly to a real SQL Server 2022, and the archive's to its own database, from empty. What that
  bought is small but it is not nothing: it is the first time anything here met a real database,
  and it settled two claims that SQLite had left open — the filtered unique index really does
  reject a second active plan (error 2601), and `Restrict` really does block deleting a merge
  request a plan still refers to (error 547). Both were previously marked unverifiable in
  `ArchiveRunnerTests` and CLAUDE.md.

  Everything else on this list stands. The application still has not started; PostgreSQL's
  migrations still have not been applied to anything
- **Neither database has ever been run.** PostgreSQL is supported on the same terms as SQL Server: same model, same tests, same CI, and the same "никогда не запускался" above. Its mapping differences are covered by tests against the built model and by generated SQL, which is the strongest check possible without a server — it is not a substitute for one
- Metrics are exposed for Prometheus at `/metrics` on both hosts (ASP.NET Core, .NET runtime,
  outbound HTTP and Rebus), scraped directly with no collector in between; OTLP export of traces
  and metrics stays available for a collector when one is configured. `docker compose -f
  docker-compose.yml -f docker-compose.observability.yml up` brings up Prometheus and Grafana wired
  to it
- No buffering when RabbitMQ is down: webhooks answer **503**, which senders retry. Buffering in
  memory was considered and rejected — it answers 200 while the event exists only in RAM, so a
  restart loses it silently. Not losing events across an outage needs a persistent outbox
- TLS is terminated outside the app; nothing in `docker-compose.yml` does it

See [docs/issues/003-roadmap.md](docs/issues/003-roadmap.md) for planned improvements.

---

## Project Structure

```
src/
  ReleaseOrchestrator.Core/                    # Domain entities
  ReleaseOrchestrator.Application/             # Application logic
  ReleaseOrchestrator.Infrastructure/          # EF Core, providers, queue
  ReleaseOrchestrator.Web/                     # REST API + BFF
  ReleaseOrchestrator.Ingress.Webhooks/        # Webhook receiver
  ReleaseOrchestrator.Pwa/                     # Blazor WebAssembly UI
  ReleaseOrchestrator.Providers.Abstractions/  # Port interfaces
  ReleaseOrchestrator.Providers.GitLab/        # GitLab adapter
  ReleaseOrchestrator.Providers.YandexTracker/ # Yandex Tracker adapter
  ReleaseOrchestrator.Migrations.MsSql/        # EF Core migrations

tests/
  ReleaseOrchestrator.UnitTests/               # unit tests (no live dependencies)

docs/
  README.md                                    # This doc's parent (navigation)
  en/, ru/                                     # Comprehensive guides
  issues/                                      # Audit reports & decision logs
```

---

## Development

### Prerequisites

- .NET SDK 10
- Visual Studio 2024, VS Code, or Rider
- Docker & Docker Compose (for local stack)

### First Steps

1. Clone repo
2. Read [Architecture](docs/en/architecture.md)
3. Run `dotnet build` (verify 0/0)
4. See [Getting Started](docs/en/getting-started.md) for Docker Compose setup

### Contributing

Before submitting a PR:
1. Run `dotnet build` (must be 0/0 errors/warnings)
2. Run `dotnet test` (all tests must pass)
3. Follow commit style: lowercase, imperative, reference issue
4. Update docs if you change architecture or add configuration

---

## Audit Reports

- **[001. Current State](docs/issues/001-current-state.md)** — What was broken, what was fixed, what was never tested
- **[002. Provider Independence](docs/issues/002-provider-independence.md)** — Why providers are registered at compile-time, not discovered
- **[003. Roadmap](docs/issues/003-roadmap.md)** — Planned features, known gaps, performance improvements

---

## Support

**Community:** GitHub Issues (if enabled)
**Documentation:** [docs/README.md](docs/README.md)
**Security:** Report privately (see SECURITY.md if present)

---

## License

[Add license here if applicable]

---

**Last Updated:** 2025-01-17  
**Status:** Audit complete, documentation published, app untested in live environment
