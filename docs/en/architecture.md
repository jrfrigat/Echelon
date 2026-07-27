# Release Orchestrator - Architecture

> [Русская версия ->](../ru/architecture.md) - [← Back to docs](../README.md)

---

## Overview

Release Orchestrator plans and executes deployments **per task**. There is no single global release
plan and no "stacks": for each task imported from a tracker, it builds the order in which that task's
repositories deploy, then executes that order as a **rollout** into an environment, holding each step
at a **readiness gate** until the merge request is deployable.

Two inputs shape the order:

- **Repository ordering** — global rules between repositories (`A` deploys after `B`), each **Hard**
  (a real constraint) or **Soft** (a preference that yields first on conflict).
- **Task hierarchy** — parent/predecessor links from the tracker.

These become a directed acyclic graph; cycles are resolved by dropping the lowest-priority edges
(soft first) and logged, and a topological sort produces **waves** — repositories that may deploy in
parallel.

---

## Architecture layers

Onion / ports-and-adapters. Dependencies point **inward only**.

```
Core                enums, pure parsing (task-key extraction, readiness signals); zero dependencies
  ← Application     ports, message contracts, the planning algorithm — no EF
      ← Infrastructure          EF models, DbContext, adapters (Rebus, Redis, DataProtection)
      ← Providers.Abstractions  provider ports + normalized models
          ← Providers.GitLab / Providers.YandexTracker
              ← Web (composition root, API) / Ingress.Webhooks
```

- **Core knows no concrete provider.** Vendor names, status dictionaries and key formats live in the
  adapters; the domain sees only normalized values.
- **EF entities never leave Infrastructure.** The planner takes `PlanMergeRequest`; factories take
  `*ConnectionDescriptor`. Application and Providers.Abstractions never see the EF models.
- **The planning algorithm (`Application/ReleasePlanning`) has no EF dependency** — it is pure and
  tested without a database.
- **Providers register at compile time** (keyed DI + marker records), no dynamic assembly loading.
- **Migrations exist for both providers** — `ReleaseOrchestrator.Migrations.MsSql` and
  `...Migrations.Postgres`, each with two contexts (operational + archive).
- **The PWA is a separate client.** It talks to the API over HTTP and references only the inward,
  zero-dependency assemblies (Core, Providers.Abstractions) so the merge-request→task linking preview
  runs the very same `TaskKeyExtractor` the ingestion does.

---

## Ingestion: push and poll

Events reach the system two ways, and both emit the **same** normalized events so push and poll can
never disagree on what "merged" or "resolved" means:

- **Push** — a `gitlab-webhook` connection's GitLab pushes deliveries to `Ingress.Webhooks`. The host
  owns the route, resolves the connection's secret, and puts events on the bus; each provider owns its
  payload shape and authentication behind `IWebhookParser`.
- **Poll** — the `VcsPollingCoordinator` sweeps every connection whose provider type is registered
  `IngestionMode.Poll`, on the interval that connection configures (`VcsPollSettings`), and emits the
  same events.

**Deduplication** — a delivery may arrive twice (a webhook retry, an overlapping poll). An
`EventDedupStep` backed by a `ProcessedEvent` inbox drops repeats; polled events carry a deterministic
id so the same merge-request state folds to the same event.

Normalized contracts live in `Providers.Abstractions.Ingestion` (e.g. `MrOpened` carries the branch,
title, labels and pipeline result; `TaskCreated`, `TaskStatusChanged`, …). The consumer, not the
parser, applies the connection's **linking rule** to attach a merge request to its task, because the
parser runs in the ingress with no database.

---

## Planning: per task

For each task the planner builds a **rollout plan** (`RolloutPlan` with `PlanTaskNode` / `PlanItem`):

1. Gather the task's repositories (those its merge requests touch).
2. Apply the repository ordering rules (`RepositoryDependency`, Hard/Soft) and the task hierarchy.
3. Resolve cycles by dropping the lowest-priority edges, log what was dropped.
4. Topologically sort into **waves**.

The plan records a **content hash over its order**, so an equivalent recomputation is recognised as
the same plan rather than churning a new one. `PlanOverride` records deliberate manual adjustments.

---

## Execution: rollouts

Launching a task into an environment creates a `Rollout` with a `RolloutStep` per repository, in wave
order (`RolloutStep`, `RolloutStepAttempt` for retries, `RolloutEvent` for the audit trail):

- Each step deploys one repository through its **deploy target** — a `RepositoryDeployTarget` per
  `(repository, environment)` carrying the deploy strategy (`gitlab-merge`, `gitlab-pipeline`, …), a
  redeploy policy, and frozen deploy settings captured at launch (secrets unprotected only at dispatch).
- **The readiness gate** holds a step until the merge request is deployable. Readiness is a set of
  normalized **signals** — `label:…`, `mr-status:…`, `pipeline:…` — evaluated against a named
  `ReadinessRule` (`AllOf` / `AnyOf`). The rule is resolved: a per-merge-request pin
  (`MergeRequestReadinessPin`) → the deploy target's override → the environment's default
  (`DeploymentEnvironment.ReadinessRuleId`) → no gate.
