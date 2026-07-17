# Ruflo — Claude Code Configuration

## Rules

- Do what has been asked; nothing more, nothing less
- NEVER create files unless absolutely necessary — prefer editing existing files
- NEVER create documentation files unless explicitly requested
- NEVER save working files or tests to root — use `/src`, `/tests`, `/docs`, `/config`, `/scripts`
- ALWAYS read a file before editing it
- NEVER commit secrets, credentials, or .env files
- Keep files under 500 lines
- Validate input at system boundaries
- **Коммиты БЕЗ трейлера co-author** (`Co-authored-by: ...`) — никогда не добавлять
- **XML-документация обязательна** на public типах/членах/параметрах — там, где комментарий говорит больше имени. Держится на ревью, не на компиляторе: CS1591 заглушён (как в Flare), иначе он заставляет писать «Gets or sets the Id» и обесценивает комментарии как жанр
- **Версии пакетов — ТОЛЬКО в `Directory.Packages.props`** (Central Package Management). В `.csproj` — `<PackageReference Include="X" />` без `Version`. Одна правка вместо восьми, и версии не расходятся молча
- **Перед коммитом** — `bash scripts/clean-empty-files.sh`. Незаэкранированный `>` в shell-команде создаёт пустой файл с именем следующего токена (`_success`, `t.ClosedAt`); такие файлы уже трижды попадали в коммиты
- Правки, ломающие публичный контракт, допустимы, если оправданы — но должны быть видны на `dotnet build`, а не в рантайме

## Архитектура

Onion / ports-adapters, зависимости **только внутрь** (эталон — репозиторий Flare):

```
Core (enum'ы, чистый разбор; ноль зависимостей — ни одной)
  ← Application (порты, контракты сообщений, алгоритм планирования — без EF)
      ← Infrastructure (EF-модели, DbContext, адаптеры: MassTransit, Redis, DataProtection)
      ← Providers.Abstractions (контракты провайдеров) ← Providers.GitLab / Providers.YandexTracker
          ← Web (корень композиции, API) / Ingress.Webhooks
```

Правила:

- **Ядро не знает ни об одном конкретном провайдере.** Имена GitLab/YandexTracker, их словари статусов и форматы ключей задач живут в адаптерах, не в `Core`. В домене — только нормализованные значения
- **EF-сущности живут в `Infrastructure/Persistence/Models` и наружу не выходят.** Ни Application, ни Providers.Abstractions их не видят: планировщик принимает `PlanMergeRequest`, фабрики — `*ConnectionDescriptor`. Раньше сущности торчали в обоих, и `ReleasePlanGraph` читал навигации EF — то есть требовал от вызывающего цепочку `Include`, забыть звено в которой означало не ошибку, а пустую коллекцию и молча неверный план
- **Маппинг — атрибутами на модели, а не в `OnModelCreating`.** Открыл модель — видишь ключи, длины, индексы и каскады; бегать по файлам не нужно. Fluent остаётся ровно для двух вещей, которым атрибута не существует: **фильтрованный** индекс (`[Index]` не умеет `HasFilter`) и **конвертация значений** (`MergeRequestStatus` хранится строкой). Обе живут в `Persistence/Configurations` и объясняют в комментарии, почему они там
- **`Restrict` — не конвенция.** Обязательная связь по умолчанию `Cascade`, поэтому каждый `[DeleteBehavior(DeleteBehavior.Restrict)]` — осознанный запрет. Потерять его — значит превратить заблокированное удаление в успешное, уносящее чужие строки. Держится тестом `ModelMappingTests`
- **Дубль индекса `has-pending-model-changes` не ловит.** Атрибут плюс забытая fluent-строка дают два индекса в модели и один в БД — схема идентична, проверка зелёная. Ловится только `ModelMappingTests.NoEntityDeclaresTheSameIndexTwice`
- **Алгоритм планирования (`Application/ReleasePlanning`) не зависит от EF** — он чистый и тестируется без БД. Именно недостижимость этого кода для теста скрыла инверсию рёбер графа до аудита
- **Провайдеры регистрируются на этапе компиляции** (keyed services + фабрика), без динамической загрузки сборок. Обоснование и обзор аналогов — `docs/issues/002-provider-independence.md`
- **Локализация** — `Resources/*.resx` (нейтральная культура = en) + `*.ru.resx`. Логи не локализуются: они для операторов и должны быть на одном языке

