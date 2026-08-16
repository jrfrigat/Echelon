# Echelon - Configuration

> [Русская версия ->](../ru/configuration.md) - [← Back to docs](../README.md)

---

## Overview

All configuration is read from environment variables on startup. The app performs **fail-fast validation**: if a required variable is missing, the application refuses to start before any dependency (RabbitMQ, Redis, database) is initialized.

This document lists every environment variable, required/optional status, and what happens if missing.

---

## Required Configuration

These must be set or the application will not start.

### Database Connection

**`ConnectionStrings__Default`** (REQUIRED)
- **What:** Connection string for the operational database
- **Format:** SQL Server connection string
- **Example:** `Server=localhost;Database=Echelon;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **If missing:** `InvalidOperationException` on startup - "ConnectionStrings:Default is required"
- **Retry on transient errors:** Enabled (SQL Server timeout, deadlock -> automatic retry, exponential backoff)

**`ConnectionStrings__Archive`** (REQUIRED)
- **What:** Connection string for the archive database (historical data >90 days old)
- **Format:** SQL Server connection string
- **Example:** `Server=localhost;Database=EchelonArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **If missing:** `InvalidOperationException` on startup
- **Note:** Can be the same SQL Server instance as Default, but separate database, or entirely separate server

### Message Queue (RabbitMQ)

Give the broker either as a full connection string **or** as its parts. The parts are what
`docker-compose.yml` sets; a connection string is for a deployment that would rather hand over one
URI (TLS, a cluster, a non-default vhost).

**`Queue__ConnectionString`** (optional - the connection string form)
- **What:** A full AMQP URI, e.g. `amqps://user:pass@rabbit.company.com:5671/vhost`
- **Behaviour:** When set, it is used verbatim and the parts below are ignored
- **When to use:** TLS (`amqps`), a clustered host list, or a vhost you would rather not assemble

**`Queue__Username`** (required unless `Queue__ConnectionString` is set)
- **What:** RabbitMQ authentication username
- **Example:** `guest`
- **If missing:** `InvalidOperationException` at startup, before the bus connects
- **Security note:** Never use `guest` in production

**`Queue__Password`** (required unless `Queue__ConnectionString` is set)
- **What:** RabbitMQ authentication password
- **If missing:** `InvalidOperationException` at startup
- **Security note:** Use strong password in production

**`Queue__Host`**
- **What:** Hostname or IP of RabbitMQ broker
- **Example:** `rabbitmq.company.com` or `localhost`
- **Default:** `localhost`

**`Queue__Port`**
- **What:** RabbitMQ AMQP port
- **Example:** `5672` (standard)
- **Default:** 5672

**`Queue__VirtualHost`**
- **What:** RabbitMQ virtual host
- **Example:** `/` (the default vhost) or `release`
- **Default:** `/`

### Cache (Redis)

**`Redis__ConnectionString`** (REQUIRED)
- **What:** Redis connection string for permission caching
- **Format:** `{host}:{port}` or `{host}:{port},password={password}`
- **Example:** `redis.company.com:6379` or `localhost:6379,password=MySecurePassword`
- **If missing:** `InvalidOperationException` on startup
- **Important:** Permissions are cached here; without Redis, every API call queries the database. **Do not expose Redis to untrusted networks** - cached permissions are not validated on every request.

---

## Optional Configuration with Defaults

### Application Environment

**`ASPNETCORE_ENVIRONMENT`**
- **What:** Controls logging, error details, middleware behavior
- **Valid values:** `Development`, `Production`, `Staging`
- **Default:** `Production`
- **Production behavior:** No stack traces in error responses, HSTS enabled
- **Development behavior:** Full error details, middleware logging

### Hosting

**`ASPNETCORE_URLS`**
- **What:** HTTP listener addresses
- **Example:** `https://localhost:5173;http://localhost:5172`
- **Default:** `http://localhost:5000`

**`ASPNETCORE_FORWARDEDHEADERS_ENABLED`**
- **What:** Whether to trust `X-Forwarded-For`, `X-Forwarded-Proto` headers (set by reverse proxy)
- **Valid values:** `true` or `false`
- **Default:** `false`
- **Important:** Must be `true` if running behind Nginx/Traefik and using HTTPS redirect or HSTS

**`ASPNETCORE_FORWARDEDHEADERS_KNOWNNETWORKS`**
- **What:** CIDR networks from which to trust forwarded headers
- **Example:** `172.16.0.0/12;10.0.0.0/8`
- **Default:** localhost, link-local ranges
- **Security:** If empty, only localhost is trusted

### Archive Service

