# 002. Независимость ядра от VCS и трекера

## 1. Цель

Ядро (модель, планировщик, API, UI) не должно знать, что существуют GitLab и Яндекс.Трекер. Поддержка конкретного провайдера — отдельный адаптер, поверх которого можно добавить эндпоинты и клиенты для GitHub, Jira, YouTrack и т. д.

Вопрос «плагины или нет» разбирается в §4. Короткий ответ: **нет, плагины не нужны** — и это вывод не из вкуса, а из того, к чему пришли системы, прошедшие этот путь.

## 2. Фактическая связанность (измерено)

### 2.1. Главное: провайдер-агностичного ядра нет

Ветвления по `VcsType`/`TrackerType` **нет нигде**:

```
$ grep -rn "VcsType\.|TrackerType\." src --include=*.cs --include=*.razor
(пусто)
```

Enum'ы существуют, хранятся в БД и **декоративны**. DI регистрирует ровно одну реализацию на интерфейс:

```csharp
services.AddHttpClient<IVcsApiClient, GitLabApiClient>(...);
services.AddHttpClient<ITrackerApiClient, YandexTrackerApiClient>(...);
```

Это не «ядро с провайдерами», а **один захардкоженный провайдер за интерфейсом**. Механизма выбора не существует — добавление второго GitLab-совместимого хостинга сегодня потребует не только нового класса, но и изобретения способа его выбрать.

Хорошая новость: **шов уже есть**. `IVcsApiClient`/`ITrackerApiClient` — правильная идея, реализованная наполовину.

### 2.2. Провайдерская специфика в контрактах

```csharp
// Application/Services/ITrackerApiClient.cs
Task<TrackerIssueInfo?> GetIssueAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct);
```

Две проблемы в одной строке:

1. **`orgId` — понятие Яндекс.Трекера в общем контракте.** У Jira его нет, будет что-то своё. Это уже течь: контракт описывает не «трекер», а «Яндекс.Трекер».
2. **`apiUrl`, `token` в каждой сигнатуре.** Ни один из изученных аналогов так не делает: Renovate связывает креды с клиентом в `initPlatform`, go-scm держит `BaseURL` в `Client`. Прокидывание конфигурации через порт означает, что вызывающий обязан знать, что нужно провайдеру, — то есть абстракция не абстрагирует.

### 2.3. Провайдерский словарь в домене

Это моя собственная ошибка, внесённая при исправлении дублирования. Правила были размазаны по двум копиям, которые разошлись; я свёл их в один источник — верно — но поместил в **Core**, то есть в домен:

| Файл | Что течёт |
|---|---|
| `Core/Parsing/MergeRequestStatusResolver.cs:15` | `"opened"`, `"merged"`, `"closed"` — словарь GitLab. GitHub: `open`/`closed` + флаг `merged`. Bitbucket: `OPEN`/`MERGED`/`DECLINED` |
| `Core/Parsing/TaskStatusRules.cs:12` | `closed`, `cancelled`, `rejected`, `resolved` — словарь Яндекс.Трекера. Jira: настраиваемые workflow-статусы |
| `Core/Parsing/BranchTaskParser.cs:17` | `[A-Z][A-Z0-9]*-\d+` — формат ключа Jira/Яндекса. GitHub Issues: `#123`. Это диалект, а не универсальное правило |
| `Core/Enums/VcsType.cs`, `TrackerType.cs` | `GitLab = 1`, `YandexTracker = 1` — имена провайдеров в домене |
| `Core/Entities/VcsConnection.cs` | `ReadyForDeployLabel` — лейблы есть у GitLab/GitHub, но не у всякого VCS |

**Дедупликация была правильной, дом — нет.** Эти правила принадлежат адаптеру провайдера: у каждого провайдера свой словарь, и общего значения у них нет.

### 2.4. Остальные протечки

