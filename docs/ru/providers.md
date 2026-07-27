# Архитектура поставщиков (провайдеров)

> [English version ->](../en/providers.md) - [← К оглавлению](../README.md)

## Обзор

**Провайдер** — это адаптер, приводящий один диалект (эндпоинты API, аутентификацию, строки статусов)
к общему порту. ReleaseOrchestrator регистрирует провайдеров через **keyed DI на этапе компиляции**, а
не через динамическое обнаружение или плагины.

### Почему compile-time DI?

- **Fail-fast** — неизвестный тип провайдера отлавливается при валидации ввода в API, а не в рантайме.
- **Без рефлексии** — никакого сканирования атрибутов и загрузки сборок.
- **Явные зависимости** — корень композиции перечисляет всех зарегистрированных провайдеров.
- **Фиксированные версии** — версии пакетов провайдеров живут в `Directory.Packages.props`.

### Шов

Провайдер разбит на два порта, чтобы привязка к соединению могла делать I/O (определить версию
self-hosted сервера) и всё же отдавать вызывающему готовый к работе объект:

- **`IVcsProviderAdapter` / `ITrackerProviderAdapter`** — keyed по типу провайдера, хранящемуся на
  соединении. `ConnectAsync(context, ct)` привязывается к одному соединению и возвращает провайдера.
- **`IVcsProvider` / `ITrackerProvider`** — уже привязаны к соединению; ни один метод не принимает
  API URL или токен — экземпляр их уже знает.

Keyed DI умеет резолвить по ключу, но не умеет *перечислять* ключи, поэтому каждый провайдер также
регистрирует **маркер-запись** (`VcsProviderRegistration` / `TrackerProviderRegistration`). Именно она
позволяет фабрике ответить «должно быть одним из: gitlab-webhook, gitlab-poll» и позволяет API
проверить тип провайдера до записи в БД.

## Настройки провайдера

Соединение несёт настройки провайдера как непрозрачный «мешок» (`ProviderSettingsJson` на сущности).
Адаптер объявляет, что это за настройки, через `SettingsSchema`, а форма админки строится по этой
схеме — ничто в UI не называет провайдера или конкретное поле.

```csharp
public enum ProviderSettingKind { Text = 0, Int = 1, Enum = 2, Regex = 3 }

public sealed record ProviderSettingSchema(
    string Key,                              // например "orgId"
    string Label,                            // "Organization ID" (для пользователя)
    string? Description = null,
    bool Required = false,                   // без него соединение не сохранить
    bool Secret = false,                     // write-only: API не возвращает значение
    ProviderSettingKind Kind = ProviderSettingKind.Text,
    IReadOnlyList<string>? Options = null,   // для Kind = Enum
    string? Default = null,                  // предзаполняет форму
    int? Min = null,                         // для Kind = Int
    int? Max = null);
```

`GET /api/providers/vcs` и `GET /api/providers/trackers` возвращают тип каждого зарегистрированного
провайдера и его схему. Например, список трекеров:

```json
[
  {
    "ProviderType": "yandextracker",
    "Settings": [
      { "Key": "orgId", "Label": "Organization ID", "Required": true, "Kind": "Text" },
      { "Key": "closedStatuses", "Label": "Closed statuses", "Kind": "Text",
        "Default": "closed, cancelled, rejected, resolved" }
    ]
  }
]
```

Секретные настройки шифруются тем же key ring, что и токен доступа; API не возвращает их значения, а
пустой секрет при сохранении означает «оставить сохранённый».

## Связь merge request'а с задачей

Метода провайдера, разбирающего ключ задачи из ветки, **нет**. То, как merge request называет свою
задачу, — соглашение конкретного соединения, а не диалект провайдера, поэтому это пара настроек
соединения, которую VCS-провайдер объявляет, а применяет **приём (ingestion)**:

```csharp
// В Providers.Abstractions.Vcs.TaskLinkSettings
public const string SourceKey  = "taskKeySource";   // Enum: branch | title | label
public const string PatternKey = "taskKeyPattern";  // Regex; ключ — группа 1 (или всё совпадение)
public const string DefaultPattern = @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])";
```

VCS-адаптер добавляет `TaskLinkSettings.Schema` в свой `SettingsSchema`, а приём строит правило через
`TaskLinkSettings.RuleFrom(settings)` и применяет его через
`Core.Parsing.TaskKeyExtractor.Extract(source, pattern, branch, title, labels)` — одна чистая,
покрытая тестами, единственная копия правила. Форма подключения показывает извлечённый ключ вживую.

## Добавление VCS-провайдера

Ориентир — существующий провайдер GitLab (`src/ReleaseOrchestrator.Providers.GitLab/`).

### 1. Реализуйте порты

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

