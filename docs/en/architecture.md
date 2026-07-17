# Release Orchestrator - Architecture

> [Русская версия ->](../ru/architecture.md) - [← Back to docs](../README.md)

---

## Overview

Release Orchestrator transforms a set of merge requests into an ordered, staged deployment plan. The plan is built from two independent sources:

- **Task dependencies** from a tracker (if TASK-2 depends on TASK-1, then MR for TASK-1 deploys first)
- **Stack dependencies** between repositories (e.g., DB → Backend → Frontend)

The system combines these into a **directed acyclic graph** (DAG), resolves conflicts by dropping edges, and uses **Kahn's topological sort** to produce deployment stages. Each stage contains MRs that can deploy in parallel.

**Core insight:** The product is "input + edges + topological sort". Everything else — manual editing, YAML import/export, permissions, archival — serves these three things.

---

## Architecture Layers

The system follows a modular monolith pattern: one service with strict layering to allow future separation.

```
┌─────────────────────────────────────────┐
│  PWA (Blazor WebAssembly)               │  Frontend
├─────────────────────────────────────────┤
│  Web / Ingress.Webhooks                 │  HTTP boundary
├─────────────────────────────────────────┤
│  Application Layer                      │  Business logic
│  - VCS Service                          │
│  - Tracker Service                      │
│  - Release Planner                      │
│  - Authorization                        │
├─────────────────────────────────────────┤
│  Providers Layer                        │  Abstraction boundaries
│  - IVcsProviderAdapter                  │
│  - ITrackerProviderAdapter              │
├─────────────────────────────────────────┤
│  Infrastructure Layer                   │  External integration
│  - EF Core DbContext                    │
│  - Provider Implementations (GitLab,    │
│    Yandex Tracker)                      │
│  - RabbitMQ Consumers                   │
│  - Archive Service                      │
│  - Redis Cache                          │
├─────────────────────────────────────────┤
│  Core Domain Layer                      │  Entities (no I/O)
│  - TaskItem, MergeRequest, ReleasePlan  │
│  - ReleasePlanGraph (pure algorithm)    │
└─────────────────────────────────────────┘
```

### Key Principle: Dependency Direction

Dependencies point **inward only**. The Core layer has no external dependencies beyond the C# standard library. Each outer layer only depends on the layers inside it:

- **Web** depends on Application, Infrastructure, Core
- **Infrastructure** depends on Application, Core  
- **Application** depends on Core
- **Core** depends on nothing

This allows extracting the Release Planning service to a separate microservice (via message queue) without coupling.

---

## Component Architecture

### 1. Webhook Ingress (`Ingress.Webhooks`)

**Role:** Receive external events and publish normalized messages.

**Technology:** ASP.NET Core Minimal API, .NET 10

**Responsibilities:**
- Receive webhooks from GitLab, Yandex Tracker, and other VCS/tracker systems
- Validate each webhook's signature (token-based)
- Normalize provider-specific payloads into universal messages:
  ```csharp
  public record MrOpened(Guid MrId, string ExternalMrId, Guid RepositoryId, 
                         string SourceBranch, string TaskExternalId);
  public record TaskCreated(Guid TaskId, string ExternalId, string Title);
  public record TaskStatusChanged(string ExternalId, string NewStatus);
  ```
- Publish messages to RabbitMQ
- Return `200 OK` immediately (do not block on business processing)

**Scaling:** Stateless, horizontally scalable behind a reverse proxy.

**Note:** Event buffering is **not implemented**. If RabbitMQ is down, the webhook returns 503 Service Unavailable with a Retry-After header. This signals senders that the failure is temporary and the webhook should be retried, preventing permanent event loss. Recommendations in [Configuration](configuration.md).

### 2. Core Service

**Single service** with modular internals: no separate microservices per provider or function. Reasoning:

- **Transactional consistency:** Building a plan requires simultaneously accessing task dependencies, MR states, and stack definitions. Distributed queries would complicate this.
- **Tight coupling by design:** The graph algorithm is pure (no I/O), but reading data and writing the plan is a tight loop.
- **Future flexibility:** The Release Planning module can be extracted to a separate service consuming messages from RabbitMQ, without changing the architecture.

