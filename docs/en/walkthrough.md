# Echelon - First Setup, Step by Step

> [Русская версия ->](../ru/walkthrough.md) - [← Back to index](../README.md)

The service is deployed, the admin UI is open, and it is not obvious what to fill in first. That is
what this document is for: what to add where, with what values, and **what should happen after each
step**, so you never continue blind.

Installation and environment variables are in [Getting Started](getting-started.md); why each part
exists is in the [User Guide](user-guide.md). This page is only the order of operations.

---

## How this works, in two paragraphs

**Only configuration is entered by hand. The data arrives on its own.** Tasks are read from the
tracker, merge requests and branches from the VCS. Neither can be created in the UI, and that is not
an omission: they already exist in the systems that own them.

So the setup is three answers: **where to go** (connections), **what to read there** (repositories),
and **where to deploy** (environments and deploy targets). After that tasks appear by themselves, a
plan is built for each, and a plan is launched into an environment.

**Do the first run on polling connections (`-poll`)**, even if production will use webhooks. Polling
does not require GitLab and the tracker to reach your ingress, and it gives you a "Poll now" button -
no waiting for an event to find out whether the chain works. Webhooks come later, at
[step 13](#step-13-webhooks-instead-of-polling).

---

# Part A. The shortest path to a first rollout

Thirteen steps; the first eleven are mandatory. Every screen is in the left-hand menu.

## Step 1. Make sure you have the permissions

Open **Administration → Permissions**. If the page opens and lists the permissions, you are fine -
go to step 2. If the API answers 403 or nothing saves, your account is missing `config.edit`.

There are four permissions, stored in the database under these names:

| Permission | What it allows |
| :-- | :-- |
| `release.plan.view` | Reading tasks, plans, merge requests and rollouts. The baseline |
| `config.edit` | All administration: connections, repositories, environments, permissions themselves |
| `release.execute` | Launching, cancelling, retrying and skipping a rollout |
| `release.plan.approve` | Readiness decisions: pins on merge requests, a manual status |

The first administrator is granted through an environment variable - see
[Getting Started, §1](getting-started.md). Once signed in that way, grant the others from this same
screen and remove the variable.

## Step 2. A VCS connection

**Administration → VCS Connections → "New connection"**

| Field | Example | Notes |
| :-- | :-- | :-- |
| Name | `gitlab-main` | **Latin letters, digits, `-` and `_` only** (up to 100 characters). The name goes into the webhook URL, so a name like `GitLab (prod)` leaves the webhook unusable. Renaming later is possible, but the webhook URL changes with it |
| Type | `gitlab-poll` | For the first run. `gitlab-webhook` is for GitLab pushing events. **The type is fixed at creation**: switching from polling to webhooks means a second connection |
| API URL | `https://gitlab.company.com` | The instance root, without `/api/v4` |
| Poll interval (seconds) | `60` | `gitlab-poll` only. 30 - 86400, default 300 |
| Task-key source | `branch` | Where the task key is read from: the branch, the MR title, or a label |
| Task-key pattern | leave as is | The default regex reads an uppercase key: `PROJ-42`, `ПР-5639`. Non-Latin keys work |
| Access token | `glpat-…` | Scopes `api` and `read_repository`. Stored encrypted; when editing, an empty field means "keep the existing one" |

The labels `Poll interval (seconds)`, `Task-key source` and the other provider settings are **in
English whatever the UI language**: the provider declares them, not the application.

**Check the linking rule before saving.** Below the fields there is a live preview: type an example
branch - `feature/PROJ-42-add-login` - and it shows the key it extracts (`PROJ-42`). If nothing is
extracted, merge requests will arrive and link to no task at all, and nothing will say so.

**After this step:** the connection is listed, its Type column shows a `gitlab-poll` chip, and a
refresh icon appears on the row - that is "Poll now".

## Step 3. A tracker connection

**Administration → Tracker Connections → "New connection"**

| Field | Example | Notes |
| :-- | :-- | :-- |
| Name | `tracker-main` | Same character restriction as a VCS connection |
| Type | `yandextracker-poll` | For the first run. `yandextracker-webhook` is for a tracker that pushes |
| API URL | `https://api.tracker.yandex.net` | |
| Poll interval (seconds) | `60` | `-poll` only |
| **Queues to sweep** | `PROJ, INFRA` | **The load-bearing field.** Queue keys, comma-separated. They become the query `Queue: PROJ, INFRA AND Resolution: empty()`, which is the only way the system learns that any task exists |
| Search query (optional) | empty | A whole query in the tracker's language, instead of the queues, when "open" is not "has no resolution" for your workflow |
| Organization ID | `1234567` | Sent as `X-Org-Id`. Required: the connection will not bind without it |
| Closed statuses | empty | Empty = the defaults (`closed, cancelled, rejected, resolved`), the statuses that mean a task is done |
| Access token | your OAuth token | Sent as `Authorization: OAuth <token>` |

**Leaving Queues to sweep empty means a poll finds nothing**, and it says so - but only when you
press "Poll now". Silently finding nothing is the most common cause of "I configured everything and
there are no tasks".

**After this step:** the connection is listed with a `yandextracker-poll` chip and a refresh icon.

## Step 4. Repositories

**Administration → Repositories → "New repository"**

Register every repository the orchestrator will manage.

| Field | Example | Notes |
| :-- | :-- | :-- |
| Name | `Backend` | Whatever you call it; this is what plans and rollouts show |
| External ID | `my-group/backend` | **The full GitLab path**, not the project name. `backend` is a 404, and the repository is skipped silently on every poll |
| VCS connection | `gitlab-main (gitlab-poll)` | |

This is not paperwork: **a merge request from an unregistered repository is dropped.** The event
reaches the service and is counted on the Ingestion screen, but nothing appears under Work - the log
holds a `Repository not found` warning. A poll likewise walks **only** registered repositories.

**After this step:** the repository is listed with its connection.

## Step 5. An environment

**Administration → Environments** - the form sits above the table.

| Field | Example | Notes |
| :-- | :-- | :-- |
| Key | `dev` | The short machine key; deploy targets and action scopes address it |
| Name | `Development` | |
| Order | `10` | Position in the progression: `dev` 10, `staging` 20, `prod` 30 |
| Gate | **No gate** | For the first run, exactly this. A readiness rule comes in [part B](#b1-readiness-rules) |

**After this step:** a row in the table, an empty Gate and Enabled = yes. A disabled environment is
not offered at launch.

## Step 6. A deploy target

**Administration → Deploy Targets**

One row per **(repository, environment)** pair: how this repository is deployed into this
environment. **Without one the launch refuses**, with "Repository 'Backend' has no deploy strategy
for environment 'dev'".

| Field | Example | Notes |
| :-- | :-- | :-- |
| Repository | `Backend` | |
| Environment | `dev` | |
| Strategy | `gitlab-merge` | What deploying means here - the three options are below |
| Redeploy | `Once` | `Always` permits deploying the same MR into this environment again |
| Readiness | "environment default" | Overrides the environment's rule for this repository |

There are three strategies, and choosing between them is choosing **what counts as a deploy**:

| Strategy | What it does | Its own fields |
| :-- | :-- | :-- |
| `gitlab-merge` | Merges the merge request | none |
| `gitlab-pipeline` | Starts a **new** pipeline on a branch or tag and waits for it | `Pipeline ref` - branch or tag, defaults to `main` |
| `gitlab-job` | Runs **one job of the merge request's own latest pipeline** and waits for it | `Job name`, `Re-run` |

**`gitlab-job` is for a manual gate in the pipeline you already have.** The MR's pipeline has run and
the artefact is built, and deploying means pressing the one button on it (`deploy:staging`).
`gitlab-pipeline` will not do: it starts a *second* pipeline, rebuilding everything against the
branch head rather than the commit that was tested.

| Field | Example | Notes |
| :-- | :-- | :-- |
| Job name | `deploy:staging` | Exactly as the pipeline spells it, case included. Chips below the field carry the names from the selected repository's recent pipelines - clicking beats recalling. Typing your own still works: a job added in the branch under review is in no pipeline yet |
| Re-run | `if-not-successful` | What to do when that job already succeeded. The default treats it as already deployed; `always` runs it again, which is what "Redeploy: Always" on the target means for an idempotent deploy job |
| Pipeline source | `any` | Which kind of pipeline to take the job from: `merge_request_event`, `push`, `web`, `api`, `trigger`, `schedule`, `parent_pipeline`, `external` |
| Pipeline status | `any` | Only consider a pipeline in this state: `success`, `manual`, `running`, `failed` |
| Pipeline to take | `last` | Which of the matching pipelines to use - the newest (`last`) or the oldest (`first`) |

What happens depends on the job's state: one waiting on its manual gate is **played**; one that has
already run (`failed`, `canceled`, `skipped`) is **retried**, which is a new job with a new id; one
already running is adopted rather than started twice; a successful one is *already done* unless
`Re-run` is `always`.

**Which pipeline it takes.** A merge request usually has several at once: the branch push built one,
opening the merge request built another (`merge_request_event`), and a manual, scheduled or
API-triggered run can sit beside them. They do not run the same jobs, so "the latest" is a
coincidence, not a decision. The rule is:

1. the merge request's pipelines are narrowed by **Pipeline source** and **Pipeline status** (`any`
   narrows nothing);
2. **Pipeline to take** picks which end of what remains - newest or oldest;
3. then only pipelines **of that same commit** are opened, in that order, until one holds the job.

Step three is a safety property and is not configurable. A branch pipeline and a merge-request
pipeline of one commit are two views of the same build, and the job may live in either; a pipeline of
an earlier commit is different code, and deploying it by accident is worse than failing. So an older
commit is never reached, however the filters are set.

When nothing matches, the error lists what the merge request actually has -
`101 (push/failed), 100 (merge_request_event/success)` - which is enough to correct the filters.

**What it cannot do:** a job in a child pipeline (`trigger:`). GitLab does not return it among the
parent's jobs, so such a job is reported as missing.

**Where the chips come from.** The union of the repository's three most recent pipelines: a branch
pipeline and a merge-request pipeline do not run the same jobs, so reading only the newest would miss
the one you want about half the time. When there are no chips, the reason is printed under the field -
the provider cannot list jobs, the token was refused, or no pipeline has run yet. The field stays a
plain text box either way: a job added in the branch under review is in no pipeline yet and still has
to be configurable.

Repeat for every repository from step 4.

## Step 7. The first tracker poll

**Administration → Tracker Connections** → the refresh icon on the row.

It reports how many syncs were queued and how many of those tasks were new. If the tracker could not
be read, you get the reason - usually a missing queue setting or a rejected token - rather than
"nothing found".

**After this step:** a message like "queued 12 sync(s), 12 of them new".

## Step 8. The first VCS poll

**Administration → VCS Connections** → the refresh icon.

The poll walks every repository of the connection, emits one event per open merge request, and
reports the branches separately. A repository it cannot read is named, and the rest are still
processed.

## Step 9. Check the Ingestion screen

**Administration → Ingestion**

This answers "did anything arrive at all". Three tables:

- **Workers** - VCS poll, tracker poll, task reconciliation. State: *running*, *idle*, *another
  replica* (another instance holds it right now) or *off*. Plus the interval, the last pass, how
  long it took, and the problem if the pass failed;
- **What arrived** - counts by kind since the process started: tasks created, merge requests seen,
  branches seen, plans rebuilt. Counted identically for a webhook and a poll;
- **The last poll per connection** - one row per polling connection, naming the repositories it
  could not read.

The screen refreshes itself every 5 seconds. The counters are **per replica and since its start**: a
restart zeroes them, and that is not data loss.

**What you want to see:** tasks created above zero and merge requests seen above zero. A counted
signal with nothing in the lists is [part C](#part-c-when-something-is-wrong).

## Step 10. Tasks and merge requests

**Tasks** lists the imported tasks: key, title, status, MR count, whether a plan exists.

**Work** lists the units of work: task, repository, carrier (a merge request or a bare branch),
state and readiness. A manual merge-request status is set here when the provider did not report one.

**What you want to see:** your task (`PROJ-42`) under Tasks, and its merge requests under Work with a
non-empty Task column. A merge request with no task means the linking rule from step 2 did not fire.

## Step 11. Build the plan

**Tasks → open the task → "Build plan"** ("Recalculate plan" once one exists).

A plan is waves: everything inside a wave deploys in parallel, wave 2 waits for wave 1. With no
ordering rules yet, every repository lands in the first wave - that is expected.

"Show YAML" hands you the plan as text, which can be saved, edited and posted back.

**After this step:** wave cards holding the merge requests. A conflict warning at the top disables
the launch button until the conflicts are resolved.

## Step 12. Launch a rollout

On the same page: pick the **Environment** (`dev`) and press **"Launch rollout"**. Leave the
"Redeploy" switch off.

The rollout page opens: steps by wave, their states, the events. Every rollout is under
**Rollouts**.

**If the launch refuses**, the message says why, and it is always one of three: no deploy target for
the repository, a merge request that fails the readiness gate, or a stale plan that needs
recalculating.

## Step 13. Webhooks instead of polling

Once the chain has run end to end on polling, move to webhooks - events then arrive in seconds
rather than on a timer.

**A connection's type is fixed at creation**, so the move means a new connection of type
`gitlab-webhook` / `yandextracker-webhook`, and the repositories have to be re-registered against it -
better decided before history accumulates.

Where to point them:

| Provider | URL (on the **ingress** service) | Secret header |
| :-- | :-- | :-- |
| GitLab | `POST https://ingress.company.com/webhooks/gitlab/gitlab-main` | `X-Gitlab-Token` |
| Yandex.Tracker | `POST https://ingress.company.com/webhooks/tracker/tracker-main` | `X-Tracker-Token` |

The last URL segment is the **connection name**, which is why it is restricted to Latin characters.

The secret is ingress configuration, not a UI field:

```bash
Webhooks__GitLab__gitlab-main__Token=<the secret you gave GitLab>
Webhooks__Tracker__tracker-main__Token=<the tracker's secret>
```

A wrong or missing secret is `401` with no body. An unknown connection name is also `401`, so the
difference in answers cannot be used to enumerate connections.

---

# Part B. What to configure after the first success

## B1. Readiness rules

**Administration → Readiness Rules**

What a merge request must show before it may be deployed at all. A rule is a set of **signals**
combined with "all of" or "any of".

A signal is a `kind:value` token:

| Kind | Example | Where it comes from |
| :-- | :-- | :-- |
| `label:` | `label:ready-prod` | The merge request's labels |
| `mr-status:` | `mr-status:merged` | Its normalized status |
| `pipeline:` | `pipeline:success` | The latest pipeline result (polling has it; a webhook does not) |

Under the field there is a **clickable vocabulary**: chips for every status and for every label your
merge requests actually carry. A rule requiring a signal nobody has is a gate nobody passes, and you
find out at launch.

Example: Name `prod-gate`, Match `All of`, Required signals `label:ready-prod pipeline:success`.

The rule is then assigned to an **environment** (the Gate field on Environments) and optionally
overridden on a specific **deploy target**.

The gate blocks the **whole launch** rather than filtering the unready ones out: a task's merge
requests have ordering dependencies, so deploying some and quietly dropping the rest is a partial
rollout that reads as complete.

The targeted exception is a **pin** on one merge request: the pin icon on its row on the task page,
per environment. Requires `release.plan.approve`.

## B2. Deploy order

**Administration → Default Plan**

The order is configured once and applies to every task. Two editors, **one document**: the visual
form builds YAML, which is immediately read back by the same reader the planner uses.

```yaml
version: 1

groups:
  db:
    repositories: ["my-group/migrations"]
  backend:
    repositories: ["my-group/backend"]
  frontend:
    repositories: ["my-group/web"]

order:
  - group: backend
    needs: [db]
    type: hard        # hard is a real constraint; soft is a preference that yields on a cycle
  - group: frontend
    needs: [backend]
    type: soft
```

Selectors take globs - `repositories: ["my-group/svc-*"]` - plus the `connectors`, `branches`,
`tasks.keys` and `tasks.labels` axes and a nested `exclude`. The language is described in full in
[012 - Ordering rules](../issues/012-ordering-rules.md) (Russian).

The task wait policy lives here too:

```yaml
tasks:
  wait_for_subtasks: true
  wait_for_linked: true
  group_order: together      # together | subtasks_first | linked_first
```

The page shows the resulting waves immediately, and any conflicts separately.

## B3. Action handlers

**Administration → Action Handlers**

An "event → action" binding.

The events the service actually raises: `RolloutSucceeded`, `RolloutFailed` (the rollout stopped on a
failed step), `RolloutCancelled`, `RolloutChanged`.

The actions:

| Action type | Settings |
| :-- | :-- |
| `telegram` | `botToken` (secret), `chatId`, `template` |
| `tracker-status` | `status` - the target status in your tracker's own vocabulary |
| `tracker-comment` | `template` |

Templates take `{task}`, `{environment}`, `{status}` and `{event}`.

**Scope (optional)** narrows a binding: `env:prod` for prod rollouts only, `task:PROJ-42` for one
task. An empty scope matches every event of that type.

Example: Event `RolloutSucceeded`, Action `tracker-comment`, Scope `env:prod`, template
`Deployed to {environment}`.

A failing action is isolated: it is logged and fails neither the rollout step nor the sibling
bindings.

## B4. Permissions

**Administration → Permissions → "Add mapping"**

A mapping from an AD group SID to a permission. Paste the whole SID (`S-1-5-21-…`) and pick the
permission from step 1. A holder of `config.edit` can grant anything to anyone including themselves,
which is why every change here lands in the **Request Audit**.

---

# Part C. When something is wrong

Find the symptom. Nearly everything is diagnosed by the Ingestion screen plus the two "Poll now"
buttons.

### No tasks at all

- Is the tracker connection type `yandextracker-webhook`? Then tasks arrive only by webhook; it
  searches for nothing itself. Discovery needs a `-poll` type.
- A `-poll` connection with **Queues to sweep empty** has nowhere to look. Press "Poll now": it
  names the reason.
- Is the tracker-poll worker *off* on the Ingestion screen? Then polling is disabled by
  configuration (`TrackerPolling__Enabled`). It is on by default, every 60 s.
- A rejected token or organization id also comes back through "Poll now".

### Tasks arrive, merge requests do not

- **The repository is not registered** (step 4) - by far the most common cause, with a distinctive
  sign: the "merge requests seen" counter on Ingestion climbs while Work stays empty. The log holds
  `Repository not found for connection=…, path=…`.
- **External ID is not the full path.** GitLab wants `group/project`; `backend` is a 404. Such a
  repository is named in the Problem column of the last poll.
- The merge request is closed or already merged - a poll only takes the open ones.

### Merge requests arrive but link to no task

- The linking rule (step 2). Open the connection and paste a **real** branch name into the preview:
  it shows at once whether a key comes out.
- The wrong source: the key is in the MR title while the setting says `branch`.
- A lower-case key (`proj-42`) - the default pattern requires upper case.
- The same key exists **in two trackers** - then nothing is linked, deliberately, rather than
  guessing. Fixed by binding the repository to one tracker (see the appendix).

### The plan is empty or will not build

- The task has no merge requests - see above.
- The plan predates recorded waves: "Recalculate the plan before launching". Press Recalculate.
- A conflict warning means the ordering rules contradict each other; the launch is blocked on
  purpose.

### The launch refuses

- **"has no deploy strategy for environment …"** - no deploy target for that repository and
  environment (step 6).
- **The readiness gate** - the merge request lacks the required signals. Check the Readiness column
  under Work, add the label in GitLab and poll again, or pin it (B1).
- **No environments offered** - they are all disabled; enable one under Environments.

### A redeploy does not happen

It needs **both**: the "Redeploy" switch at launch **and** an `Always` policy on the deploy target.
Either alone is not enough - that is the guard against an accidental production redeploy.

### The Ingestion counters reset

By design: they live in the replica's memory and count from its start. They are facts about a live
process, not history. A task's history is on its timeline; request history is in the Request Audit.

---

## Appendix. Fields only the API can set today

**A repository's tracker binding** (`TrackerConnectionId`). The repository form does not offer it,
although the API accepts it. With a single tracker it is unnecessary - a task key is matched
globally. It matters when there are two trackers and the same key (`PROJ-42`) exists in both:
without the binding nothing is linked, rather than the wrong thing.

```
PUT /api/repositories/{id}
{ "name": "Backend", "externalId": "my-group/backend",
  "connectionId": "…", "trackerConnectionId": "…" }
```

Interactive documentation for the whole API is at `/swagger`, where a request can be run from the
browser under your own account.

---

## See also

- [User Guide](user-guide.md) - what each screen is for
- [Getting Started](getting-started.md) - install, Docker Compose, environment variables
- [Configuration](configuration.md) - every environment variable
- [Operations](operations.md) - health endpoints, archival, monitoring