**`Archiving__Enabled`** (section: `Archiving`)
- **What:** Whether to run the archive service
- **Valid values:** `true`, `false`
- **Default:** `true`
- **If false:** No archival happens; operational DB grows unbounded
- **Note:** Registered in every replica but gated on a lease, so one cycle runs per night across the deployment

**`Archiving__RunAtUtcHour`**
- **What:** Hour of day, UTC, at which the nightly cycle starts
- **Example:** `2`
- **Default:** 2
- **Note:** An hour, not an interval - the cycle runs once a night. There is no cron expression: none could be honoured without a parser in this assembly, and a setting that is read but ignored is worse than no setting

**`Archiving__ArchiveAfterDays`**
- **What:** How long a merge request or task must have been terminal before it is eligible to move
- **Example:** `90`
- **Default:** 90
- **Behavior:** Measured from the merge, close or task-closed timestamp, never from first sight. A row still referenced by a plan, a rollout or a deployment state waits regardless of age

**`Archiving__MrBatchSize`**
- **What:** Merge requests moved per batch
- **Example:** `500`
- **Default:** 500

**`Archiving__TaskBatchSize`**
- **What:** Tasks moved per batch
- **Example:** `1000`
- **Default:** 1000
- **Note:** Larger batches = fewer DB round-trips, but more lock contention

**`Archiving__StatusJournalRetentionDays`**
- **What:** How long merge-request status and label transitions are kept, in days
- **Example:** `730`
- **Default:** 730 (two years)
- **Behavior:** Pruned outright, not archived - these rows back the task timeline and nothing else reads them. A long-lived task can lose its earliest transitions while still open; raise this if that history matters more than the rows

**`Archiving__RolloutHistoryRetentionDays`**
- **What:** How long a finished rollout and its steps are kept, in days
- **Example:** `730`
- **Default:** 730 (two years)
- **Behavior:** Only terminal runs are pruned; one that never finished is kept whatever its age. Steps and events go with it by cascade
- **Why it matters:** Not a tidiness setting. `RolloutStep` references a task and a merge request with `Restrict`, so until history ages out, a task that was ever deployed cannot be archived at all - this is what lets the archive drain

**`Archiving__PlanHistoryRetentionDays`**
- **What:** How long a superseded plan version is kept, in days
- **Example:** `90`
- **Default:** 90
- **Behavior:** Also removes every version of a task closed before `ArchiveAfterDays`, active one included - `PlanTaskNode` pins the task otherwise. Recalculating rebuilds a plan from the atlas, so nothing is unrecoverable
- **Why shorter than the rollout history:** Every ingestion event rebuilds every active plan, so this is churn rather than evidence - what actually happened is the rollout

### Task Reconciliation Service

**`TaskReconciliation__Enabled`** (section: `TaskReconciliation`)
- **What:** Whether to periodically sync task dependencies from tracker
- **Valid values:** `true`, `false`
- **Default:** `true`
- **If false:** Task dependencies are only loaded when tasks are explicitly created or status-changed
- **Why it exists:** Trackers raise no event when a dependency link is added or removed, so without this sweep an edge added in the tracker never arrives until something unrelated touches that task

**`TaskReconciliation__IntervalMinutes`**
- **What:** How often to sync open tasks
- **Example:** `30`
- **Default:** 30
- **Note:** Leased, so one pass runs per interval across the deployment rather than one per replica

**`TaskReconciliation__MaxTasksPerRun`**
- **What:** Open tasks re-read per pass, so a backlog cannot flood the tracker's API
- **Example:** `500`
- **Default:** 500
- **Behavior:** Passes resume where the last one stopped and wrap around, so more open tasks than the cap means slower coverage, not tasks that are never swept

### Ingestion Polling

For a connection whose provider type is a *poll* type (`gitlab-poll`, `yandextracker-poll`), the app
re-reads it on a timer instead of receiving webhooks. Each connection carries its own interval in its
settings; these globals set whether the poller runs and how often it wakes - the floor for a
connection's interval.

**`VcsPolling__Enabled`** / **`TrackerPolling__Enabled`** (sections: `VcsPolling`, `TrackerPolling`)
- **What:** Whether the VCS / tracker poller runs
- **Valid values:** `true`, `false`
- **Default:** `true`

**`VcsPolling__IntervalSeconds`** / **`TrackerPolling__IntervalSeconds`**
- **What:** How often the poller wakes and sweeps poll-mode connections - the floor for a connection's own interval
- **Example:** `60`
- **Default:** 60

**`TrackerPolling__MaxTasksPerRun`**
- **What:** Open tasks re-read per tracker connection per pass
- **Example:** `500`
- **Default:** 500