#### 2.1 Application Layer

**VCS Service** (`Application/Vcs`): Orchestrates repository, MR, and VCS connection management. Not tied to a specific provider.

**Tracker Service** (`Application/Tracker`): Loads task definitions and dependencies from tracker(s). Triggers task sync via the message queue.

**Release Planner** (`Application/ReleasePlanning`): 
- Reads current MRs, tasks, and stack definitions
- Builds a graph using `ReleasePlanGraph` (pure algorithm, no EF)
- Resolves cycles by edge dropping (priority: Soft → Task → Hard dependencies)
- Runs Kahn's topological sort to produce stages
- Saves plan and conflict log

**Authorization**: Claim-based permissions (no roles). Claims stored in `PermissionClaims` table, mapped to AD/LDAP groups or individual user overrides.

#### 2.2 Infrastructure Layer

**Persistence** (`Infrastructure/Persistence`):
- `AppDbContext`: Operational data (current MRs, tasks, plans, connections)
- `ArchiveDbContext`: Historical data (closed tasks/MRs, plans older than 90 days)
- EF Core with SQL Server, retry-on-transient-failure enabled
- Migrations in separate assembly `ReleaseOrchestrator.Migrations.MsSql`

**Providers** (`Infrastructure/Providers`):
- `VcsProviderFactory`: Resolves connection type to adapter instance (keyed DI)
- `TrackerProviderFactory`: Same pattern for trackers
- Providers are registered at composition time (no plugin discovery)

**Queue** (`Infrastructure/Queue`):
- Rebus as the message bus
- Consumers for: MR opened/status changed, task created/status changed, task sync, plan recalculation
- Coalescing: redundant recalculation requests are deduplicated before `SaveChangesAsync`

**Archive Service** (`Infrastructure/Archive`):
- Runs as a hosted service (background worker in every pod)
- Moves tasks/MRs closed >90 days ago to archive DB
- Idempotent: runs in every replica (no leader election)
- Phase order: old plans → old MRs → old tasks (foreign key safety)

### 3. Release Plan Graph Algorithm

**File:** `Application/ReleasePlanning/ReleasePlanGraph.cs`

**Principle:** Pure algorithm, no EF Core, no I/O. 100% testable.

**Inputs:**
- Merge requests with status (Opened, ReadyForDeploy, etc.)
- Tasks with closure status
- Task dependencies (DependentTaskId → DependsOnTaskId)
- Stack dependencies (FromStackId → ToStackId, Hard or Soft type)

**Algorithm:**

1. **Build edges:**
   - For each MR with a ReadyForDeploy status:
     - If MR has an associated task:
       - Add outgoing edges to all MRs of predecessor tasks (task dependencies)
   - For each MR:
     - Add outgoing edges to MRs in dependent stacks (stack dependencies)

2. **Detect and resolve cycles:**
   - Compute strongly connected components (SCC)
   - For each SCC with >1 node:
     - Drop edges by priority: Soft dependencies first, then task edges, then hard stack edges
     - Repeat until acyclic
     - Log dropped edges in `ReleasePlan.ConflictsJson`

