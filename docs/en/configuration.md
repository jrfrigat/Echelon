# Release Orchestrator - Configuration

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
- **Example:** `Server=localhost;Database=ReleaseOrchestrator;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **If missing:** `InvalidOperationException` on startup — "ConnectionStrings:Default is required"
- **Retry on transient errors:** Enabled (SQL Server timeout, deadlock → automatic retry, exponential backoff)

**`ConnectionStrings__Archive`** (REQUIRED)
- **What:** Connection string for the archive database (historical data >90 days old)
- **Format:** SQL Server connection string
- **Example:** `Server=localhost;Database=ReleaseOrchestratorArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **If missing:** `InvalidOperationException` on startup
- **Note:** Can be the same SQL Server instance as Default, but separate database, or entirely separate server

### Message Queue (RabbitMQ)

**`Queue__Username`** (REQUIRED)
- **What:** RabbitMQ authentication username
- **Example:** `guest`
- **If missing:** Rebus fails on bus startup
- **Security note:** Never use `guest` in production

**`Queue__Password`** (REQUIRED)
- **What:** RabbitMQ authentication password
- **If missing:** Rebus connection fails
- **Security note:** Use strong password in production

**`Queue__Host`** (REQUIRED)
- **What:** Hostname or IP of RabbitMQ broker
- **Example:** `rabbitmq.company.com` or `localhost`
- **If missing:** DNS resolution fails, Rebus startup fails
- **Default:** Typically derived from Docker service name or localhost

**`Queue__Port`** (REQUIRED)
- **What:** RabbitMQ AMQP port
- **Example:** `5672` (standard)
- **Default:** 5672
- **If missing or invalid:** Connection refused

### Cache (Redis)

**`Redis__ConnectionString`** (REQUIRED)
- **What:** Redis connection string for permission caching
- **Format:** `{host}:{port}` or `{host}:{port},password={password}`
- **Example:** `redis.company.com:6379` or `localhost:6379,password=MySecurePassword`
- **If missing:** `InvalidOperationException` on startup
- **Important:** Permissions are cached here; without Redis, every API call queries the database. **Do not expose Redis to untrusted networks** — cached permissions are not validated on every request.

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
- **Default:** `true` (runs in every pod)
- **If false:** No archival happens; operational DB grows unbounded
- **Note:** No leader election — archival runs in all replicas (idempotent)

**`Archiving__CutoffDays`**
- **What:** Age threshold for archival (days)
- **Example:** `90`
- **Default:** 90
- **Behavior:** Tasks/MRs/plans closed >90 days ago are moved to archive DB

**`Archiving__BatchSize`**
- **What:** Number of records to archive in one batch
- **Example:** `1000`
- **Default:** 1000
- **Note:** Larger batches = fewer DB round-trips, but more lock contention

**`Archiving__RunIntervalMinutes`**
- **What:** How often the archive service runs
- **Example:** `60`
- **Default:** 60 (hourly)

### Task Reconciliation Service

**`TaskReconciliation__Enabled`** (section: `TaskReconciliation`)
- **What:** Whether to periodically sync task dependencies from tracker
- **Valid values:** `true`, `false`
- **Default:** `true`
- **If false:** Task dependencies are only loaded when tasks are explicitly created or status-changed

**`TaskReconciliation__RunIntervalMinutes`**
- **What:** How often to sync open tasks
- **Example:** `30`
- **Default:** 30
- **Behavior:** Every N minutes, fetch open task dependencies from all trackers

**`TaskReconciliation__BatchSize`**
- **What:** Tasks fetched per tracker per run
- **Example:** `100`
- **Default:** 100

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

Configured when adding a VCS Connection in Admin UI:
- **API URL** — endpoint of GitLab or other VCS
- **Access Token** — encrypted at rest
- **Ready-for-Deploy Label** — (optional) label name marking MRs ready for deployment

### Tracker Provider Settings

Configured when adding a Tracker Connection:
- **API URL** — endpoint of Yandex Tracker or other tracker
- **Access Token** — encrypted at rest
- **Organization ID** — (Yandex Tracker only) numeric org ID
- **Provider-Specific Settings** — opaque JSON stored in `TrackerConnection.ProviderSettingsJson`

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

Release Orchestrator relies on an external OIDC provider. Configuration is typically in web application configuration (e.g., `appsettings.json` or Azure AD in admin portal), not environment variables.

**Key claims used:**
- `oid` — Unique user identifier (required)
- `name` — Display name (optional)
- `email` — Email address (optional)

Ensure your OIDC provider includes `oid` in ID tokens.

---

## Logging & Observability

### Logging

**Destination:** Console (JSON format in production)

**Level control:** Set via standard .NET Core:
```bash
LOGLEVEL_ReleaseOrchestrator=Debug
LOGLEVEL_Microsoft.EntityFrameworkCore=Warning
```

### Structured Logging

All operational events are logged as JSON. Example:
```json
{
  "timestamp": "2025-01-15T10:30:00Z",
  "level": "Information",
  "logger": "ReleaseOrchestrator.Application.ReleasePlanning.ReleasePlanner",
  "message": "Release plan recalculated",
  "planId": "550e8400-e29b-41d4-a716-446655440000",
  "stageCount": 5,
  "conflictCount": 2
}
```

### OpenTelemetry

**OTEL_EXPORTER_OTLP_ENDPOINT** (optional)
- **What:** gRPC endpoint of an OpenTelemetry collector
- **Example:** `http://localhost:4317`
- **If set:** Traces and metrics are exported to the collector
- **If not set:** No tracing (app does not emit traces locally)

**Known limitation:** The app instruments async paths (webhooks → queue → processing) but without OTEL, observability is limited to logs and `/health` endpoints.

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
# Unsafe — anyone on the network is admin
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
| **Permission cache** | `redis-cli DBSIZE` | Growing → check for memory leaks |
| **Active connections** | Logs + DB metrics | Should stabilize after initial load |

---

## Environment Variable Template

```bash
# Database
ConnectionStrings__Default=Server=sqlserver;Database=ReleaseOrchestrator;User Id=sa;Password=MyPassword;TrustServerCertificate=true;
ConnectionStrings__Archive=Server=sqlserver;Database=ReleaseOrchestratorArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;

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
Archiving__CutoffDays=90
Archiving__RunIntervalMinutes=60

# Task Sync
TaskReconciliation__Enabled=true
TaskReconciliation__RunIntervalMinutes=30

# Authorization
Authorization__BootstrapAdminObjectIds=

# Observability (optional)
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
LOGLEVEL_ReleaseOrchestrator=Information
```

---

## See Also

- [Getting Started](getting-started.md) - How to set up locally
- [Architecture](architecture.md) - System design
- [Operations](operations.md) - Deployment and monitoring
