# Echelon - User Guide

> [Русская версия ->](../ru/user-guide.md) - [← Back to docs](../README.md)

This guide is for the people who **use** the orchestrator: release managers and engineers who
configure it and roll tasks out. It answers two questions for every part of the product - *why does
this exist* and *how do I use it*.

For installation see [Getting Started](getting-started.md); for the first setup as numbered steps
with example values see [First Setup](walkthrough.md); for internals see
[Architecture](architecture.md).

---

## 1. What this is for

A task is rarely finished in one repository. A single ticket routinely touches a database migration,
a backend service and a frontend - three repositories, three merge requests - and they cannot be
deployed in just any order. The migration goes first or the backend breaks on boot. The backend goes
before the frontend or the UI calls an endpoint that does not exist yet.

Teams normally hold that ordering in someone's head, or in a spreadsheet, and re-derive it by hand
for every release. It works until the day it does not: a task depends on another team's task, someone
deploys the frontend first, and the outage is discovered by users.

The orchestrator makes that ordering **explicit, reusable and executable**:

- you state the ordering rules **once** (which repository goes after which);
- it learns the rest from your tracker (which task waits for which, which subtasks hang under which
  parent);
- for any task it builds a **rollout plan** - the merge requests to deploy, grouped into waves;
- it then **executes** that plan against an environment, in order, and stops when something fails.

The result: rolling out a task is a decision ("roll out PROJ-42 to staging"), not a procedure.

---

## 2. The vocabulary

| Term | What it is |
| :-- | :-- |
| **Task** | An issue imported from your tracker (`PROJ-42`). The thing you roll out. |
| **Merge request** | An MR/PR in a repository. It is tied to a task by naming the task key in its branch. |
| **Repository** | A repo served by a VCS connection. |
| **Environment** | A deploy target: `dev`, `staging`, `prod`. |
| **Rollout plan** | For one task: every merge request that has to deploy, and in what order. |
| **Wave** | One step of a plan. Everything inside a wave deploys in parallel; wave 2 waits for wave 1. |
| **Rollout** | One execution of a plan against one environment. |

Two things worth internalising early:

- **A plan is built per task, not globally.** There is no single "release plan" everyone edits. You
  open a task, and its plan is the task plus everything it has to wait for.
- **Waves are a property of merge requests, not of tasks.** One task's merge requests can land in
  different waves, because the ordering rules act on repositories.

---

## 3. How work gets in

You do not enter merge requests by hand. Three channels feed the system, and all three produce the
same normalized events, so nothing downstream can tell which one a change arrived through.

### Push - webhooks (the default)

GitLab and the tracker call the orchestrator's webhook endpoint when something changes. This is the
default and the one to prefer: a change is reflected in seconds, and the orchestrator does no work
while nothing is happening.

Point your VCS and tracker webhooks at the ingress service (see
[Getting Started](getting-started.md) for the URL and the shared secret).

### Poll - the orchestrator asks

For a setup that cannot deliver webhooks - a VCS behind a firewall, no permission to add hooks - a
connection can be set to poll instead. The orchestrator then asks the provider for changes on an
interval.

Poll is slower and costs requests whether or not anything changed, so it is opt-in per connection.

Set it per connection in **VCS connections** - the *Ingestion* field. New connections default to
Push, and the connection list marks the polling ones so they are easy to spot.

Editing a connection for any other reason leaves the mode alone: blank means "keep what is stored",
the same rule the access token follows.

### Reconciliation - the periodic catch-up

Some things never emit a webhook at all. Dependency links between tracker issues are the main one:
most trackers do not fire an event when someone adds "blocks" between two issues.

So a background job re-reads task links periodically. This is a safety net, not the main path - it
also repairs anything a missed webhook left stale.

---

## 4. The admin sections

Everything below lives under **Administration** in the left-hand menu. This is roughly the order to
set them up in - each depends on the ones above it.

### 4.1 VCS connections

**Why:** the orchestrator has to reach your GitLab to read merge requests and to deploy them.

**How:** add one connection per GitLab instance - name, type, API URL, access token. The **type** is
`gitlab-webhook` (GitLab pushes events to the orchestrator) or `gitlab-poll` (the orchestrator polls
on an interval you set).

Each connection carries a **linking rule**: which tracker task an incoming merge request belongs to,
as a *source* (branch, title or label) and a *pattern*. The connection form previews the task key the
rule would extract from a sample you type, so you can get it right before saving. (A merge request's
status can also be pinned by hand from the merge-requests screen when you need to override.)