### Database migrations

**`Database__MigrateOnStartup`** (section: `Database`)
- **What:** Whether the app applies pending EF Core migrations at startup
- **Valid values:** `true`, `false`
- **Default:** `true`
- **If false:** Apply migrations by hand (or from an init container / CI) - recommended for a multi-replica deployment, where concurrent auto-migration would race

### Authorization & Bootstrap

**`Authorization__BootstrapAdminObjectIds`** (section: `Authorization`)
- **What:** Semicolon-separated list of user OIDs to grant bootstrap admin access
- **Example:** `00000000-0000-0000-0000-000000000001;00000000-0000-0000-0000-000000000002`
- **Default:** Empty (no bootstrap admins)
- **Behavior:** Users whose `oid` claim matches these values get full permissions automatically
- **Security:** Remove this after setup; it's a permanent admin bypass if set
- **Use case:** Fresh deployment where no users have initial permissions

**`Authorization__PermissionBootstrapEnabled`**
- **What:** Whether to auto-seed permission claims on first login
- **Valid values:** `true`, `false`
- **Default:** `true`
- **Behavior:** On first run, `PermissionClaims` table is populated with standard claims (`release.plan.approve`, `config.edit`, etc.)

---

## External Provider Configuration

Providers are configured per **connection** (stored in database), not globally. However, some environment hints exist:

### VCS Provider Settings

Configured when adding a VCS Connection in the Admin UI. The generic fields:
- **API URL** - endpoint of GitLab or other VCS
- **Access Token** - encrypted at rest

Everything else is declared by the chosen provider and rendered from its schema, so it varies by type:
- **Type** - `gitlab-webhook` (GitLab pushes events to the ingress) or `gitlab-poll` (the orchestrator polls; adds a **poll interval**, in seconds)
- **Linking rule** - how an incoming merge request is matched to its tracker task: a **task-key source** (branch, title or label) and a **pattern** (regex). The connection form previews the key the rule would extract from a sample.

Deploy readiness is **not** a connection field. It is configured per environment (and optionally per repository) as a named **readiness rule** over normalized signals - a label, a merge-request status, or a pipeline result - on the Environments and Readiness Rules admin pages.

### Tracker Provider Settings

Configured when adding a Tracker Connection. Generic fields are **API URL** and **Access Token** (encrypted at rest); the rest are declared by the provider and rendered from its schema:
- **Organization ID** - (Yandex Tracker) sent as the `X-Org-Id` header
- **Closed statuses** - (Yandex Tracker) comma-separated status keys that mean a task is done; blank uses the defaults (`closed, cancelled, rejected, resolved`)

Provider settings are stored as JSON in `TrackerConnection.ProviderSettingsJson`; secret settings are encrypted with the same key ring as the access token.

Example:
```json
{
  "customFieldMap": {
    "dependency": "depends_on_field_id",
    "sprint": "sprint_field_id"
  }
}
```

These are provider-specific and documented in [Providers](providers.md).

---

## OpenID Connect

Echelon relies on an external OIDC provider. Configuration is typically in web application configuration (e.g., `appsettings.json` or Azure AD in admin portal), not environment variables.

**Key claims used:**
- `oid` - Unique user identifier (required)
- `name` - Display name (optional)
- `email` - Email address (optional)

Ensure your OIDC provider includes `oid` in ID tokens.

---

## Logging & Observability

### Logging

**Destination:** Console (JSON format in production)

**Level control:** Set via standard .NET Core:
```bash
LOGLEVEL_Echelon=Debug
LOGLEVEL_Microsoft.EntityFrameworkCore=Warning
```

### Structured Logging

All operational events are logged as JSON. Example:
```json
{
  "timestamp": "2025-01-15T10:30:00Z",
  "level": "Information",
  "logger": "Echelon.Application.ReleasePlanning.ReleasePlanner",
  "message": "Release plan recalculated",
  "planId": "550e8400-e29b-41d4-a716-446655440000",
  "stageCount": 5,
  "conflictCount": 2
}
```

### Metrics (Prometheus)

**`Prometheus__Enabled`**
- **What:** Whether to expose the `/metrics` scrape endpoint
- **Valid values:** `true`, `false`
- **Default:** `true`
- **Endpoint:** `GET /metrics` on both hosts, Prometheus text format, anonymous and not rate-limited
- **Exports:** ASP.NET Core request metrics, the .NET runtime (GC, thread pool, memory), outbound
  HTTP client calls, and Rebus message timings
- **If false:** No `/metrics` endpoint and no meters are registered for it

### Traces and metrics export (OpenTelemetry / OTLP)

