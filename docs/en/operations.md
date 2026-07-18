# Release Orchestrator - Operations & Deployment

> [Русская версия ->](../ru/operations.md) - [← Back to docs](../README.md)

---

## Overview

This document covers production deployment, monitoring, and operational concerns for Release Orchestrator.

**⚠️ Warning:** This application has **never been deployed or run in a live environment**. The following guidance is based on code analysis, not production experience. Treat recommendations as starting points; test thoroughly in staging before production.

---

## Deployment

### Prerequisites

- **Kubernetes 1.20+** or Docker Swarm (or standalone server)
- **Microsoft SQL Server 2019+** (can be managed by your cloud provider)
- **RabbitMQ 3.8+** (or cloud managed service)
- **Redis 6.0+** (or cloud managed service)
- **Reverse proxy** (Nginx, Traefik, API Gateway) with HTTPS termination
- **OpenID Connect provider** (Azure AD, Keycloak, Auth0, etc.)

### Docker Images

The application includes a `docker-compose.yml` for local development. For production:

```dockerfile
# Multi-stage build (example)
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS builder
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.8
WORKDIR /app
COPY --from=builder /app .
EXPOSE 5000
ENTRYPOINT ["dotnet", "ReleaseOrchestrator.Web.dll"]
```

**Base image:** `mcr.microsoft.com/dotnet/aspnet:10.0.8`
**SDK:** `mcr.microsoft.com/dotnet/sdk:10.0.300`

**Note:** Images were not built in development environment (registry blocked by proxy). Verify image availability in your environment before deploying.

### Kubernetes Deployment Example

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: release-orchestrator
spec:
  replicas: 3
  selector:
    matchLabels:
      app: release-orchestrator
  template:
    metadata:
      labels:
        app: release-orchestrator
    spec:
      containers:
      - name: web
        image: your-registry/release-orchestrator:latest
        ports:
        - containerPort: 5000
        env:
        - name: ConnectionStrings__Default
          valueFrom:
            secretKeyRef:
              name: db-credentials
              key: connection-string
        - name: ConnectionStrings__Archive
          valueFrom:
            secretKeyRef:
              name: db-credentials
              key: archive-connection-string
        - name: Queue__Username
          valueFrom:
            secretKeyRef:
              name: queue-credentials
              key: username
        - name: Queue__Password
          valueFrom:
            secretKeyRef:
              name: queue-credentials
              key: password
        - name: Redis__ConnectionString
          valueFrom:
            secretKeyRef:
              name: cache-credentials
              key: connection-string
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: ASPNETCORE_FORWARDEDHEADERS_ENABLED
          value: "true"
        livenessProbe:
          httpGet:
            path: /health
            port: 5000
          initialDelaySeconds: 10
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 5000
          initialDelaySeconds: 5
          periodSeconds: 5
        resources:
          requests:
            cpu: 100m
            memory: 256Mi
          limits:
            cpu: 500m
            memory: 1Gi

---
apiVersion: v1
kind: Service
metadata:
  name: release-orchestrator
spec:
  type: ClusterIP
  ports:
  - port: 80
    targetPort: 5000
  selector:
    app: release-orchestrator
```

---

## Health Checks

### Liveness (`/health`)

Indicates whether the process is running and can handle requests.

```bash
curl http://localhost:5000/health
# Response: 200 OK, empty body
```

**Use case:** Kubernetes liveness probe, process restart detection

### Readiness (`/health/ready`)

Indicates whether all dependencies (DB, RabbitMQ, Redis) are available.

```bash
curl http://localhost:5000/health/ready
# If healthy: 200 OK
# If any dependency unavailable: 503 Service Unavailable
```

**Response body example (unavailable DB):**
```json
{
  "status": "Unhealthy",
  "checks": {
    "Database": {
      "status": "Unhealthy",
      "description": "Could not connect to SQL Server"
    },
    "RabbitMQ": {
      "status": "Healthy"
    },
    "Redis": {
      "status": "Healthy"
    }
  }
}
```

**Use case:**
- Kubernetes readiness probe (pod removed from load balancer if not ready)
- Deployment: wait for 200 before marking healthy
- Monitoring: alert if stays 503 for >5 minutes

---

## Monitoring

### Metrics to Watch

| Metric | How to Check | Warning Threshold | Action |
|--------|---|---|---|
| **API response time** | Logs, APM tool | >500ms p99 | Check database slow queries |
| **Queue depth (RabbitMQ)** | RabbitMQ admin, logs | >10,000 messages | Scale consumers or investigate stall |
| **Database connections** | `SELECT COUNT(*) FROM sys.dm_exec_sessions` | >80 (if max 100) | Identify long-running queries, scale connections |
| **Archive job runtime** | Logs | >30 minutes (if hourly) | Investigate slow deletes, consider smaller batches |
| **Redis memory** | `redis-cli INFO memory` | >80% of limit | Investigate memory leaks, purge old cache entries |
| **Permission cache hit rate** | Monitor hits vs. DB queries | <80% | Consider cache TTL tuning |
| **Active users** | Azure AD sign-in logs, app metrics | N/A | Baseline for capacity planning |

### Key Log Patterns

Look for these in logs:

- **"Database connection failed"** — Check SQL Server availability
- **"RabbitMQ connection failed"** — Check RabbitMQ, network, credentials
- **"Release plan recalculation failed"** — Check logs for graph algorithm issues
- **"Archive batch failed"** — Check foreign key constraints, disk space
- **"Permissions cache error"** — Check Redis availability

### Metrics with Prometheus

Both hosts expose metrics at `/metrics` in the Prometheus text format, on by default and needing no
collector — Prometheus scrapes them directly. What is exported:

- **ASP.NET Core** — request rate, duration and active requests (Core's API, the Ingress webhooks)
- **.NET runtime** — GC, heap, thread pool, exception count, CPU and working set
- **HTTP client** — outbound calls to GitLab and the tracker
- **Rebus** — message send, receive and handle timings

Turn the endpoint off with `Prometheus__Enabled=false`. It is anonymous and exempt from the rate
limiter, like the health probes, so a scrape reaches the process regardless of auth and does not
spend the API request budget.

A ready-to-run stack lives alongside the compose files:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
# Prometheus at http://localhost:9090 (already scraping core and ingress)
# Grafana at http://localhost:3000 (Prometheus wired as the default data source)
```