## Agent Comms (SendMessage-First Coordination)

Named agents coordinate via `SendMessage`, not polling or shared state.

```
Lead (you) ←→ architect ←→ developer ←→ tester ←→ reviewer
              (named agents message each other directly)
```

### Spawning a Coordinated Team

```javascript
// ALL agents in ONE message, each knows WHO to message next
Agent({ prompt: "Research the codebase. SendMessage findings to 'architect'.",
  subagent_type: "researcher", name: "researcher", run_in_background: true })
Agent({ prompt: "Wait for 'researcher'. Design solution. SendMessage to 'coder'.",
  subagent_type: "system-architect", name: "architect", run_in_background: true })
Agent({ prompt: "Wait for 'architect'. Implement it. SendMessage to 'tester'.",
  subagent_type: "coder", name: "coder", run_in_background: true })
Agent({ prompt: "Wait for 'coder'. Write tests. SendMessage results to 'reviewer'.",
  subagent_type: "tester", name: "tester", run_in_background: true })
Agent({ prompt: "Wait for 'tester'. Review code quality and security.",
  subagent_type: "reviewer", name: "reviewer", run_in_background: true })

// Kick off the pipeline
SendMessage({ to: "researcher", summary: "Start", message: "[task context]" })
```

### Patterns

| Pattern | Flow | Use When |
|---------|------|----------|
| **Pipeline** | A → B → C → D | Sequential dependencies (feature dev) |
| **Fan-out** | Lead → A, B, C → Lead | Independent parallel work (research) |
| **Supervisor** | Lead ↔ workers | Ongoing coordination (complex refactor) |

### Rules

- ALWAYS name agents — `name: "role"` makes them addressable
- ALWAYS include comms instructions in prompts — who to message, what to send
- Spawn ALL agents in ONE message with `run_in_background: true`
- After spawning: STOP, tell user what's running, wait for results
- NEVER poll status — agents message back or complete automatically

## Swarm & Routing

### Config
- **Topology**: hierarchical-mesh (anti-drift)
- **Max Agents**: 15
- **Memory**: hybrid
- **HNSW**: Enabled
- **Neural**: Enabled

```bash
npx @claude-flow/cli@latest swarm init --topology hierarchical --max-agents 8 --strategy specialized
```

### Agent Routing

| Task | Agents | Topology |
|------|--------|----------|
| Bug Fix | researcher, coder, tester | hierarchical |
| Feature | architect, coder, tester, reviewer | hierarchical |
| Refactor | architect, coder, reviewer | hierarchical |
| Performance | perf-engineer, coder | hierarchical |
| Security | security-architect, auditor | hierarchical |

### When to Swarm
- **YES**: 3+ files, new features, cross-module refactoring, API changes, security, performance
- **NO**: single file edits, 1-2 line fixes, docs updates, config changes, questions

### 3-Tier Model Routing

| Tier | Handler | Use Cases |
|------|---------|-----------|
| 1 | Agent Booster (WASM) | Simple transforms — skip LLM, use Edit directly |
| 2 | Haiku | Simple tasks, low complexity |
| 3 | Sonnet/Opus | Architecture, security, complex reasoning |

## Memory & Learning

### Before Any Task
```bash
npx @claude-flow/cli@latest memory search --query "[task keywords]" --namespace patterns
npx @claude-flow/cli@latest hooks route --task "[task description]"
```

### After Success
```bash
npx @claude-flow/cli@latest memory store --namespace patterns --key "[name]" --value "[what worked]"
npx @claude-flow/cli@latest hooks post-task --task-id "[id]" --success true --store-results true
```

### MCP Tools (use `ToolSearch("keyword")` to discover)

| Category | Key Tools |
|----------|-----------|
| **Memory** | `memory_store`, `memory_search`, `memory_search_unified` |
| **Bridge** | `memory_import_claude`, `memory_bridge_status` |
| **Swarm** | `swarm_init`, `swarm_status`, `swarm_health` |
| **Agents** | `agent_spawn`, `agent_list`, `agent_status` |
| **Hooks** | `hooks_route`, `hooks_post-task`, `hooks_worker-dispatch` |
| **Security** | `aidefence_scan`, `aidefence_is_safe`, `aidefence_has_pii` |
| **Hive-Mind** | `hive-mind_init`, `hive-mind_consensus`, `hive-mind_spawn` |