internal sealed class MyVcsAdapter(HttpClient http) : IVcsProviderAdapter
{
    // Поля, которые форма предложит для этой VCS. Правило связи общее;
    // добавьте рядом всё вендор-специфичное.
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } = TaskLinkSettings.Schema;

    public async Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct)
    {
        // context = (ConnectionName, ApiUrl, AccessToken). Настроек провайдера здесь НЕТ:
        // правило связи и интервал опроса читает приём, а не вызовы API.
        var capabilities = new VcsCapabilities { SupportsMergeRequestLabels = true };
        return new MyVcsProvider(http, context, capabilities);
    }
}

internal sealed class MyVcsProvider(HttpClient http, VcsProviderContext context, VcsCapabilities caps)
    : IVcsProvider
{
    public VcsCapabilities Capabilities { get; } = caps;

    public Task<VcsMergeRequest?> GetMergeRequestAsync(
        string projectPath, string mergeRequestId, CancellationToken ct) => /* GET одного MR */;

    public Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(
        string projectPath, CancellationToken ct) => /* GET открытых MR */;
}
```

Порт намеренно маленький — это вызовы, которые планировщик делает сегодня. Нормализуйте строки
статусов вендора в `MergeRequestStatus` и заполняйте `VcsMergeRequest.Labels` /
`VcsMergeRequest.PipelineStatus`, чтобы гейт готовности мог их прочитать.

### 2. Регистрация — push и/или poll это разные типы

Push или poll — свойство *типа* провайдера, а не переключатель на соединении. VCS, которая и шлёт
пуши, и опрашивается, регистрирует два типа, каждый со своим `IngestionMode`:

```csharp
public const string WebhookProviderType = "myvcs-webhook";
public const string PollProviderType    = "myvcs-poll";

public static IServiceCollection AddMyVcsProvider(this IServiceCollection services)
{
    services.AddHttpClient<MyVcsAdapter>(c => c.Timeout = TimeSpan.FromSeconds(30));

    services.AddKeyedScoped<IVcsProviderAdapter>(WebhookProviderType,
        (sp, _) => new MyVcsWebhookAdapter(sp.GetRequiredService<MyVcsAdapter>()));
    services.AddKeyedScoped<IVcsProviderAdapter>(PollProviderType,
        (sp, _) => new MyVcsPollAdapter(sp.GetRequiredService<MyVcsAdapter>()));

    // Маркер-записи делают типы обнаружимыми и говорят поллеру, какие опрашивать.
    services.AddSingleton(new VcsProviderRegistration(WebhookProviderType, IngestionMode.Push));
    services.AddSingleton(new VcsProviderRegistration(PollProviderType, IngestionMode.Poll));
    return services;
}
```

`SettingsSchema` типа **poll** также объявляет интервал, а поллер читает его обратно:

```csharp
// схема poll-адаптера = TaskLinkSettings.Schema + поле интервала
new ProviderSettingSchema(VcsPollSettings.IntervalKey, "Poll interval (s)",
    Kind: ProviderSettingKind.Int, Default: "300",
    Min: VcsPollSettings.MinIntervalSeconds, Max: VcsPollSettings.MaxIntervalSeconds)
// поллер вызывает VcsPollSettings.IntervalFrom(connection.Settings)
```

### 3. Владейте вебхуком (только push-типы)

Push-провайдер владеет всем вендор-специфичным в доставке — формой payload, тем, какой заголовок несёт
секрет, как строка статуса маппится в статус — за портом `IWebhookParser`. Хост держит только маршрут,
разрешение секрета и отправку получившихся событий на шину.

```csharp
internal sealed class MyVcsWebhookParser : IWebhookParser
{
    public WebhookEndpointDescriptor Descriptor => /* имя эндпоинта + какой заголовок несёт секрет */;

    // Должен fail closed и работать за константное время — см. WebhookSignatures.
    public bool Authenticate(WebhookRequest request, string? secret) => /* проверка */;

    // Пусто — нормальный ответ (событие, которое провайдер не моделирует); бросайте
    // WebhookPayloadException только для некорректного тела.
    public IReadOnlyList<IngestionEvent> Parse(WebhookRequest request) => /* нормализация */;
}

// Регистрируется отдельно, чтобы ingress-хост получил парсер без HTTP read-адаптера:
services.AddKeyedSingleton<IWebhookParser, MyVcsWebhookParser>(WebhookProviderType);
services.AddSingleton(new WebhookParserRegistration(WebhookProviderType));
```

### 4. Стратегии выкатки

Как катится репозиторий — это `IDeployStrategy`, keyed и в паре с `DeployStrategyRegistration` (GitLab
поставляет `gitlab-merge` и `gitlab-pipeline`). Выбирается на пару `(репозиторий, окружение)` через
deploy target и объявляет свой `SettingsSchema`.

### 5. Подключение в корне композиции

```csharp
// src/ReleaseOrchestrator.Infrastructure/InfrastructureExtensions.cs (API-хост)
services.AddGitLabProvider();
services.AddMyVcsProvider();          // ← read-адаптеры + регистрации
services.AddMyVcsDeployStrategies();