**`OTEL_EXPORTER_OTLP_ENDPOINT`** (optional)
- **What:** Endpoint of an OpenTelemetry collector (also settable as `Otel__Endpoint`)
- **Example:** `http://localhost:4317`
- **If set:** Traces are exported to the collector, and metrics are pushed there in addition to
  being scrapeable at `/metrics`
- **If not set:** No traces are emitted; metrics still work over Prometheus

**`Otel__Enabled`**
- **What:** A kill switch for OTLP export that wins over a configured endpoint
- **Default:** `true`
- **If false:** OTLP export is off even when an endpoint is set; Prometheus is unaffected

**`OTEL_EXPORTER_OTLP_PROTOCOL`** (optional)
- **What:** `grpc` (default) or `http/protobuf`, following the OTLP spec's names

**Known limitation:** Prometheus stores metrics only. Without an OTLP collector there is no
distributed tracing across the webhook -> queue -> processing path.

---

## Security

### HTTPS & Proxy

The application assumes HTTPS is terminated at a reverse proxy (Nginx, Traefik, API Gateway). Use:

```bash
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

The proxy must set:
- `X-Forwarded-For` (client IP)
- `X-Forwarded-Proto` (scheme, must be `https`)
- `X-Forwarded-Host` (hostname)

### Redis Security

**Critical:** Redis caches computed permissions. Without authentication:
```bash
# Unsafe - anyone on the network is admin
REDIS_CONNECTION_STRING=redis.company.com:6379
```

**Recommended:**
```bash
# With password
REDIS_CONNECTION_STRING=redis.company.com:6379,password=YourStrongPassword
```

Also configure Redis with `--requirepass` and disable the `FLUSHALL` and `CONFIG` commands.

### Credential Protection

- **VCS tokens, tracker tokens:** Encrypted at rest using **ASP.NET Core Data Protection**, stored in the same database as the encryption keys themselves. **Critical:** Without a certificate (`DataProtection__CertificatePath`), keys are unencrypted in the database, and a dump + restore is equivalent to storing tokens in plaintext. Configuration fails fast outside Development unless a certificate is provided or `DataProtection__AllowUnprotectedKeys=true` is explicitly set.
- **Do not put secrets in logs:** The app avoids logging token values
- **Do not commit `.env`:** Add to `.gitignore`

---

## Performance Tuning

### Database Connection Pool

EF Core's connection pool is automatically configured. For production, tune in the connection string:
```
Server=...;Max Pool Size=100;Min Pool Size=10;
```

### RabbitMQ Concurrency

Rebus handlers run concurrently, across a pool of workers each pulling from the input queue. Both
are configurable:

| Variable | Default | Meaning |
|---|---|---|
| `Queue__Workers` | 4 | Worker threads, each running one message at a time |
| `Queue__PrefetchCount` | 16 | Messages prefetched from RabbitMQ, and the max parallelism |

### Redis Connection Pool

StackExchange.Redis automatically handles pooling. No explicit configuration needed.

---

## Monitoring Checklist

| Item | How to Check | What to Monitor |
|---|---|---|
| **Database health** | `GET /health/ready` | 503 if DB unavailable |
| **RabbitMQ health** | `GET /health/ready` | 503 if queue unavailable |
| **Redis health** | `GET /health/ready` | 503 if cache unavailable |
| **Disk space** | `df -h` on host | Archive DB grows ~3.6M rows/year at 10K tasks/day |
| **Permission cache** | `redis-cli DBSIZE` | Growing -> check for memory leaks |
| **Active connections** | Logs + DB metrics | Should stabilize after initial load |

---

## Environment Variable Template

```bash
# Database
ConnectionStrings__Default=Server=sqlserver;Database=Echelon;User Id=sa;Password=MyPassword;TrustServerCertificate=true;
ConnectionStrings__Archive=Server=sqlserver;Database=EchelonArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;

# RabbitMQ
Queue__Username=guest
Queue__Password=guest
Queue__Host=rabbitmq
Queue__Port=5672

# Redis
Redis__ConnectionString=redis:6379

# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=https://0.0.0.0:443
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# Archival
Archiving__Enabled=true
Archiving__ArchiveAfterDays=90
Archiving__RunAtUtcHour=2

# Task Sync
TaskReconciliation__Enabled=true
TaskReconciliation__IntervalMinutes=30

# Authorization
Authorization__BootstrapAdminObjectIds=

# Observability (optional)
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
LOGLEVEL_Echelon=Information
```

---

## See Also

- [Getting Started](getting-started.md) - How to set up locally
- [Architecture](architecture.md) - System design
- [Operations](operations.md) - Deployment and monitoring
