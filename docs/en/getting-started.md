# Release Orchestrator - Getting Started

> [Русская версия ->](../ru/getting-started.md) - [← Back to docs](../README.md)

---

## Requirements

- **.NET SDK 10** (to build)
- **SQL Server 2019+** (operational and archive databases)
- **RabbitMQ 3.8+** (message queue)
- **Redis 6.0+** (cache for computed permissions)
- **Docker & Docker Compose** (recommended for local development)
- A system supporting OpenID Connect authentication (e.g., Azure AD, keycloak, or any OIDC provider)

---

## 1. Local Setup with Docker Compose

The easiest way to run locally is with Docker Compose. All required services are included.

### 1.1 Prepare `.env` File

In the project root, create `.env` based on `.env.example`:

```bash
# Example: C:\Job\Projects\FrigaT\ReleaseOrchestrator\.env

# SQL Server
MSSQL_SA_PASSWORD=YourComplexPassword123!
SA_PASSWORD=YourComplexPassword123!

# RabbitMQ
RABBITMQ_DEFAULT_USER=guest
RABBITMQ_DEFAULT_PASS=guest

# Redis (leave blank for no auth in dev)
REDIS_PASSWORD=

# OpenID Connect (configure your provider)
OIDC_AUTHORITY=https://your-oidc-provider/.well-known/openid-configuration
OIDC_CLIENT_ID=your-client-id
OIDC_CLIENT_SECRET=your-client-secret
OIDC_REDIRECT_URI=https://localhost:5173/authentication/login-callback

# Release Orchestrator
ASPNETCORE_ENVIRONMENT=Development
CONNECTION_STRING_DEFAULT=Server=sqlserver;Database=ReleaseOrchestrator;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
CONNECTION_STRING_ARCHIVE=Server=sqlserver;Database=ReleaseOrchestratorArchive;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
REDIS_CONNECTION_STRING=redis:6379
QUEUE_USERNAME=guest
QUEUE_PASSWORD=guest
QUEUE_HOST=rabbitmq
QUEUE_PORT=5672
```

**⚠️ IMPORTANT for production:**
- Use strong passwords (not `guest`)
- Enable Redis authentication and use `--requirepass` in redis config
- Use HTTPS with proper certificates
- Set `ASPNETCORE_ENVIRONMENT=Production`
- Configure real OIDC credentials

### 1.2 Start Services

```bash
docker-compose up -d
```

Wait for SQL Server to be ready (30-60 seconds):

```bash
docker-compose logs -f sqlserver | grep "Recovery is complete"
```

### 1.3 Apply Database Migrations

```bash
# From project root
dotnet ef database update --project src/ReleaseOrchestrator.Migrations.MsSql \
  --startup-project src/ReleaseOrchestrator.Web \
  --context AppDbContext

# Repeat for archive context
dotnet ef database update --project src/ReleaseOrchestrator.Migrations.MsSql \
  --startup-project src/ReleaseOrchestrator.Web \
  --context ArchiveDbContext
```

### 1.4 Bootstrap Admin User

On first run, no users have permissions. Use bootstrap admin mode to create the first admin:

```bash
# Set environment variable (temporary, for this run only)
$env:AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS="your-oid-value"

# Then start the app
dotnet run --project src/ReleaseOrchestrator.Web
```

**How to find your OID:**
1. Log in with your OpenID provider
2. The app will create a user (stored in database)
3. Go to **Admin → Users** (will be empty due to no permissions yet)
4. Check application logs for your `oid` claim
5. Set `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` to that value and restart

Once you log in as bootstrap admin:
- Your user gets full permissions automatically
- You can add other admins via **Admin → Permissions**
- Remove `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` for production (fail-safe: if set, anyone matching it becomes admin)

---

## 2. Configure External Connections

Before building a release plan, connect to your VCS and tracker.

### 2.1 Add VCS Connection (GitLab)

1. **Go to Admin → VCS Connections**
2. **Click "+ New Connection"**
3. **Fill in:**
   - **Name:** `my-gitlab` (used in YAML and stack config)
   - **Type:** `GitLab`
   - **API URL:** `https://gitlab.company.com` or `https://gitlab.com`
   - **Access Token:** Personal access token with `api`, `read_repository` scopes
   - **Ready-for-Deploy Label:** (optional) A label name in GitLab that marks MRs ready for deployment (e.g., `ready-deploy`)

