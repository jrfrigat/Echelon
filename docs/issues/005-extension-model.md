# 005. Модель расширения: четыре оси, один механизм

> Черновик от 2026-07-18. Проектный документ, не отчет о сделанном.

Документ [002](002-provider-independence.md) закрыл вопрос "плагины или нет" одним словом: нет. Его вывод -- in-process DI поверх keyed services, compile-time ссылки, никакого ALC -- остается в силе и здесь. Настоящий документ его не пересматривает, а достраивает: показывает, что тот же самый шов, который сегодня работает ровно для одной оси (чтение VCS), нужно клонировать еще на три оси, и делает это честно -- без обещания "нулевых ссылок на провайдер в хосте", которое потребовало бы ровно той динамики, что 002 отверг.

Требование пользователя звучало как "хочу добавлять обработчики" (выкатку, уведомления, источники приема). Это и есть расширяемость -- но в нашей трактовке "добавить обработчик" стоит **новый проект + одна строка `AddX()` + строка-ключ в БД**, а не подгрузка сборки в рантайме. Ниже -- ровно что это значит и где проходит честная граница.

## 1. Один проверенный механизм

Шов чтения VCS ([002](002-provider-independence.md) s.5.3) уже собран из пяти частей и работает. Мы не изобретаем второй механизм расширения -- мы фиксируем этот как канонический и переиспользуем дословно на каждой новой оси.

| Часть | Что делает | Где живет сегодня |
|---|---|---|
| **Строковый ключ в БД** | выбирает реализацию в рантайме; нормализуется через `ProviderKey.Normalize` | `VcsConnection.ProviderType` |
| **Keyed service** | `GetRequiredKeyedService<TAdapter>(key)` достает адаптер по ключу | `Providers.GitLab` регистрирует `AddKeyedScoped` |
| **Маркер регистрации** | singleton-маркер (`XRegistration`), перечисляющий валидные ключи; неизвестный ключ -> `UnknownProviderException`, а не голый промах DI | по образцу `setPlatformApi` из Renovate |
| **Фабрика** | прячет `GetRequiredKeyedService` за `CreateAsync(descriptor)`, отдает порт, привязанный к соединению | `IVcsProviderFactory` |
| **`ProviderSettingSchema[]`** | декларирует настройки соединения, чтобы админ-формы оставались провайдер-агностичными | уже есть у адаптеров |

К этим пяти добавляется шестая, общая для всех осей: **endpoint обнаружения**. Каждая ось отдает свой список зарегистрированных ключей (`GET /api/providers/vcs`, `.../deploy-strategies`, `.../action-types`, `.../ingestion-modes`), и PWA строит выпадающие списки из этого ответа. Это прямое лекарство от захардкоженных `<option value="GitLab">` из [002](002-provider-independence.md) s.2.4: список валиден ровно потому, что его источник -- те же маркеры регистрации, против которых валидируется запись.

Инвариант механизма: **ноль рефлексии, ноль ALC, только `ProjectReference` на этапе компиляции**. Адаптер самрегистрируется одной строкой `AddX()` в корне композиции, и это единственное место, где имя провайдера попадает в код хоста.

## 2. Плагины БЕЗ ALC: цена одного обработчика и честная оговорка

[002](002-provider-independence.md) s.4 разобрал, почему динамическая загрузка сборок и контейнерный деплой взаимно уничтожают выгоду друг друга: единственная причина для ALC -- "добавить провайдера, не пересобирая образ", но образ пересобирается на каждое изменение. Значит, полная цена (грабли Jenkins, ловушка контракт-сборки, невыгружаемость, "ALC не граница безопасности") -- за ноль выгоды. Здесь ничего не меняется.

Что значит "добавить обработчик" в целевой модели -- три шага, ни один из которых не трогает домен и не требует миграции:

```bash
# 1. Новый проект-адаптер (или класс в существующем Providers.*)
#    реализует SPI-контракт своей оси + самрегистрацию AddX()

# 2. Одна строка в корне композиции (InfrastructureExtensions.AddInfrastructure)
services.AddSlackAction();          # AddHttpClient + AddKeyedScoped + AddSingleton(new ActionHandlerRegistration("slack"))

# 3. Строка-ключ в БД (через админку, не через код)
#    ActionBinding { ActionType = "slack", ... }
```