// ingress-хост вместо этого подключает парсеры
services.AddGitLabWebhookParser();
services.AddMyVcsWebhookParser();
```

Добавьте ссылку на проект в нужный `.csproj` хоста, а версию пакета — в `Directory.Packages.props`
(никогда `Version` на `PackageReference`).

## Добавление провайдера трекера

Ориентир — `src/ReleaseOrchestrator.Providers.YandexTracker/`. Контекст трекер-адаптера **несёт** мешок
настроек (`TrackerProviderContext(ConnectionName, ApiUrl, AccessToken, ProviderSettings)`), потому что
они нужны самому адаптеру (заголовок с org id, какие статусы «завершены»).

```csharp
public sealed record MyTrackerOptions(string ProjectKey, IReadOnlySet<string> ClosedStatuses)
{
    public const string ProjectKeyKey     = "projectKey";
    public const string ClosedStatusesKey = "closedStatuses";

    public static MyTrackerOptions From(TrackerProviderContext context)
    {
        context.ProviderSettings.TryGetValue(ProjectKeyKey, out var key);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Tracker '{context.ConnectionName}' requires '{ProjectKeyKey}'.");

        context.ProviderSettings.TryGetValue(ClosedStatusesKey, out var closed);
        var closedStatuses = string.IsNullOrWhiteSpace(closed)
            ? new HashSet<string>(["done", "closed"], StringComparer.OrdinalIgnoreCase)
            : closed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MyTrackerOptions(key.Trim(), closedStatuses);
    }
}

internal sealed class MyTrackerAdapter(HttpClient http) : ITrackerProviderAdapter
{
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } =
    [
        new(MyTrackerOptions.ProjectKeyKey, "Project key", Required: true),
        new(MyTrackerOptions.ClosedStatusesKey, "Closed statuses",
            Description: "Статусы через запятую, означающие, что задача завершена.",
            Default: "done, closed"),
    ];

    public Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct)
        => Task.FromResult<ITrackerProvider>(new MyTrackerProvider(http, context, MyTrackerOptions.From(context)));
}
```

Провайдер реализует `ITrackerProvider` (прочитать задачу, решить, закрыт ли статус) и, **опционально**,
`ITrackerDependencySource` (связи задач) и `ITrackerMutator` (обратная запись). Опциональные
возможности вызывающий проверяет через `is`, а не по пустым спискам, которые не отличить от
«действительно ничего нет»:

```csharp
public bool IsClosedStatus(string? statusKey) =>
    statusKey is not null && _options.ClosedStatuses.Contains(statusKey.Trim());
```

Именно то, что набор «закрытых» — настройка, а не зашитый список, позволяет одному проекту звать
терминальное состояние `done`, а другому — `deployed`. Регистрация — keyed адаптер плюс маркер-запись:

```csharp
public const string ProviderType = "mytracker";
services.AddHttpClient<MyTrackerAdapter>(c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddKeyedScoped<ITrackerProviderAdapter, MyTrackerAdapter>(ProviderType);
services.AddSingleton(new TrackerProviderRegistration(ProviderType));
```

## Возможности (capabilities)

`VcsCapabilities` / `TrackerCapabilities` отвечают на вопрос «что умеет *это соединение*», строятся один
раз в `ConnectAsync` и далее только читаются. `VcsCapabilities.SupportsMergeRequestLabels` отличает
пустой набор меток, означающий «меток нет», от означающего «этот инстанс не умеет сообщать метки» —
гейт готовности не должен читать «не могу сказать» как «метку сняли». `ServerVersion` определяется один
раз и кэшируется; `null` — это «неизвестно», а не «старая», и сравнивается численно, а не как текст
(«16.11» новее «16.9»).

## Поток

1. Оператор создаёт соединение в UI и выбирает `ProviderType` (например, `gitlab-webhook`).
2. API проверяет его по зарегистрированным типам фабрики.
3. Соединение сохраняется с этим типом и мешком настроек.
4. Когда оркестратору нужен провайдер, фабрика делает `GetRequiredKeyedService` по типу, вызывает
   `ConnectAsync` и возвращает привязанный `IVcsProvider` / `ITrackerProvider`.
5. Доменный код вызывает методы провайдера без URL, токена и знаний о провайдере.
6. Неизвестный тип даёт `UnknownProviderException` с перечнем зарегистрированных.

**Порты не зависят от провайдера; адаптеры специфичны для диалекта.** Учётные данные и конфигурация
привязываются в момент connect, а не протаскиваются через каждый метод.