- Deploy state is tracked per environment (`MrDeploymentState`, `MrDeployClaim`): the same merge
  request can be live on staging and not on prod. Relaunching an identical plan is recognised as the
  same rollout, so it does not double-deploy.

---

## Messaging

Rebus over RabbitMQ, type-based routing. Consumers handle MR opened / status changed, task created /
status changed, task sync, and rollout progress. Cores are competing consumers on the same queue; the
`ProcessedEvent` inbox makes redelivery safe.

---

## Data model (operational database, `AppDbContext`)

- **Connections** — `VcsConnection`, `TrackerConnection`: name, type, URL, encrypted token, and an
  opaque `ProviderSettingsJson` bag (linking rule / poll interval / org id / closed statuses). No
  "ready-for-deploy label" column.
- **Repositories & ordering** — `Repository` (name, external path, connection); `RepositoryDependency`
  (from → after, Hard/Soft); `RepositoryDeployTarget` per `(repo, environment)` with strategy,
  `DeploySettingsJson`, redeploy policy, optional `ReadinessRuleId`.
- **Tasks** — `TaskItem` (external id, title, status, closed-at); `TaskDependency` (parent/predecessor).
- **Merge requests** — `MergeRequest` (external id, branches, repo, `Status`, `Labels`,
  `PipelineResult`, `IsStatusManual`); journals `MergeRequestStatusChange`, `MergeRequestLabelChange`;
  `MergeRequestReadinessPin` for a manual per-MR gate override.
- **Readiness & environments** — `ReadinessRule` (unique name, mode, required signals);
  `DeploymentEnvironment` (key, order, enabled, `ReadinessRuleId`).
- **Plans** — `RolloutPlan`, `PlanTaskNode`, `PlanItem`, `PlanOverride`.
- **Rollouts** — `Rollout`, `RolloutStep`, `RolloutStepAttempt`, `RolloutEvent`; deploy state
  `MrDeploymentState`, `MrDeployClaim`.
- **Actions & permissions** — `ActionBinding`; `PermissionClaim`, `GroupPermissionMapping`,
  `UserPermissionOverride`.
- **Ingestion** — `ProcessedEvent` (dedup inbox).

The **archive database** (`ArchiveDbContext`) holds tasks/rollouts closed long ago; it is written
idempotently, without a shared transaction, by a hosted archiver gated on a Redis lease.

Provider divergences between SQL Server and PostgreSQL are isolated to `ProviderSpecificMapping`
(concurrency token, filtered-index dialect); dates are stored `Kind=Utc` only.

---

## Event flow

```
1. GitLab pushes a webhook  ─or─  the poller sweeps a poll connection
2. A normalized event is produced (parser in ingress, or coordinator)
3. EventDedupStep drops repeats (ProcessedEvent inbox)
4. A consumer updates the database (MR status/labels/pipeline, task, dependencies),
   applying the connection's linking rule to attach the MR to its task
5. The task's rollout plan is (re)built if its inputs changed (content-hash compared)
6. An operator launches a rollout of the task into an environment
7. Each step deploys its repository in wave order, held at the readiness gate
8. The PWA reads tasks, plans and rollouts via the API
```

---

## Security

Credentials are **encrypted at rest** with ASP.NET Core Data Protection. Without a certificate to
protect the key ring (`DataProtection__CertificatePath`), a database dump contains both the encrypted
tokens and the keys — so outside Development the app refuses to start without a certificate (or an
explicit opt-out).

- **Authentication** — OpenID Connect via an external provider (the `oid` claim is the identity), or
  the built-in `Local` provider in development.
- **Authorization** — claim-based, not role-based (`release.plan.approve`, `config.edit`, …), mapped
  to groups via `GroupPermissionMapping` or per-user via `UserPermissionOverride`; computed permissions
  are cached in Redis.
- **HTTPS** is assumed terminated at a reverse proxy; use `ForwardedHeaders`.
- **Request audit** records every API request (with a recorder middleware) for the admin audit screen.

---

## Observability

- **Health** — `GET /health` (liveness) and `GET /health/ready` (readiness: DB, RabbitMQ, Redis).
- **Logging** — structured (Serilog), with a correlation id threaded through requests and consumers.
  Logs are not localized — they are for operators.
- **Metrics & tracing** — a Prometheus metrics endpoint and OpenTelemetry are wired; the async path
  (ingress → RabbitMQ → core) is traced when an OTLP collector is configured.

---

## Deployment topology

A modular monolith: the API host (`Web`, serving the PWA and API) and the ingress host
(`Ingress.Webhooks`) can run as separate processes over a shared database and RabbitMQ. Multiple
replicas are competing consumers on the same queue. The background archiver runs in every core pod but
is gated on a Redis-backed distributed lease (`SET NX PX` with owner validation) — not a consensus
algorithm, but acceptable because archiving is idempotent; if Redis is unavailable the pass is skipped
(fail-closed).

---

## See Also

- [User guide](user-guide.md) - Why each screen exists and how to use it
- [Getting started](getting-started.md) - Setup walkthrough
- [Configuration](configuration.md) - Environment variables and setup
- [Providers](providers.md) - How to add a new VCS or tracker