Пересобрали образ -- обработчик доступен. Ни строки в `Core`, ни миграции, ни ветвления по типу провайдера в планировщике или UI.

**Честная оговорка про `ProjectReference` хоста.** Утверждать, что хост "не ссылается на провайдеров", было бы ложью, за которую пришлось бы платить ALC. Хосты (`Web`, `Ingress.Webhooks`, будущие `Workers.Executor`/`Workers.Polling`) обязаны на этапе компиляции ссылаться на `Providers.*` -- иначе адаптеры не окажутся в графе сборки и `AddX()` некому будет вызвать, а keyed service не найдется в DI. Провайдер-агностичными становятся ровно две вещи, не больше:

- **файлы эндпоинтов** -- вместо двух захардкоженных `POST /webhooks/gitlab` и `.../tracker` (см. [002](002-provider-independence.md) s.2.4) один параметрический `POST /webhooks/{connectionName}`, который смотрит `connection.ProviderType` и резолвит keyed-транслятор;
- **захардкоженные маршруты и словари** в UI и Ingress -- уезжают за порт.

Сам граф сборки остается тем же: `Ingress` -> `Providers.GitLab`, `Providers.YandexTracker`. Это компромисс, а не победа, и называть его надо своим именем. Подробнее про приемную сторону -- [008](008-ingestion-and-messaging.md).

## 3. Четыре оси

Расширяемость раскладывается на четыре независимые оси. Ось (a) существует и сохраняется дословно; оси (b), (c), (d) -- новые реестры по тому же шаблону из s.1.

### (a) Чтение VCS -- сохранено

Ключ -- `VcsConnection.ProviderType`. Сегодня все методы провайдера **только читают** (`GetMergeRequestAsync`, `GetOpenMergeRequestsAsync`, `ParseTaskKeyFromBranch`, `GetIssueAsync`, `IsClosedStatus`) -- действия выкатки и записи в трекер нет вовсе. Эта ось не меняется: `IVcsProvider`/`ITrackerProvider`, привязанные к одному соединению, фабрики, `VcsCapabilities`/`TrackerCapabilities` на соединение (версия сервера детектится при подключении), опциональный `ITrackerDependencySource`, пробуемый через `is`. Все следующее -- новые оси, а не переделка этой.

### (b) Стратегия выкатки -- ключ по второму измерению

Принцип выкатки меняется **не от соединения, а от репозитория**: один и тот же GitLab-хост держит репозитории, которые катятся merge-ем MR, и репозитории, которые катятся запуском именованного pipeline. Поэтому ось (b) ключуется по второму измерению -- `Repository.DeployStrategyKey`, с переопределением на уровне `PlanItem` (см. [006](006-per-task-planning.md)). Это единственная ось, чей ключ живет не на соединении.

Ключ стратегии **окружение-независим**: `DeployStrategyKey` не зависит от того, куда катим. Окружение -- это runtime-параметр, а не третье измерение ключа. `DeployContext` несет целевое окружение (его ключ, например `"staging"`/`"prod"`), и стратегия катит MR именно в него; окружение-специфичные параметры (имя pipeline, ветка, переменные) живут в декларированных схемой настройках стратегии, **адресуемых по окружению**. Так одна и та же связка (репозиторий, стратегия) обслуживает все окружения, различая их не выбором реализации, а значением в контексте. Про окружение как ортогональное измерение и его гейт продвижения -- [007](007-execution-engine.md).

```csharp
// предлагаемая форма, не скопированный код
public interface IDeployStrategy
{
    DeployCapabilities Capabilities { get; }
    Task<DeployResult> StartAsync(DeployContext ctx, CancellationToken ct);
    Task<DeployResult> PollAsync(string externalRef, CancellationToken ct);
    Task CancelAsync(string externalRef, CancellationToken ct);
    Task<DeployResult?> ReconcileAsync(DeployContext ctx, CancellationToken ct);
}

public sealed record DeployResult(
    DeployOutcome Outcome,      // Succeeded | Failed | Awaiting | AlreadyDone
    string? ExternalRef = null,
    string? Message = null);
```