4. **Click Save** — the app will validate the connection and detect GitLab version

**Note:** If you leave "Ready-for-Deploy Label" blank, only MRs explicitly marked via the API endpoint will enter the plan.

### 2.2 Add Tracker Connection (Yandex Tracker)

1. **Go to Admin → Tracker Connections**
2. **Click "+ New Connection"**
3. **Fill in:**
   - **Name:** `my-tracker`
   - **Type:** `Yandex Tracker`
   - **API URL:** `https://api.tracker.yandex.net` or your self-hosted instance
   - **Access Token:** OAuth token with `write:tracker` scope
   - **Organization ID:** The numeric org ID visible in Yandex Tracker

4. **Click Save** — the app will validate the connection

### 2.3 Add Repository

1. **Go to Admin → Repositories**
2. **Click "+ New Repository"**
3. **Fill in:**
   - **Name:** (display name, e.g., "API Backend")
   - **External ID:** (path in GitLab, e.g., `my-group/my-project`)
   - **VCS Connection:** Select from dropdown
   - **Tracker Connection:** (optional) Select if this repo's MRs have associated tasks

4. **Click Save** — the app will test access to the repository

### 2.4 Add Stacks

1. **Go to Admin → Stacks**
2. **Create stacks for your release groups:**
   - `backend` (all services)
   - `frontend` (web UI)
   - `data-migrations` (database-only changes)
3. **Assign repositories to stacks:**
   - Select a stack
   - Add repositories (many repositories per stack)
4. **Set stack dependencies:**
   - `data-migrations` → `backend` (Hard: migrations must complete)
   - `backend` → `frontend` (Soft: preferred order, but not strict)

---

## 3. Trigger First Release Plan

### 3.1 Open an MR in Your Repository

Mark an MR as ready for deployment:

**Option A: Via Label (if configured)**
- Add the label you specified in "Ready-for-Deploy Label"
- The app automatically detects this and includes the MR in the plan

**Option B: Via API**
```bash
curl -X PATCH https://localhost:5173/api/merge-requests/{mr-id}/status \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"status": "ReadyForDeploy"}'
```

### 3.2 View the Plan

1. **Navigate to UI:** `https://localhost:5173`
2. **Go to Release → Plans**
3. You should see a plan with stages showing which MRs can deploy in parallel

### 3.3 Manual Editing

1. **Drag and drop** MRs between stages to reorder manually
2. **Click "+ Stage"** to insert a new stage
3. **Click "Save & Export"** to download YAML for version control

---

## 4. Stopping Services

```bash
docker-compose down
```

To remove data:
```bash
docker-compose down -v
```

---

## 5. Connecting Your CI/CD

The app exposes:
- **REST API:** `/api` (docs at `/swagger`)
- **BFF (Backend for Frontend):** `/bff` (for PWA)

Example: Trigger plan recalculation after all tests pass:
```bash
curl -X POST https://your-release-orchestrator/api/plans/recalculate \
  -H "Authorization: Bearer <api-token>"
```

---

## 6. Troubleshooting

### "Database connection failed"
- Ensure SQL Server is running: `docker-compose logs sqlserver`
- Check `CONNECTION_STRING_DEFAULT` in `.env`
- Verify SA password matches

### "RabbitMQ connection failed"
- Check: `docker-compose logs rabbitmq`
- Ensure credentials match `.env`

### "I have no permissions"
- Set `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` with your OID
- Check logs for: `"User {oid} initialized as bootstrap admin"`
- Restart the app

### "No tasks are linked to my MRs"
- Ensure branch names follow pattern: `{TRACKER_KEY}-{number}` (e.g., `TASK-123-fix-bug`)
- Check that task exists in tracker and is not closed
- Wait ~30 seconds for task sync consumer

---

## See Also

- [Architecture](architecture.md) - System design and data flow
- [Configuration](configuration.md) - All environment variables
- [Operations](operations.md) - Health checks and monitoring