The scrape targets are in `observability/prometheus.yml`.

### Traces with OpenTelemetry

Traces go out over OTLP when a collector is configured (`OTEL_EXPORTER_OTLP_ENDPOINT` set); metrics
are pushed there too, in addition to being scrapeable:

```bash
# Example: send traces and metrics to a collector
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
dotnet run --project src/ReleaseOrchestrator.Web
```

Traces capture:
- Webhook ingestion (Ingress)
- Message queue send and handling (Rebus propagates W3C trace context across RabbitMQ, so the
  Ingress → queue → Core path is one trace)
- Database operations (EF Core)
- Release plan calculations

**Limitation:** Prometheus stores metrics only. Without an OTLP collector there is no distributed
tracing — a Prometheus counter tells you *that* webhook processing slowed, not *which* span.

---

## Scaling

### Horizontal Scaling (Multiple Replicas)

All components are stateless:

- **Web pods:** Automatically scale behind load balancer
- **RabbitMQ:** Requires cluster setup (see RabbitMQ docs)
- **Database:** Shared among all pods (SQL Server replication if desired)
- **Redis:** Shared cache (Redis Cluster if scaling beyond single instance)
- **Archive service:** Runs in every pod (idempotent, no coordination needed)

**Concurrency note:** Archive service has no leader election. Multiple pods run simultaneously. Correctness is maintained via idempotent insert + retry; performance may suffer due to lock contention.

### Database Connection Pooling

EF Core automatically manages pool (default 100 connections). Monitor:

```sql
SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID('ReleaseOrchestrator');
```

If approaching limit, increase in connection string:

```
Server=...;Max Pool Size=200;
```

### RabbitMQ Tuning

Current configuration (from code):
- Consumers per deployment: Default (check `Program.cs`)
- Retry policy: Exponential backoff (initial 0s, max 1h)
- Dead letter queue: Not configured

**Recommendation for production:**
- Enable RabbitMQ Dead Letter Queue (DLQ) for failed messages
- Set up monitoring on DLQ depth
- Configure consumer concurrency based on workload (3-5 per pod recommended)

---

## Maintenance

### Database Backups

**Frequency:** Daily (adjust based on criticality)
**Scope:** Both `ReleaseOrchestrator` and `ReleaseOrchestratorArchive` databases

**Example (SQL Server):**
```bash
sqlcmd -S your-server -U sa -P password -Q "
BACKUP DATABASE ReleaseOrchestrator 
TO DISK = '/var/opt/mssql/backup/ReleaseOrchestrator.bak'
WITH FORMAT, COMPRESSION;
"
```

### Archive Database Maintenance

Archive DB grows ~3.6M rows/year at 10K tasks/day throughput. After 2+ years, consider:

1. **Index maintenance:** Rebuild indexes on `TaskItem`, `MergeRequest`
2. **Clean up old archives:** Optionally delete records >2 years (not implemented in code)
3. **Separate storage:** Archive DB can move to cheaper tier (cold storage)

### Periodic Tasks

| Task | Frequency | Owner | Notes |
|---|---|---|---|
| **Archival** | Hourly (configurable) | Automatic (Archive service) | Moves closed tasks/MRs >90 days old |
| **Task sync** | Every 30 minutes (configurable) | Automatic (Task Reconciliation) | Loads open task dependencies |
| **Permission cache invalidation** | On-demand | Automatic (permission changes) | No TTL — invalidate on change only |
| **Health check** | Continuous | Kubernetes/monitoring | `/health` and `/health/ready` |