Двухфазность `Start` + `Poll` делает асинхронные pipeline-выкатки возобновляемыми: `StartAsync` возвращает `Awaiting` + `ExternalRef`, дальше `PollAsync` опрашивает статус. `ReconcileAsync` позволяет возобновленному шагу **переприсоединиться** к уже запущенному pipeline (найденному по детерминированному ключу идемпотентности), а не запустить его заново. GitLab поставляет `gitlab-merge` (идемпотентно: уже смерженный MR -> `AlreadyDone`) и `gitlab-pipeline` (триггер именованного pipeline -> `Awaiting` + `ExternalRef`).

Идемпотентность здесь -- **контрактное обязательство, а не гарантия типов**: type system не заставит стороннюю стратегию уважать `ExternalRef`. Она проверяется SDK-тестом на соответствие (conformance test), и это ограничение названо прямо -- см. [007](007-execution-engine.md) и раздел рисков там. Комплект: адаптер + фабрика + `DeployStrategyRegistration`, ровно как в s.1.

### (c) Обработчики действий + опциональный `ITrackerMutator`

Telegram -- ни VCS, ни трекер, и это доказывает, что нужна **третья самостоятельная ось**, а не расширение первых двух. Ключ -- `ActionType`.

```csharp
// предлагаемая форма
public interface IActionHandler
{
    string ActionType { get; }
    IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; }
    Task<ActionResult> ExecuteAsync(ActionContext ctx, CancellationToken ct);
}
```

Запись в трекер моделируется НЕ как обязательный метод провайдера, а как **опциональная capability** -- по тому же шаблону, что уже работает для `ITrackerDependencySource`:

```csharp
public interface ITrackerMutator
{
    Task SetStatusAsync(string issueKey, string status, CancellationToken ct);
    Task AddCommentAsync(string issueKey, string body, CancellationToken ct);
}
// пробуется через 'is', а не вызывается вслепую:
if (trackerProvider is ITrackerMutator mutator) { await mutator.AddCommentAsync(...); }
```

Так read-only трекеры остаются read-only: адаптер, не реализующий `ITrackerMutator`, просто не пройдет `is`-проверку, и это видно на компиляции, а не падает в рантайме. Обработчики `tracker-status`/`tracker-comment` пользуются `ITrackerMutator`; `telegram` -- самостоятельный обработчик в `Providers.Telegram`.

Ключевое разделение: **связка "событие -> действие" -- это данные** (строки `ActionBinding`), а **новый ТИП действия -- один класс**. Добавить оповещение в новый чат -- новая строка `ActionBinding` через админку. Добавить принципиально новый канал (например, вебхук в PagerDuty) -- новый `IActionHandler` + `AddX()`. См. [007](007-execution-engine.md) про изоляцию действий от шага выкатки (упавший Telegram НИКОГДА не роняет деплой).

### (d) Прием: push и pull одним нормализованным словарем

Четвертая ось -- источник приема данных, ключ -- `ProviderType` соединения, режим выбирается на соединении (`IngestionMode = Push | Poll`). Два взаимозаменяемых подконтракта, испускающих **одни и те же** нормализованные события, чтобы push и pull не могли разойтись в трактовке "смержено"/"решено":

```csharp
// предлагаемые формы
public interface IWebhookTranslator      // push
{
    bool VerifySignature(HttpRequest req, string secret);   // constant-time, fail-closed
    IReadOnlyList<IHasEventIdentity> Translate(WebhookPayload payload);
}

public interface IPollingSource          // pull
{
    Task<IReadOnlyList<IHasEventIdentity>> PollAsync(IngestionCursor cursor, CancellationToken ct);
}
```

Эта ось вытаскивает провайдер-специфику (`GitLabMergeRequestState`, `BranchTaskParser`, `YandexTrackerStatusRules`) ИЗ `Ingress` за порт -- добивая течи, размеченные в [002](002-provider-independence.md) s.2.3 и s.2.4. Детали дедупликации, детерминированных id поллера и словаря событий -- в [008](008-ingestion-and-messaging.md); здесь важно лишь, что это четвертая инстанция того же механизма, а не особый случай.