### Background Workers

| Worker | When |
|--------|------|
| `audit` | After security changes |
| `optimize` | After performance work |
| `testgaps` | After adding features |
| `map` | Every 5+ file changes |
| `document` | After API changes |

```bash
npx @claude-flow/cli@latest hooks worker dispatch --trigger audit
```

## Agents

**Core**: `coder`, `reviewer`, `tester`, `planner`, `researcher`
**Architecture**: `system-architect`, `backend-dev`, `mobile-dev`
**Security**: `security-architect`, `security-auditor`
**Performance**: `performance-engineer`, `perf-analyzer`
**Coordination**: `hierarchical-coordinator`, `mesh-coordinator`, `adaptive-coordinator`
**GitHub**: `pr-manager`, `code-review-swarm`, `issue-tracker`, `release-manager`

Any string works as a custom agent type.

## Build & Test

- ALWAYS run tests after code changes
- ALWAYS verify build succeeds before committing

```bash
dotnet build src/ReleaseOrchestrator.sln -v q --nologo   # must be 0 errors, 0 warnings
dotnet test src/ReleaseOrchestrator.sln
bash scripts/clean-empty-files.sh                        # before every commit
```

`TreatWarningsAsErrors=true`, поэтому «0 предупреждений» — не пожелание, а условие сборки.

### Ограничения среды (проверено, не тратьте время заново)

- **`python3` — заглушка Windows Store: печатает `Python`, ничего не делает и возвращает `0`.** Самый опасный вид отказа: скрипт «отработал успешно», файл не изменился. Так молча не применились правки конфигураций EF, а сборка и `has-pending-model-changes` при этом оставались зелёными. Файлы править только Write/Edit; для текстовых замен — `perl -pi -e` или `sed`, и **проверять результат `grep`, а не кодом возврата**
- **nuget.org доступен** (проверено 2026-07-17: `Flare.Theme.VisualStudio 0.7.0` приехал прямо с `api.nuget.org` — источник записан в `.nupkg.metadata`). Раньше здесь стояло «заблокирован прокси (403)», и это уже неверно. База уязвимостей тоже доступна: `NU1900` не срабатывает даже без подавления. Поэтому **NU1902/NU1903 — ошибки и локально**, как в CI: единственным основанием для поблажки была недостижимость патча, и оно отпало. Если прокси снова закроется — вернётся `NU1900` (предупреждение, не ошибка), и это и будет сигналом, что аудит молча ничего не проверил
- **Реестр Docker фильтруется** — образы не собрать, теги не проверить
- **Тесты с БД пишутся — через EF SQLite** (`DataSource=:memory:`, соединение держать открытым: база живёт ровно столько, сколько последнее соединение). Провайдера EF in-memory по-прежнему нет и он не нужен. При EF 9 версии расходились и это было невозможно; переход на .NET 10 ограничение снял. Образец — `tests/.../ReleasePlanning/PlannerTestBase.cs`
- **SQLite — не SQL Server.** Поведение FK и типов совпадает не во всём: цепочки `Include`, фильтрованные индексы и логика запросов проверяются, порядок каскадов — нет. Что не проверено, писать в тесте прямо
- Миграции: `dotnet ef migrations add <Name> --project src/ReleaseOrchestrator.Migrations.MsSql --context AppDbContext`

## CLI Quick Reference

```bash
npx @claude-flow/cli@latest init --wizard           # Setup
npx @claude-flow/cli@latest swarm init --v3-mode     # Start swarm
npx @claude-flow/cli@latest memory search --query "" # Vector search
npx @claude-flow/cli@latest hooks route --task ""    # Route to agent
npx @claude-flow/cli@latest doctor --fix             # Diagnostics
npx @claude-flow/cli@latest security scan            # Security scan
npx @claude-flow/cli@latest performance benchmark    # Benchmarks
```

26 commands, 140+ subcommands. Use `--help` on any command for details.

## Setup

```bash
claude mcp add claude-flow -- npx -y @claude-flow/cli@latest
npx @claude-flow/cli@latest daemon start
npx @claude-flow/cli@latest doctor --fix
```

**Agent tool** handles execution (agents, files, code, git). **MCP tools** handle coordination (swarm, memory, hooks). **CLI** is the same via Bash.