---

## Known Limitations & Workarounds

### RabbitMQ Broker Unavailability Handling

If RabbitMQ is down, webhooks return 503 Service Unavailable with a Retry-After header. Most VCS systems (e.g. GitLab) respect this and retry the webhook delivery. No event buffering is implemented. Recommendation:

- Implement buffering in reverse proxy or API gateway
- Or: Accept event loss and monitor RabbitMQ health closely

### No Leader Election for Archive

Archive service runs in every pod (duplicate work). Recommendation:

- Monitor for excessive locking on archive tables
- If performance degrades, manually scale to single archive pod (requires deployment config)
- Consider implementing leader election using Redis (not in codebase)

### Limited Observability Without OTEL

Async paths have poor visibility. Recommendation:

- Enable OTEL + Jaeger/DataDog/similar
- Or: Monitor via RabbitMQ admin + database query logs
- Set up alerts for `/health/ready` returning 503

### No PostgreSQL Support

Only SQL Server is supported (Npgsql code removed for clarity). Porting would require:
- EF Core migration assembly for PostgreSQL
- Testing against PostgreSQL-specific SQL (filtered unique index, rowversion handling)

---

## Security Checklist

- [ ] HTTPS enabled (reverse proxy with valid certificate)
- [ ] `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` set
- [ ] Redis requires password (`Redis__ConnectionString` includes password)
- [ ] RabbitMQ credentials strong (not default guest/guest)
- [ ] SQL Server connections use strong SA password + firewall
- [ ] OIDC credentials stored in secure secret management (not `.env` files)
- [ ] `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` empty in production (or removed)
- [ ] API rate limiting enabled (consider reverse proxy)
- [ ] Audit logging enabled (check logs for permission changes)
- [ ] Data Protection keys backed up (stored in `DataProtectionKeys` table)

---

## Disaster Recovery

### Data Loss Scenarios

| Scenario | Impact | Recovery |
|---|---|---|
| **SQL Server database deleted** | Complete data loss | Restore from backup |
| **Redis cache cleared** | Permissions re-computed on next request (slow) | No action needed (cache will refill) |
| **RabbitMQ messages lost** | Webhook events lost (no retry) | Manual re-trigger from VCS/tracker |
| **Active plan lost** | Users see no plan until auto-recalculation | Manually import YAML backup |

### Backup Strategy

```bash
# Weekly full backup
sqlcmd -S server -U sa -P pwd -Q "
BACKUP DATABASE ReleaseOrchestrator 
TO DISK = '/mnt/backups/full_$(date +%Y%m%d).bak' 
WITH FORMAT, COMPRESSION;
"

# Daily incremental (if using full backup model)
BACKUP DATABASE ReleaseOrchestrator 
TO DISK = '/mnt/backups/incr_$(date +%Y%m%d).bak' 
WITH DIFFERENTIAL;
```

### Restore Procedure

```bash
# Restore latest full backup
RESTORE DATABASE ReleaseOrchestrator 
FROM DISK = '/mnt/backups/full_20250115.bak' 
WITH REPLACE;

# Restore latest incremental (if applicable)
RESTORE DATABASE ReleaseOrchestrator 
FROM DISK = '/mnt/backups/incr_20250117.bak' 
WITH RECOVERY;
```

---

## Support & Troubleshooting

### Common Issues

**Issue:** API returns 500 with "Database connection timeout"
- **Cause:** SQL Server overloaded or unreachable
- **Check:** `SELECT COUNT(*) FROM sys.dm_exec_sessions`, network connectivity
- **Fix:** Increase connection pool, scale database, restart container

**Issue:** Webhooks return 503 "RabbitMQ unavailable"
- **Cause:** RabbitMQ down or network issue
- **Check:** RabbitMQ admin UI (port 15672), network policies
- **Fix:** Restart RabbitMQ, check firewall rules, scale if queue depth high

**Issue:** Plan doesn't update after creating MR
- **Cause:** Task not linked, MR status not ReadyForDeploy, or sync not run
- **Check:** `/health/ready` (should be 200), check logs for sync errors
- **Fix:** Manually check branch name (must include task key), verify label config

---

## See Also

- [Architecture](architecture.md) - System design and components
- [Configuration](configuration.md) - All environment variables
- [Getting Started](getting-started.md) - Local setup

---

## Useful Links

- [.NET 10 Deployment Guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [SQL Server Backup & Restore](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/back-up-and-restore-of-sql-server-databases)
- [RabbitMQ Clustering](https://www.rabbitmq.com/clustering.html)
- [Redis Cluster](https://redis.io/docs/management/replication/)
- [Kubernetes Best Practices](https://kubernetes.io/docs/concepts/configuration/overview/)