### Сводка по осям

| Ось | Ключ | Измерение ключа | SPI (предлагаемый) | Статус |
|---|---|---|---|---|
| (a) чтение VCS/трекера | `ProviderType` | соединение | `IVcsProvider`, `ITrackerProvider` | есть, сохраняется |
| (b) стратегия выкатки | `DeployStrategyKey` | **репозиторий** (override на `PlanItem`) | `IDeployStrategy` | новая |
| (c) обработчик действия | `ActionType` | привязка (`ActionBinding`) | `IActionHandler` (+ опц. `ITrackerMutator`) | новая |
| (d) режим приема | `ProviderType` | соединение (режим Push/Poll) | `IWebhookTranslator`, `IPollingSource` | новая |

Сегодня пригодны к расширению только чтение VCS и чтение трекера. Целевая модель добавляет три реестра по идентичному шаблону -- ничего принципиально нового изобретать не нужно, что и делает клонирование правдоподобным, а не аспирационным.

**Порядок между репозиториями -- это НЕ пятая ось.** Задача охватывает несколько репозиториев, и между ними есть дефолтный редактируемый порядок (репозиторий БД раньше бэкенда раньше фронтенда). Это выражается конфигом `RepositoryDependency` (правила построения плана, редактируемые в админке и YAML), а не новым SPI-контрактом и не реестром реализаций: тут нечего подключать строкой `AddX()`, тут строки данных. Поэтому осей по-прежнему **четыре**, а порядок репозиториев -- это данные, которые потребляет планировщик (см. [006](006-per-task-planning.md)), а не механизм расширения.

## 4. Размещение контрактов: без новой сборки

[002](002-provider-independence.md) s.5.1 предлагал отдельную сборку под контракты (прием Backstage `plugin-*-node`). Целевая модель это **отклоняет** -- но по конкретной причине, а не из лени.

Разбор: отдельная contract-сборка нужна ровно тогда, когда без нее образуется цикл между слоями. Цикл возникает, только если интерфейсы провайдеров положить в `Application` -- тогда `Infrastructure -> Application` и `Providers.* -> Application`, а `Application` захочет знать про события, которые производят `Providers.*`. Мы этого не делаем.

Целевое размещение:

```
Providers.Abstractions/          # уже есть; Infrastructure и Providers.* уже ссылаются
  Vcs/          IVcsProvider, VcsCapabilities, VcsConnectionDescriptor, ...
  Tracker/      ITrackerProvider, ITrackerMutator, ITrackerDependencySource, ...
  Deploy/       IDeployStrategy, DeployContext, DeployResult, DeployStrategyRegistration
  Actions/      IActionHandler, ActionContext, ActionResult, ActionHandlerRegistration
  Ingestion/    IWebhookTranslator, IPollingSource, IHasEventIdentity + нормализованные записи событий

Application/Contracts/           # оркестрационные сообщения Rebus (IMessage), НЕ SPI провайдеров
  Messages/     RolloutLaunched, DeployStepRequested, TaskReadinessChanged, ...
```

Почему это работает без цикла: `Infrastructure` уже ссылается на `Providers.Abstractions`, поэтому **производители** событий (`Providers.*`) и **потребитель** (`Infrastructure`) делят одни и те же record-типы событий без новой сборки. `Core` не ссылается ни на что из этого и остается zero-dependency (см. [004](004-target-architecture.md) -- чек-лист жестких ограничений).

Разграничение по назначению, а не по слою:

| Что | Где | Почему |
|---|---|---|
| SPI-интерфейсы осей + их context/result записи | `Providers.Abstractions/{Vcs,Tracker,Deploy,Actions,Ingestion}` | реализуются в `Providers.*`, потребляются в `Infrastructure`; обе стороны уже видят эту сборку |
| Нормализованные записи событий приема | `Providers.Abstractions/Ingestion` | их производит `Providers.*`, потребляет `Infrastructure` -- общий тип без цикла |
| Оркестрационные сообщения (`IMessage`) | `Application/Contracts/Messages` | это домен-оркестрация, не провайдер-специфика; Rebus `TypeBased` routing уже здесь |