| Где | Что |
|---|---|
| `Ingress/Endpoints/*` | Два захардкоженных эндпоинта, две модели payload, разные заголовки секрета (`X-Gitlab-Token`, `X-Tracker-Token`). Абстракции нет |
| `ReleasePlanner.ParseMrId` | Формат `connection:project/path!iid` — `path_with_namespace` + `iid` суть понятия GitLab |
| `Repository.ExternalId` | Хранит GitLab-путь; в PWA подпись поля — «GitLab project ID or path» |
| `Pwa/Pages/Admin/*.razor` | `<option value="GitLab">`, `<option value="YandexTracker">` захардкожены |
| `TrackerConnection.OrgId` | Поле Яндекса в общей сущности |

### 2.5. Сводка

| Слой | Состояние |
|---|---|
| `Core` (домен) | ❌ Знает GitLab и Яндекс: enum'ы, словари статусов, формат ключа задачи |
| `Application` (порты) | ⚠️ Порты есть, но с `orgId` и с кредами в сигнатурах |
| `Infrastructure` | ⚠️ Один провайдер на интерфейс, выбора нет |
| `Ingress` | ❌ Полностью провайдероспецифичен |
| `Pwa` | ❌ Хардкод в выпадающих списках |

## 3. Как это решают другие

Исследование проводилось по исходникам и документации; ссылки — в конце раздела. То, что подтвердить не удалось, помечено.

### 3.1. Renovate — самый близкий аналог

Поддерживает 11 платформ (github, gitlab, bitbucket, azure, gerrit, gitea, forgejo, codecommit…). Три вещи стоит взять:

**Двухфазная инициализация.** `initPlatform(params) → PlatformResult` (endpoint, token, gitAuthor) и затем `initRepo(params) → RepoResult`. Креды связываются с клиентом на этой фазе и **не появляются в сигнатурах методов** — прямое лекарство от §2.2.

**Два слоя абстракции.** Есть `Platform` (PR, комментарии, статусы, лейблы) и **отдельно** `PlatformScm` (ветки, коммиты, merge). Ключевое: `PlatformScm` для большинства платформ — общий `DefaultGitScm` поверх обычного git; override только у `gerrit`, `github` и `local`. **Половина «работы с VCS» вообще не зависит от хостинга** и не должна быть в интерфейсе провайдера.

**Runtime-детект версии.** Внутри GitLab-провайдера:

```typescript
if (semver.lt(defaults.version, '13.9.0')) {
  logger.warn('Adding reviewers is only available in GitLab 13.9 and onwards');
  return;
}
const useMergeTrain = config.mergeTrainsEnabled && !semver.lt(defaults.version, '17.11.0');
```

Для нас это **обязательно, а не опционально**: self-hosted GitLab бывает любой версии, и Renovate — живое доказательство, что без этого не обойтись.

Реестр — статическая мапа с fail-fast:

```typescript
export function setPlatformApi(name: PlatformId): void {
  if (!platforms.has(name)) {
    throw new Error(`Init: Platform "${name}" not found. Must be one of: ${getPlatformList().join(', ')}`);
  }
}
```

Чего **не** брать: интерфейс на ~40 методов. Он накоплен за годы под задачу обновления зависимостей (issues, vulnerability alerts, CODEOWNERS). Начинать надо с того, что реально зовёт планировщик.

### 3.2. go-scm (Drone/Harness) — нарезка контракта

Не один богатый интерфейс, а **13 узких сервисов на одном клиенте**: `Contents`, `Git`, `Issues`, `PullRequests`, `Repositories`, `Webhooks`, `Reviews`… `BaseURL` живёт в клиенте, а не в аргументах.

**Вебхуки — самое ценное:**

```go
WebhookService interface {
    Parse(req *http.Request, fn SecretFunc) (Webhook, error)
}
SecretFunc func(webhook Webhook) (string, error)
```

Двухфазно: сначала распарсили и поняли, **какой это репозиторий**, потом достали секрет именно для него, потом проверили подпись. Это ровно наша задача — у каждого `VcsConnection` свой секрет — и объясняет, почему `Parse` принимает callback, а не готовый секрет.