3. **Topological sort (Kahn's algorithm):**
   - For acyclic graph, compute in-degree of each MR
   - Repeatedly extract nodes with in-degree 0 → stage N
   - Decrement in-degree of descendants
   - Continue until all nodes are staged

4. **Output:**
   - `ReleaseStage[]` ordered by sequence
   - Each stage contains `StageItem[]` (MRs that can deploy in parallel)

**Key fixes applied (from audit):**
- Task dependency mapping was inverted (fixed via EF navigation reversal)
- Multiple MRs per task were handled incorrectly (now all are ordered)
- Soft dependencies were ignored (now applied, with lower conflict priority)
- Cycles and all downstream nodes were silently grouped (now conflict-logged)

---

## Data Model

### Operational Database (AppDbContext)

**VCS Connections:**
- `VcsConnection`: id, name (unique, used in YAML), type (GitLab, ...), URL, encrypted token, ReadyForDeployLabel

**Tracker Connections:**
- `TrackerConnection`: id, name, type (YandexTracker, ...), URL, encrypted token, org ID

**Repositories & Tasks:**
- `Repository`: id, name, external ID (path in VCS), connection ID
- `TaskItem`: id, external ID (e.g., "TASK-123"), title, status, closed at, tracker connection
- `TaskDependency`: dependent task → predecessor task

**Merge Requests:**
- `MergeRequest`: id, external ID (iid in VCS), source/target branches, repo, status (Opened, ReadyForDeploy, Merged, Closed), created/merged/closed timestamps
- Status can be set manually via API (marked `IsStatusManual`) — webhooks don't override manual status
- Linked to task via external key parsed from branch name

**Release Plans:**
- `ReleasePlan`: id, name, version, is active, auto-generated flag, YAML hash, conflicts JSON
- Unique filtered index on `(IsActive=true)` ensures only one active plan
- Atomic swap via transaction: recalc deactivates old auto plans, new one becomes active

**Stacks & Dependencies:**
- `Stack`: id, name (unique)
- `RepositoryStack`: many-to-many (repository in which stacks)
- `StackDependency`: from stack → to stack, Hard (must deploy before) or Soft (preferred order)

**Stages & Items:**
- `ReleaseStage`: id, plan, sequence (order within plan), name (nullable), is manual override
- `StageItem`: id, stage, merge request, manual inclusion flag

**Archive Table:**
- `ArchivedReleasePlan`: Snapshot of plan after stages are built (for audit trail)

### Archive Database (ArchiveDbContext)

Same structure as operational DB, but:
- Holds only data closed >90 days ago
- Written to separately (no shared transaction)
- Idempotent insert (checked if already exists)

---

## Event Flow: Webhook to Deployment Plan

```
1. VCS/Tracker sends webhook
   ↓
2. Ingress.Webhooks receives, validates token
   ↓
3. Publishes normalized message to RabbitMQ
   ↓
4. Consumer receives message
   ↓
5. Updates database (MR status, task, task dependencies)
   ↓
6. Publishes "ReleasePlanRecalculationRequested" if needed
   ↓
7. ReleasePlanRecalculationConsumer:
   - Reads current MRs, tasks, stacks
   - Calls ReleasePlanGraph.BuildAsync()
   - Saves new plan, deactivates old auto plan
   - Logs conflicts if edges were dropped
   ↓
8. PWA fetches new plan via BFF API
   ↓
9. UI displays stages
```

**Coalescing:** If multiple recalculation requests arrive in quick succession, the consumer coalesces them—only one recalculation runs, saving expensive reads.

---

## Provider Architecture

Providers are registered at **compile time**, not runtime. Each provider implements two interfaces:

### IVcsProviderAdapter

```csharp
public interface IVcsProviderAdapter
{
    Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct);
}
```

Returns an `IVcsProvider` after:
- Validating endpoint + credentials
- Detecting server version (if self-hosted)
- Initializing any state needed for subsequent calls

This separation prevents "not initialized yet" state in the provider itself.

### ITrackerProviderAdapter

```csharp
public interface ITrackerProviderAdapter
{
    Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct);
}
```

Same pattern for task trackers.

**Registering a provider:**
1. Create project: `ReleaseOrchestrator.Providers.NewVcs` or `ReleaseOrchestrator.Providers.NewTracker`
2. Implement adapter interface
3. Add to `InfrastructureExtensions.cs`: `services.AddNewVcsProvider();` (one line per provider)

See [Providers](providers.md) for detailed walkthrough.

---

## Observability

### Health Endpoints

- **`GET /health`:** Liveness (process running). Always 200 if reachable.
- **`GET /health/ready`:** Readiness (dependencies initialized). 503 if DB, RabbitMQ, or Redis are unavailable.

### Logging

- Structured logging to console (JSON format in production)
- Optional Seq integration via configuration (not included by default)

### Metrics & Tracing

**OpenTelemetry support:** Framework plumbing is in place; no Prometheus exporter included. Requires setting `OTEL_EXPORTER_OTLP_ENDPOINT` and a collector to receive traces.

**Known limitation:** Async path (Ingress → RabbitMQ → Core) has poor visibility without OTLP. Recommendation: use `/health/ready` polling + RabbitMQ admin UI + database query audit.

---

## Security Architecture

All credentials are **encrypted at rest** using **ASP.NET Core Data Protection**. Keys are stored in the database with application-specific isolation.

**Important:** Without a certificate to encrypt the keys themselves (`DataProtection__CertificatePath`), this provides **no real security**. A database dump, backup, or restore contains both the encrypted tokens *and* the unencrypted keys—attackers can decrypt tokens trivially. This is enforced at startup in non-Development environments: the app refuses to run without a certificate or an explicit opt-out. Development is exempt for convenience; production deployments must configure a certificate.

### Authentication

- OpenID Connect via external provider (AD, OIDC-compatible system)
- User ID (`oid` claim) is the unique identifier
- Email and other claims are optional

### Authorization

- Claim-based, not role-based
- Claims like `release.plan.approve`, `release.plan.view`, `config.edit` stored in `PermissionClaims`
- Mapped to AD groups via `GroupPermissionMapping` or individual overrides via `UserPermissionOverride`

**Bootstrap:** Fresh installation needs at least one admin. See [Getting Started](getting-started.md).

### HTTPS

- Assumed to be terminated at reverse proxy (Nginx, Traefik, etc.)
- Use `ForwardedHeaders` middleware; configure via `ASPNETCORE_FORWARDEDHEADERS_ENABLED`

---

## Deployment Topology

**Single Pod / Process:**
```
PWA browser → BFF API (Web) ←→ Core Logic (Application) ←→ AppDbContext ←→ SQL Server
                                                              ↓
                                                          ArchiveDbContext ↔ SQL Server (Archive)
          
          Ingress (separate pod) → RabbitMQ ↔ Core (Rebus handler)
```

**Multiple Replicas (Kubernetes, Docker Swarm, etc.):**
```
             PWA browser
                  ↓
         ┌─────┬─────┬─────┐
         ↓     ↓     ↓     ↓
      Web1  Web2  Web3  Web4  (behind load balancer)
         └─────┬─────┬─────┘
               ↓
         AppDbContext (shared)
               ↓
         SQL Server (Leader-aware BFF, all Webs)
               
   Ingress1 ───┐
   Ingress2 ───┼─→ RabbitMQ ←── Core1, Core2, Core3 (all Cores consume same queue)
   Ingress3 ───┘   (Competing consumers, Rebus handles)
               
         Archive service in every Core pod (gated on Redis lease)
```

**Distributed lease (not consensus):** Archive service runs in every pod but is gated on a Redis-backed distributed lease. The lease uses `SET key owner NX PX ttl` with owner validation and renewal. In any given cycle, only one pod holds the lease and runs archiving. This is **not a consensus algorithm**: a single Redis is a single point of failure, and under a network partition two replicas could briefly believe they hold the same lease. However, that is acceptable here because archiving is idempotent — double-running it once is the same cost as the normal case (which used to run on every replica before the lease). For tasks where double-running is a correctness bug, a fencing token is required. If Redis is unavailable, the lease cannot be acquired, so the pass is skipped (fail-closed), preventing thundering herd on the next cycle.

---

## Known Limitations

**Not tested in live environment:**
- App has never run against live GitLab or Yandex Tracker
- Docker images have not been built (registry is blocked in development environment)
- Database migrations have not been applied to a real database
- Behavior under production load is unknown

See [Current State Audit](../issues/001-current-state.md) §4 for full details on what has not been validated.

---

## See Also

- [Configuration](configuration.md) - Environment variables and setup
- [Providers](providers.md) - How to add a new VCS or tracker
- [Operations](operations.md) - Deploying and monitoring