Вторая сборка дублировала бы `ProviderKey` и типы схем ради границы, которой не существует. Отклонено как чистый churn.

## 5. Валидация ключей: при записи и на старте

Строковый ключ -- цена независимости от домена ([002](002-provider-independence.md) s.5.3: тип провайдера становится строкой, добавление провайдера перестает быть миграцией). Но строка -- это опечатка, ждущая случиться. Поэтому валидация двухслойная, и обе точки опираются на один источник истины -- перечень маркеров регистрации.

**Слой 1 -- при записи.** Каждый ключ (`ProviderType`, `DeployStrategyKey`, `ActionType`) проверяется в момент сохранения через API против `Available`-списка соответствующей фабрики. Опечатка валит вызов API немедленно, с телом-подсказкой, а не всплывает через недели во время выкатки:

```csharp
// семантика UnknownProviderException -- перечислять валидное, а не молчать
throw new UnknownProviderException(
    key: "gitlub",
    axis: "deploy-strategy",
    available: ["gitlab-merge", "gitlab-pipeline"]);
// -> "Unknown deploy-strategy key 'gitlub'. Valid keys: gitlab-merge, gitlab-pipeline."
```

**Слой 2 -- на старте.** `HostedService` при запуске перебирает ВСЕ уже сохраненные ключи во всех соединениях, репозиториях и `ActionBinding` и валидирует их против регистраций. Смысл: мис-сидированная prod-строка (или строка, оставшаяся от снятого адаптера) обязана уронить процесс на старте -- fail-fast, -- а не застопорить выкатку в момент, когда оператор нажал "Launch". Это тот же принцип, что `NotConfiguredVCSClient` в Atlantis ([002](002-provider-independence.md) s.3.3): ненастроенный путь **шумит ошибкой, а не молчит**.

Семантика `UnknownProviderException` едина для всех четырех осей: она не голый `KeyNotFoundException` из DI, а доменное исключение, несущее (1) неизвестный ключ, (2) имя оси, (3) перечень валидных ключей. Так и админ-форма при записи, и стартовый валидатор, и рантайм-резолв внутри фабрики дают оператору один и тот же понятный текст -- прямой аналог `setPlatformApi` из Renovate, который бросает `Must be one of: ...`.

Остаточный риск назван честно ([004](004-target-architecture.md), реестр рисков): два валидатора сужают окно, но не закрывают его полностью -- строку можно вписать в БД в обход API между стартами, и тогда ошибка ключа снова станет рантайм-ошибкой на выкатке. Это цена строкового ключа, принятая сознательно вместо enum-а, который вернул бы миграцию на каждый новый провайдер.

## 6. Что это дает и чего не дает

Дает: единый, уже проверенный механизм на всех четырех осях; "добавить обработчик" = проект + строка `AddX()` + строка в БД; ноль изменений домена и миграций при добавлении провайдера/стратегии/действия/источника; провайдер-агностичные админ-формы и эндпоинты приема; fail-fast на опечатку ключа в двух точках.

Не дает: нулевых ссылок хоста на провайдеров (это потребовало бы ALC, отвергнутого в [002](002-provider-independence.md) s.4) -- хосты по-прежнему `ProjectReference`-ят `Providers.*`; провайдер-агностичными становятся только файлы эндпоинтов и захардкоженные маршруты. Type system не гарантирует идемпотентность сторонней стратегии выкатки -- это контракт, проверяемый тестом, а не типом ([007](007-execution-engine.md)). И строковый ключ оставляет узкое окно рантайм-ошибки при записи в обход API.

Границы применимости те же, что в [002](002-provider-independence.md) s.6: раскрывать каждую ось стоит под реальную потребность (второй VCS, вторая стратегия выкатки), а не "на всякий случай". Механизм спроектирован так, чтобы раскрытие было дешевым, а не чтобы раскрыть все сразу.