**Нормализованный словарь** вместо сырых строк: `Action` (`ActionOpen`, `ActionSync`, `ActionMerge`, `ActionClose`, `ActionLabel`), `State`, `Visibility`.

Capability — sentinel-ошибка `ErrNotSupported`. Работает, но узнаётся только после вызова: планировщику этого мало.

### 3.3. Atlantis — capability-предикат

```go
SupportsSingleFileDownload(repo models.Repo) bool
```

Единственный найденный пример явного capability-предиката **в самом интерфейсе**, и он **параметризован репозиторием**, а не глобальный. Это точно наш случай: возможности зависят от конкретного соединения (версия self-hosted GitLab), а не от «GitLab вообще».

Диспетчер — `clients map[VCSHostType]Client`, ненастроенные хосты получают `NotConfiguredVCSClient` — null-object, который **шумит ошибкой**, а не молчит.

### 3.4. Backstage — важный отрицательный пример

`ScmIntegration` в Backstage почти пуст: `type`, `title`, `resolveUrl()`, `resolveEditUrl()`. **Унифицированной абстракции VCS-операций там нет** — только «хост + auth + URL», а реальная работа с GitHub/GitLab живёт внутри плагинов с провайдероспецифичными клиентами.

Реестр закрыт хардкодом (`ScmIntegrations.fromConfig()` — 12 интеграций). Запрос на регистрацию кастомной SCM-интеграции ([#23706](https://github.com/backstage/backstage/issues/23706)) закрыт как **not planned** — и это платформа, целиком построенная на плагинах.

Что взять: **отдельный пакет только под контракты** (`plugin-*-node`) — прямой аналог contract-сборки в .NET, он же решает версионирование.

### 3.5. Mergify — сознательный отказ

Merge queue + rules engine, функционально близко к нашему оркестратору. **VCS не абстрагирует**: продукт GitHub-центричный, GitLab поддержан только как CI, репортящий статусы.

Коммерчески успешный продукт в нашей нише посчитал мультиплатформенность не стоящей цены. Это не аргумент «не абстрагировать» — это аргумент **не платить за абстракцию больше, чем она стоит**.

> Не проверено: вывод из документации, кодовая база Mergify частично закрыта.

### 3.6. Трекеры: общий знаменатель узкий

| Система | Модель связей |
|---|---|
| **Jira** | `issueLinkType` = `name` + `inward` + `outward` (name=`Blocks`, inward=`is blocked by`, outward=`blocks`) |
| **Яндекс.Трекер** | `relationship` + `direction`; значения: `relates`, `depends on`, `is dependent by`, `is subtask for`, `is parent task for`, `duplicates`, `is epic of`… |
| **Linear** | Плоский enum: `blocks \| duplicate \| related \| similar` |

**Яндекс.Трекер — это модель Jira.** Те же inward/outward, то же `direction`. Адаптер Jira будет почти изоморфен существующему.

Пересечение, пригодное для топологической сортировки:

- **`depends on` / `blocks`** — есть везде. **Единственное ребро, нужное для плана выкатки**
- `parent`/`subtask` — есть везде, но это иерархия, не порядок
- `relates` — есть везде, семантики не несёт, **для графа вреден** (ложные рёбра)
- `duplicates`, `epic` — для плана бесполезны

Важное предупреждение Atlassian: связи «bidirectional, however, the semantics of each direction is only interpretable at the user interface level and not at the API level». **Направление ребра обязан вычислять адаптер** — API его не гарантирует.

**Вывод:** наш текущий `TrackerIssueDependency(IssueKey, DependsOnKey)` **уже правильный** — редкий случай, когда существующий код совпал с тем, к чему пришла индустрия. Менять не нужно.

> Не проверено: дословный JSON ответов Яндекс.Трекера не видел — список `relationship` из поисковой выдачи по официальной документации. Перед реализацией адаптера проверить против живого API.

### 3.7. Нормализация событий: CloudEvents

CNCF graduated (январь 2024). Обязательные атрибуты: `id`, `source`, `specversion`, `type`.

Ценно ровно то, что закрывает **наши существующие** проблемы:

- **`source` + `id` → дедупликация.** «Producers MUST ensure that `source` + `id` is unique for each distinct event». GitLab ретраит вебхуки — это наша реальная боль, здесь она закрывается бесплатно
- **`type` → ключ роутинга** на консьюмеры, которые уже есть
- **`dataschema` → версионирование payload**

Argo Events устроен ровно так: EventSource принимает нативный payload, проверяет HMAC, оборачивает в CloudEvent, кладёт в шину. **Вся провайдероспецифика умирает в Ingress** — это прямой архитектурный образец для нашего `Ingress.Webhooks`.

Брать **форму конверта, а не SDK**: Argo нужен CloudEvents по проводу между чужими системами, у нас Ingress и консьюмеры — один солюшн.

CDEvents (надстройка над CloudEvents для CI/CD от CD Foundation) — взять принцип именования типов, но не словарь: он про pipeline/artifact/deployment, а наши события про MR и задачи.

### Ссылки

Renovate: [types.ts](https://github.com/renovatebot/renovate/blob/main/lib/modules/platform/types.ts) · [index.ts](https://github.com/renovatebot/renovate/blob/main/lib/modules/platform/index.ts) · [scm.ts](https://github.com/renovatebot/renovate/blob/main/lib/modules/platform/scm.ts) · [gitlab/index.ts](https://github.com/renovatebot/renovate/blob/main/lib/modules/platform/gitlab/index.ts)
go-scm: [client.go](https://github.com/drone/go-scm/blob/master/scm/client.go) · [webhook.go](https://github.com/drone/go-scm/blob/master/scm/webhook.go) · [const.go](https://github.com/drone/go-scm/blob/master/scm/const.go)
Atlantis: [client.go](https://github.com/runatlantis/atlantis/blob/main/server/events/vcs/client.go) · [proxy.go](https://github.com/runatlantis/atlantis/blob/main/server/events/vcs/proxy.go)
Backstage: [ScmIntegrations.ts](https://github.com/backstage/backstage/blob/master/packages/integration/src/ScmIntegrations.ts) · [#23706](https://github.com/backstage/backstage/issues/23706)
События: [CloudEvents spec](https://github.com/cloudevents/spec/blob/main/cloudevents/spec.md) · [Argo Events EventSource](https://argoproj.github.io/argo-events/concepts/event_source/) · [CDEvents](https://cdevents.dev/)
Трекеры: [Jira issue linking model](https://developer.atlassian.com/cloud/jira/platform/issue-linking-model/) · [Яндекс.Трекер: связи](https://yandex.ru/support/tracker/ru/concepts/issues/get-links) · [Linear relations](https://linear.app/docs/issue-relations)
.NET: [Keyed services](https://andrewlock.net/exploring-the-dotnet-8-preview-keyed-services-dependency-injection-support/) · [App with plugin support](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support) · [Orchard Core: Modules](https://docs.orchardcore.net/en/latest/reference/modules/Modules/) · [Orchard 1.x dynamic compilation](https://docs.orchardcore.net/projects/O1/en/rtd/Documentation/Orchard-module-loader-and-dynamic-compilation/)

## 4. Плагины: нет

Вопрос стоял прямо: «может быть, это реализуется плагинами». Ответ — нет, и вот почему.

| Модель | Кто так делает | Применимость к нам |
|---|---|---|
| **In-process DI (keyed services)** | Renovate, Backstage(SCM), Atlantis, go-scm, **Orchard Core** | ✅ **Да** |
| **Динамическая загрузка сборок (ALC)** | Jenkins, ABP PlugInSources, **Orchard 1.x** | ❌ Нет |
| **Out-of-process gRPC** | Terraform, Crossplane | ❌ Нет |
| **Отдельный сервис-адаптер** | Argo Events | ⚠️ Только по особой нужде |

**Главный аргумент: динамическая загрузка и контейнерный деплой взаимно уничтожают выгоду друг друга.** Единственная причина для ALC — «добавить провайдера, не пересобирая». Но образ пересобирается на каждое изменение. Мы заплатили бы полную цену за ноль выгоды.

Цена, если бы платили:

- **Грабли Jenkins.** Per-plugin classloader не даёт полной изоляции: diamond-конфликты, `NoSuchMethodError`/`ClassNotFoundException`, wrapper-плагины и shading как обходные пути. В .NET получим то же самое, только `MissingMethodException`
- **Ловушка контракт-сборки.** Если `PluginBase.dll` окажется в папке плагина, рантайм сочтёт интерфейс **другим типом** и каст не пройдёт. Лечится `ExcludeAssets=runtime`, о котором надо знать заранее
- **Опыт ABP:** «you need to copy all DLL dependencies to the plug-in folder», миграции БД плагинов — руками
- **ALC не является границей безопасности.** Прямая цитата MS: «Untrusted code cannot be safely loaded into a trusted .NET process»
- **Выгрузка не гарантирована.** Autofac про ALC-скоупы: «best-effort attempt to remove all references we hold»

**Решающий довод — Orchard Core.** Orchard 1.x имел полноценную динамику: компиляция модулей в рантайме, `~/App_Data/Dependencies`, перезагрузка «as if the application was starting up again». **Orchard Core это выбросил.** Модули стали обычными class libraries; MSBuild навешивает атрибуты на этапе сборки, рантайм лишь **обнаруживает** их среди уже загруженных сборок. Динамика осталась только на уровне включения/выключения фич по тенантам.

Путь индустрии: dynamic loading → compile-time refs + runtime feature toggles. Не надо повторять пройденный чужой путь.

**MEF/System.Composition** отдельно: порт частичный, directory catalogs — то, ради чего его берут — **отсутствуют**. Мёртвый вариант.

**Out-of-process gRPC** оправдан, когда провайдеры пишут третьи лица и это недоверенный код (Terraform — тысячи провайдеров). У нас 2 провайдера, оба свои, в одном процессе. Цена не окупается на два порядка.

## 5. Рекомендация

### 5.1. Целевая структура

```
ReleaseOrchestrator.Core                 # домен: ноль знаний о провайдерах
ReleaseOrchestrator.Providers.Abstractions   # контракты провайдеров (новая сборка)
ReleaseOrchestrator.Providers.GitLab         # адаптер: клиент + вебхук + словари
ReleaseOrchestrator.Providers.YandexTracker  # адаптер
ReleaseOrchestrator.Providers.GitHub         # когда понадобится
```

Отдельная сборка под контракты — приём Backstage (`plugin-*-node`): у неё своя версия, и ломающее изменение видно на `dotnet build`, а не в рантайме.

### 5.2. Контракт: креды связываются с клиентом, а не текут в методы

Было:
```csharp
Task<VcsApiMrInfo?> GetMergeRequestAsync(string apiUrl, string token, string projectPath, string iid, CancellationToken ct);
Task<TrackerIssueInfo?> GetIssueAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct);
```

Станет:
```csharp
public interface IVcsProvider          // уже привязан к соединению
{
    VcsCapabilities Capabilities { get; }
    Task<VcsMergeRequest?> GetMergeRequestAsync(string projectPath, string iid, CancellationToken ct);
    Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(string projectPath, CancellationToken ct);
}

public interface IVcsProviderFactory { Task<IVcsProvider> CreateAsync(VcsConnection conn, CancellationToken ct); }
```

`orgId` уезжает в типизированный `YandexTrackerOptions` **внутри адаптера**. В контракте его нет.

Начинать с 6–8 методов, которые реально зовёт планировщик, и растить по факту. Renovate пришёл к 40 методам итеративно, а не спроектировал их заранее.

### 5.3. Выбор провайдера: keyed services + фабрика

```csharp
services.AddKeyedScoped<IVcsProvider, GitLabProvider>("gitlab");
services.AddKeyedScoped<ITrackerProvider, YandexTrackerProvider>("yandex");
```

Ключ приходит **из БД** (`VcsConnection.ProviderType`), поэтому `[FromKeyedServices]` не подойдёт — он только для compile-time ключей. Нужен `GetRequiredKeyedService`, спрятанный в фабрику, с fail-fast и перечислением доступных — как `setPlatformApi` в Renovate.

Следствие: `VcsType`/`TrackerType` как enum'ы **удаляются**. Тип провайдера становится строкой — добавление провайдера перестаёт быть изменением домена и миграцией БД.

### 5.4. Capabilities: три механизма под три разных вопроса

| Вопрос | Механизм | Прообраз |
|---|---|---|
| «Провайдер вообще умеет?» | Отдельный интерфейс + `is`-проверка | Renovate `commitFiles?` |
| «Эта инсталляция умеет?» | `VcsCapabilities` — объект флагов, **параметризованный соединением** | Atlantis `SupportsSingleFileDownload(repo)` |
| «Версия сервера достаточная?» | Детект при инициализации: дёрнуть `/version`, закешировать | Renovate `semver.lt(version, '13.9.0')` |

Третий пункт нужен **уже сейчас**, с одним GitLab: self-hosted версии разные.

### 5.5. Ingress: нормализованный конверт

Провайдероспецифика — парсинг payload, `X-Gitlab-Token` против HMAC GitHub, разные схемы секретов — умирает в `Ingress.Webhooks`, как в Argo Events. Дальше по шине едет только нормализованное.

Взять из CloudEvents форму:
- `source` + `id` → **дедупликация** (закрывает существующую боль с ретраями GitLab)
- `type` → роутинг на консьюмеры
- `dataschema` → версионирование payload

Верификация — по модели go-scm `SecretFunc`: распарсили → поняли, какое это соединение → достали его секрет → проверили. Это единственный способ поддержать несколько соединений с разными секретами.

Словарь действий — enum (`Opened`, `Updated`, `Merged`, `Closed`, `Reopened`), а не сырые строки GitLab.

### 5.6. Куда уезжает то, что сейчас в Core

| Сейчас | Куда |
|---|---|
| `MergeRequestStatusResolver.FromVcsState` | В адаптер: у каждого VCS свой словарь состояний |
| `TaskStatusRules.IsClosed` | В адаптер: у Jira настраиваемые workflow-статусы, единого списка нет |
| `BranchTaskParser` | В адаптер трекера **или** в настройку соединения (regex как конфиг): формат ключа — свойство трекера |
| `VcsType`, `TrackerType` | Удалить, заменить строкой |
| `VcsConnection.ReadyForDeployLabel` | Оставить, но как провайдероспецифичный конфиг, а не поле общей сущности |

В Core остаются `MergeRequestStatus` и `TaskItem.Status` как **нормализованные** значения — их задача адаптера и заполнить.

### 5.7. Что НЕ трогать

`TrackerIssueDependency(IssueKey, DependsOnKey)` — уже нормализован до `depends on`, единственного ребра, которое есть во всех трекерах и имеет смысл для топосортировки. Совпадает с выводом индустрии. Не трогать.

## 6. Границы применимости

**Абстракция оправдана, только если второй провайдер реально появится.** Если GitHub/Jira не в роадмапе — достаточно убрать течи (§5.2, §5.6) и остановиться: шов уже есть, раскрыть его позже будет дёшево. Mergify — успешный продукт в этой же нише — сознательно остался GitHub-only.

**Второй провайдер покажет, где абстракция протекает.** До него любые догадки о «правильной» границе — спекуляция. Renovate и Atlantis пришли к своим интерфейсам итеративно. Поэтому §5.2 (убрать течи) стоит делать **сейчас** — это чистая выгода независимо от роадмапа, а §5.3–5.5 — вместе со вторым провайдером, а не «на всякий случай».
