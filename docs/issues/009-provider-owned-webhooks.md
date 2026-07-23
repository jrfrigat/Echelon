# 009 — Provider-owned webhook ingestion

Status: accepted (synthesis of four competing designs + one exhaustive ground-truth pass; the
adversarial-verify and judge phases were cut short by a session limit, so the two flagged decisions
below are the author's calls, not a scored panel's).

## The ask

A VCS/tracker provider should own its own webhook endpoint —
*"Возможно так же, плагин будет открывать api endpoint для ingress (или пробрасывать запросы в ingress)"*.
Today the endpoint lives in `Ingress.Webhooks`, so adding a provider means editing files outside it.

## The two hard constraints that shaped the answer

1. **A provider must not reference `Application` or Rebus.** `Application.csproj` already references
   `Providers.Abstractions`, so `Providers.* → Application` is a **build-graph cycle** — a
   `dotnet build` failure, not a style nit. The webhook handler today uses `IBus` and the
   `Application.Contracts.Messages` records; a provider cannot.

2. **The ingress has no database.** `Ingress.Webhooks` references `Application` + the two provider
   projects, and *not* `Infrastructure` — no `DbContext`. So it cannot resolve a connection's
   `ProviderType` from a bare `/webhooks/{connectionName}` route. The provider **must** stay in the
   URL. This is why docs 005/008's single-segment route is unbuildable, and why the route stays
   `/webhooks/{providerType}/{connectionName}`.

## Decision

The provider owns **parsing and verification**; the host owns **transport and the Rebus hop**.

New folder `Providers.Abstractions/Ingestion/` (contracts only, references Core + BCL, no ASP.NET,
no EF, no Rebus):

- `WebhookRequest` — neutral: `ReadOnlyMemory<byte> Body`, a header view (`IReadOnlyDictionary`),
  and the sanitized `ConnectionName`. **No `HttpRequest`.**
- `IngestionEvent` family — neutral records the parser returns (`MrOpenedEvent`,
  `MrStatusChangedEvent`, `TaskCreatedEvent`, `TaskStatusChangedEvent`). A **parallel** family to
  the Rebus messages, deliberately: moving the Rebus records down into Abstractions would change
  their assembly-qualified type name and break the wire contract during a rolling deploy
  (`MapAssemblyDerivedFrom<IMessage>` scans `Application`). The host maps neutral → Rebus.
- `IWebhookParser` — keyed by provider type. `bool Authenticate(WebhookRequest, string? secret)` +
  `IReadOnlyList<IngestionEvent> Parse(WebhookRequest)`. Verification lives here because it is
  provider-specific in general (shared-secret today; HMAC-over-body for a future git host) — this is
  ADR 008's `SecretFunc` two-phase model.
- `WebhookParserRegistration` — enumerable marker, mirrors `VcsProviderRegistration`, so the host
  can list which keys exist.
- `WebhookSignatures` — a constant-time compare helper (BCL crypto). Same bar as the pure helpers
  Abstractions already carries (`ProviderKey.Normalize`, `ProviderSettingsBag.Validate`).

The host (`Ingress.Webhooks`) keeps and becomes fully generic:

- One endpoint builder that enumerates registered parsers and maps
  `/webhooks/{providerType}/{connectionName}` — no provider named in the file.
- Connection-name sanitization, secret resolution from config, and the neutral→Rebus mapping +
  `bus.Send`. The provider never sees Rebus or `Application`.
- The pipeline order (rate limiter, exception handler, request logging) is unchanged, so the
  security and 503-not-500 properties survive.

The ingress must gain a **compile-time DI registration** for the parsers — it has none today; it
reaches provider code only through public statics. Each provider ships an `AddXWebhookParser()`.

## Why not the others

- **endpoint-contributor (new `Providers.Hosting` with `FrameworkReference AspNetCore`)** — the most
  literal reading of "plugin opens an endpoint", but it puts ASP.NET into the provider dependency
  chain, turning replaceable adapters into web-hostable ones. That erodes the exact property
  `Providers.GitLab.csproj` says the split exists to buy, for no capability the neutral-parser design
  lacks. Rejected.
- **declarative-route (provider declares route metadata + pure parse fn)** — same core as the
  chosen design but with a longer flow and route metadata that duplicates what registration already
  knows. More indirection, no gain.
- **minimal (`IWebhookParser`, verification stays in host)** — nearly the chosen design; differs only
  in leaving verification in the host. Rejected on future-fit: ADR 008 already decided provider-owned
  verification, and the next git host needs HMAC, which the host cannot do generically.

## Two flagged decisions (deployment-affecting)

The tracker spells itself **four** ways: route `tracker`, config `Webhooks:Tracker`, `Source`
prefix `yandex/`, registered key `yandextracker`. (GitLab is consistent: `gitlab` throughout.)

- **D1 — route segment & config key.** Keeping segment `tracker` and config section `Tracker` is
  non-breaking (config keys are case-insensitive). Unifying on the registered key `yandextracker`
  would 401 every existing tracker webhook until its config key is renamed. **Recommendation:
  provider declares its own segment/config-section, preserving today's strings. Non-breaking.**
- **D2 — `Source` prefix.** Deriving `Source` from the provider key would change the tracker's
  prefix from `yandex/` to `yandextracker/`, orphaning its `ProcessedEvent` rows. The tracker sets
  `EventId=""` today (no effective dedup), so the orphaning is nearly harmless — but it is a
  forward-only change to the audit trail. **Recommendation: provider declares its own `Source`
  prefix, preserving `yandex/`. Non-breaking. Fixing the inconsistency is a separate, opt-in
  migration.**

Net: the refactor is **non-breaking** — each provider declares the strings it uses today, so the
inconsistency moves from scattered-in-the-ingress to owned-in-one-place, and the cleanup becomes an
independent decision rather than a side effect.

## Composition with the in-flight readiness work

`MrStatusChanged` is gaining `Labels` + `ObservedAt`. The neutral `MrStatusChangedEvent` carries
them too; the GitLab parser is where the dual-location label extraction lives, normalized through
`Core.Parsing.LabelSet` (today the ingress forks label normalization — it does not call `LabelSet` —
which this refactor is the moment to fix).

## Tests that prove it

- A parser round-trips a real GitLab MR payload → the right `IngestionEvent`s, with labels
  normalized via `LabelSet`.
- Unknown-connection and wrong-token produce an identical 401 (no probing) — host-level.
- The constant-time compare rejects a length-probe.
- The same provider backing two connections with different tokens authenticates each independently.
- A registered parser is actually mapped (guards the silent unmapped-endpoint blind spot: a request
  with no matched endpoint is logged at Verbose and never seen).
- **Must be verified against a running ingress**, because there is no integration-test host and the
  unit test project does not reference `Ingress.Webhooks`.
