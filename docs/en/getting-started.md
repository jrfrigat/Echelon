# Echelon - Getting Started

> [Русская версия ->](../ru/getting-started.md) - [← Back to docs](../README.md)

---

## Requirements

- **.NET SDK 10** (to build)
- **SQL Server 2019+** or **PostgreSQL 14+** (operational and archive databases - both providers are supported)
- **RabbitMQ 3.8+** (message bus)
- **Redis 6.0+** (cache for computed permissions)
- **Docker & Docker Compose** (recommended for local development)
- An authentication provider: OpenID Connect (e.g. Azure AD, Keycloak) or the built-in `Local` provider for development

---

## 1. Local Setup with Docker Compose

The easiest way to run locally is with Docker Compose. All required services are included.

### 1.1 Prepare `.env` File

In the project root, create `.env` based on `.env.example`:

```bash
# SQL Server
MSSQL_SA_PASSWORD=YourComplexPassword123!
SA_PASSWORD=YourComplexPassword123!

# RabbitMQ
RABBITMQ_DEFAULT_USER=guest
RABBITMQ_DEFAULT_PASS=guest

# Redis (leave blank for no auth in dev)
REDIS_PASSWORD=

# OpenID Connect (configure your provider, or use Auth:Provider=Local in dev)
OIDC_AUTHORITY=https://your-oidc-provider/.well-known/openid-configuration
OIDC_CLIENT_ID=your-client-id
OIDC_CLIENT_SECRET=your-client-secret
OIDC_REDIRECT_URI=https://localhost:5173/authentication/login-callback

# Echelon
ASPNETCORE_ENVIRONMENT=Development
CONNECTION_STRING_DEFAULT=Server=sqlserver;Database=Echelon;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
CONNECTION_STRING_ARCHIVE=Server=sqlserver;Database=EchelonArchive;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
REDIS_CONNECTION_STRING=redis:6379
QUEUE_USERNAME=guest
QUEUE_PASSWORD=guest
QUEUE_HOST=rabbitmq
QUEUE_PORT=5672
```

**⚠️ IMPORTANT for production:**
- Use strong passwords (not `guest`)
- Enable Redis authentication (`--requirepass`)
- Use HTTPS with proper certificates
- Set `ASPNETCORE_ENVIRONMENT=Production`
- Configure real OIDC credentials

### 1.2 Start Services

```bash
docker-compose up -d
```

Wait for the database to be ready (30-60 seconds):

```bash
docker-compose logs -f sqlserver | grep "Recovery is complete"
```

### 1.3 Apply Database Migrations

The app applies any pending migrations on startup by default (`Database:MigrateOnStartup`), so for a
single-instance or local run you can skip this step. Apply them by hand when you turn that off - e.g.
a multi-replica deployment, where concurrent auto-migration would race, so you run them from an init
container or CI instead:

Migrations live in a provider-specific project (`...Migrations.MsSql` for SQL Server, `...Migrations.Postgres` for PostgreSQL). Pick the one that matches your database, and apply **both** contexts:

```bash
# From project root - operational database
dotnet ef database update --project src/Echelon.Migrations.MsSql \
  --startup-project src/Echelon.Web \
  --context AppDbContext

# Archive database
dotnet ef database update --project src/Echelon.Migrations.MsSql \
  --startup-project src/Echelon.Web \
  --context ArchiveDbContext
```

### 1.4 Bootstrap Admin User

On first run no user has permissions. Use bootstrap admin mode to grant the first admin:

```bash
# Temporary, for this run only
$env:AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS="your-oid-value"

# Then start the app
dotnet run --project src/Echelon.Web
```

**How to find your OID:**
1. Log in with your provider
2. The app creates a user record
3. Check application logs for your `oid` claim
4. Set `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` to that value and restart

Once you log in as bootstrap admin, your user gets full permissions, and you can grant others via **Admin -> Permissions**. Remove `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` for production (fail-safe: while set, anyone matching it becomes admin).

---

## 2. Configure the orchestrator

Everything is configured from the **Administration** menu, roughly in the order below - each step
depends on the ones above it. This is the whole setup; there is no YAML to hand-write.

### 2.1 VCS connections (`Admin -> VCS Connections`)

Connect to each GitLab instance the orchestrator reads merge requests from and deploys.