What makes a merge request *deployable* is no longer a single label - see readiness rules under
Environments (4.5).

### 4.2 Tracker connections

**Why:** tasks, their statuses, their dependencies and their hierarchy all come from the tracker.

**How:** one connection per tracker - name, API URL, org id, access token.

### 4.3 Repositories

**Why:** the orchestrator only manages repositories it is told about, and it needs to know how each
one is deployed.

**How:** register each repository against its VCS connection, using the VCS's own path
(`group/project`).

A repository also carries its **deploy strategy** - *how* to deploy it (merge the MR, or trigger a
pipeline). A rollout step cannot run without one, so a repository with no strategy will stop a plan
at execution time.

### 4.4 Default rollout plan

**Why:** this is the ordering that applies to **every** task, and the single most valuable thing you
will configure. It answers "in what order do our repositories deploy" - once, instead of per release.

**How:** state rules one pair at a time: *this repository deploys after that one*. The page shows
both the rules and, above them, **the order they add up to** - the waves every rollout will follow.
That derived order is produced by the same engine that orders real rollouts, so what you see is what
will happen.

Each rule is **hard** or **soft**:

- **hard** - never dropped. Use it for real technical constraints ("the API cannot start before the
  migration").
- **soft** - advisory. If the rules ever contradict each other, soft rules are what yield first. Use
  it for preferences ("we like deploying docs last").

If your rules do contradict each other - repository A after B after A - the page says so and names
what it had to drop. That is a misconfiguration to fix, not a warning to live with.

Repositories with no rules at all deploy in the first wave, in parallel. That is the correct reading
of "nothing constrains them", not an oversight.

### 4.5 Environments

**Why:** a rollout targets an environment, and deploy state is tracked per environment - the same
merge request can be live on staging and not on prod.

**How:** add each target with a key (`prod`), a display name and an order. Disabled environments
cannot be launched into.

Each environment can carry a **readiness rule** - what a merge request must show before it may deploy
*here*. A rule is a set of normalized signals (a label, the merge-request status, or a pipeline
result) combined as all-of or any-of; you define rules on the **Readiness Rules** page and assign one
per environment, overriding it per repository where a project needs something different. An
environment with no rule gates nothing.

### 4.6 Action handlers

**Why:** a rollout should be able to do more than deploy - notify a channel, move the tracker issue,
call a webhook - without that being hard-coded.

**How:** bind an action to an event ("rollout finished", "step failed"). Each action type declares
its own settings; secret fields are stored encrypted.

### 4.7 Request log

**Why:** to answer "is anything failing, and what is slow" without leaving the product.

**How:** it records itself - there is nothing to configure. The page summarises the window by
endpoint (calls, 4xx, 5xx, p50/p95) and then lists individual requests, filterable to errors only.

Two things it deliberately tells you about itself. It shows a **warning banner** when the picture is
incomplete - records dropped, the anonymous cap reached, percentiles from a sample, or the webhook
host not covered - because an unexplained gap in a log reads as quiet traffic, which is the opposite
of what it means. And the **caller address is shown twice**: the connection's real peer, and the
`X-Forwarded-For` value, which is whatever the client claimed and is labelled as unverified.

It never records headers, bodies, query strings or cookies - those are not filtered out, they are
never read, so no future endpoint can leak by being forgotten. When something failed, the entry
carries the request id: search your log aggregator by it for the full error text.

### 4.8 Permissions

**Why:** viewing a plan, approving it and executing it are different levels of trust.

**How:** map your identity provider's groups to the orchestrator's permission claims, and add
per-user overrides where a group is too coarse.

---

## 5. What decides the deploy order

Three independent sources feed one ordering. Understanding which is which is most of understanding
the product.

**1. Repository ordering** - the default plan of section 4.4. It is what orders the merge requests
*within* a single task: the migration before the backend before the frontend.

**2. Task dependencies** - read from the tracker. "PROJ-42 depends on PROJ-17" means every merge
request of PROJ-17 deploys before any of PROJ-42's. Rolling out PROJ-42 pulls PROJ-17 in
automatically - you do not have to remember it.

**3. Task hierarchy** - also read from the tracker. **Subtasks deploy before their parent.** A parent
task is the umbrella over the concrete work its children carry, so the children go first and the
parent goes last.

That direction points one way only. Rolling out a **parent** pulls in its subtasks and deploys them
first. Rolling out a **subtask** rolls out just that subtask - it does not drag in the parent or its
siblings.

A task's parent and subtasks are shown at the top of the task screen, and each is a link to that
task.

### When the rules cannot all hold

Sometimes the constraints contradict each other - usually a cycle. The orchestrator will still
produce an order, because you need something to look at, but it will **never do so silently**: it
drops the least critical constraint (soft before hard), and reports every drop as a **conflict** on
the plan.

A plan with conflicts cannot be launched. Fix the configuration, recalculate, and the conflict goes.

---

## 6. Rolling out a task

1. **Open the task.** The task list is the home screen; it shows how many merge requests each task
   has and whether a plan already exists.
2. **Check the hierarchy.** Parent and subtasks are at the top. If this is a parent, its subtasks
   will be deployed first as part of this rollout.
3. **Build the plan** - *Recalculate*. This reads the current state of the world (merge requests,
   dependencies, hierarchy, ordering rules) and produces the dependency tree and the waves. Do this
   again whenever anything changed; a plan is a snapshot, not a live view.
4. **Read the plan.** Every task in the tree is either the target or something it waits for. Every
   merge request shows the wave it deploys in and its current status.
5. **Resolve conflicts, if any.** The warning banner names each dropped constraint. Launch is
   blocked until there are none.
6. **Pick an environment and launch.** Three things can refuse the launch at this point, each naming
   exactly what to fix: a prerequisite task not yet deployed in that environment, a merge request that
   does not meet the environment's readiness rule, and **unfinished work** - see below.
7. **Watch the rollout.** Steps run wave by wave. Everything in a wave runs in parallel; the next
   wave starts only when the current one has fully succeeded.

### "Unfinished work blocks this rollout"

The orchestrator watches branches, not only merge requests. A branch whose name points at a task in
the plan, which is not merged and which **no merge request carries**, is work that somebody started
and has not put up for review. Rolling out the parent while that branch is outstanding ships an
incomplete change, so the launch is refused and each offending branch is named.

Three ways out, and the right one depends on what the branch actually is:

- raise a merge request for it - then it is in the plan and gets deployed in order;
- merge it, if it has already landed by another route;
- delete it, if it was abandoned.

Note that the ordinary source branch of a merge request in the plan never blocks anything - every one
of those is unmerged at launch, which is precisely what the rollout is about to change. Only a branch
that nobody has raised counts.

### The task's history

Every task screen has a **History** button. It shows, newest first, everything that happened to that
task: when it arrived and through which channel, when its merge requests opened, changed status,
merged or closed, every time its plan was rebuilt and by whom, and every rollout with who launched it
and into which environment. Filter to **people only** to answer "who did what" in one click.

Two honest limits it states on the page rather than hiding:

- **Entries that predate this feature cannot be recovered.** Rollouts, plan versions and merge-request
  timestamps were always stored and appear for old tasks. Arrival time, plan authorship and status
  transitions were not recorded before, and read as "not recorded" rather than as "nothing happened".
- **Under Local authentication every operator shares one identity**, so every "who" is the same
  account whoever acted. The page says so when it detects it. Per-person attribution needs Entra or
  OIDC.

### If a step fails

The rollout stops - deliberately. Continuing past a failure is how a half-deployed release happens.

From the rollout screen you can:

- **Retry** the step, once you have fixed the cause;
- **Skip** it, if it turned out not to be needed (say it was already deployed by hand).

Launching the same task into the same environment again does not double-deploy: a relaunch of an
identical plan is recognised as the same rollout.

---

## 7. When something is not as expected

**A task is not in the list.** Nothing has imported it. Check the tracker connection, and that a merge
request actually matches the connection's linking rule (that rule is what links an MR to a task).

**A merge request is missing from the plan.** Either it does not match the linking rule, or the
readiness rule for the target environment is not satisfied, or it is closed. Closed merge requests are
excluded on purpose.

**A subtask is not in the parent's plan.** Its parent link comes from the tracker - confirm the
tracker really shows it as a subtask, then recalculate.

**Everything landed in one wave.** No ordering rules apply to those repositories. That is what "no
constraints" means - configure the default rollout plan.

**The plan says it dropped a constraint.** Your rules contain a cycle. The conflict names both ends;
remove or soften one of the rules on that path.

**A step fails with no deploy strategy.** The repository has none configured - set it on the
repository.

---

## See also

- [Getting Started](getting-started.md) - installation and first run
- [Architecture](architecture.md) - how it works internally
- [Operations](operations.md) - health, monitoring, archival
- [Configuration](configuration.md) - environment variables