- **Name** - a label for the connection.
- **Type** - `gitlab-webhook` (GitLab pushes events to the orchestrator's ingress) or `gitlab-poll`
  (the orchestrator polls on an interval you set). Push vs poll is the *type*, not a per-connection toggle.
- **API URL** - e.g. `https://gitlab.company.com`.
- **Access Token** - a token with `api`, `read_repository` scopes (encrypted at rest).
- **Linking rule** - how an incoming merge request is matched to its tracker task: a **task-key source**
  (branch, title, or label) and a **regex pattern**. The default extracts an upper-case key like
  `PROJ-12`. The form shows a **live preview** - type a sample branch/title/label and it shows the key
  the rule would extract, so you can get it right before saving.
- **`gitlab-poll` only** - a **poll interval** (seconds) is added among the settings.

There is no "ready-for-deploy label" field - deploy readiness is configured separately (§2.5).

### 2.2 Tracker connections (`Admin -> Tracker Connections`)

Tasks, their statuses, dependencies and hierarchy come from the tracker.

- **Name**, **API URL** (`https://api.tracker.yandex.net`), **Access Token**.
- **Type** - `yandextracker-webhook` (receives task webhooks) or `yandextracker-poll` (no webhook; open tasks are re-read on a **poll interval** you set).
- **Organization ID** - sent as the `X-Org-Id` header (Yandex Tracker).
- **Closed statuses** - comma-separated status keys that mean a task is *done*; leave blank for the
  defaults (`closed, cancelled, rejected, resolved`).

### 2.3 Repositories (`Admin -> Repositories`)

Register each repository the orchestrator manages.

- **Name**, **External ID** (the VCS path, e.g. `my-group/my-project`), and its **VCS connection**.
- A repository also needs a **deploy strategy** (how it is deployed - merge the MR, or trigger a
  pipeline). This is set as a **deploy target** per environment (§2.6); a rollout step cannot run
  without one.

### 2.4 Default rollout plan (`Admin -> Default Plan`)

The ordering that applies to **every** task - configured once, not per release. There is no single
global plan and no "stacks": you state ordering rules between repositories one pair at a time
(*repository A deploys after repository B*), and the page shows the **waves** they add up to. Each
rule is **Hard** (a real constraint, never dropped) or **Soft** (a preference that yields first if
rules ever conflict). Repositories with no rule deploy together in the first wave.

### 2.5 Readiness rules (`Admin -> Readiness Rules`)

What a merge request must show before it may deploy. A rule is a set of normalized **signals** -
a label (`label:ready-prod`), a merge-request status (`mr-status:merged`), or a pipeline result
(`pipeline:success`) - combined as **AllOf** or **AnyOf**. Create the rules your projects need here;
you assign them in §2.6.

### 2.6 Environments & deploy targets

- **Environments** (`Admin -> Environments`) - each target environment has a key (`prod`), a name, an
  order, and an optional **readiness rule** (§2.5). An environment with no rule gates nothing.
- **Deploy targets** (`Admin -> Deploy Targets`) - for each `(repository, environment)` pair, the
  **deploy strategy** and its settings, a **redeploy policy**, and an optional **readiness rule
  override** (falls back to the environment's rule when empty).

### 2.7 Action handlers & permissions

- **Action handlers** (`Admin -> Action Handlers`) - bind a rollout event (e.g. a step succeeded) to
  an action (notify a channel, move the tracker issue). Each handler declares its own settings.
- **Permissions** (`Admin -> Permissions`) - map auth groups to permission claims.

---

## 3. From a task to a rollout

The orchestrator plans and deploys **per task**, not one global release:

1. **A task is imported** from the tracker, and its merge requests are linked to it by the connection's
   linking rule (§2.1). The **Tasks** page lists tasks and whether each has a built plan.
2. **A per-task plan** is built from the default ordering (§2.4): the waves in which that task's
   repositories deploy.
3. **You launch a rollout** of the task into an environment. Each step deploys one repository using its
   deploy target (§2.6), in wave order, and is held at the **readiness gate** until the environment's
   readiness rule is satisfied.
4. **The Rollouts page** shows every rollout, its status and progress; open one to watch or act on its
   steps. A task's **timeline** records what happened and when.

Relaunching the same task into the same environment does not double-deploy: an identical plan is
recognised as the same rollout.

---

## 4. Stopping Services

```bash
docker-compose down       # stop
docker-compose down -v    # stop and remove data
```

---

## 5. Connecting your CI/CD

- **Webhooks** are the primary integration for `gitlab-webhook` connections: point GitLab at the
  orchestrator's ingress endpoint for that provider. Poll connections need no webhook.
- **REST API** at `/api` (interactive docs at `/swagger`) covers the same operations the admin UI uses.

---

## 6. Troubleshooting

### "Database connection failed"
- Ensure the database is running: `docker-compose logs sqlserver`
- Check `CONNECTION_STRING_DEFAULT` in `.env` and that the password matches

### "RabbitMQ connection failed"
- Check `docker-compose logs rabbitmq`; ensure credentials match `.env`

### "I have no permissions"
- Set `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` with your OID, check the logs for the bootstrap-admin
  line, and restart

### "No tasks are linked to my merge requests"
- Confirm the merge request matches the connection's **linking rule** (§2.1) - the source (branch,
  title or label) and pattern. The connection form's preview is the fastest way to check.
- Confirm the task exists in the tracker and is not closed
- Allow a short delay for the sync consumer

### "A merge request is not deploying"
- It may not satisfy the **readiness rule** for the target environment (§2.5), or the environment /
  deploy target is not configured (§2.6). Closed merge requests are excluded on purpose.

---

## See Also

- [User guide](user-guide.md) - Why each screen exists and how to use it
- [Architecture](architecture.md) - System design and data flow
- [Configuration](configuration.md) - All environment variables
- [Operations](operations.md) - Health checks and monitoring
