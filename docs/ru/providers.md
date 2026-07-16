# Архитектура поставщиков данных

## Обзор

**Поставщик** (provider) — это адаптер, нормализующий детали диалекта конкретного провайдера (API endpoints, аутентификация, отображение состояний) в единый интерфейс. ReleaseOrchestrator использует **регистрацию зависимостей с ключами на этапе компиляции**, а не динамическое обнаружение или плагины.

### Почему регистрация на этапе компиляции?

- **Быстрая ошибка:** Неизвестные типы поставщиков обнаруживаются при инициализации composition root, а не во время клика пользователя
- **Без отражения:** Нет сканирования с атрибутами, нет динамической загрузки
- **Явные зависимости:** Сразу видно, какие поставщики доступны
- **Предсказуемая версионизация:** Версии поставщиков зафиксированы в `.csproj`, не загружаются с nuget.org в runtime

### Как это работает

1. Создаёте новый проект поставщика (например, `ReleaseOrchestrator.Providers.GitHub`)
2. Реализуете интерфейс порта (`IVcsProviderAdapter` или `ITrackerProviderAdapter`)
3. Объявляете параметры поставщика через `SettingsSchema` (см. [Параметры поставщика](#параметры-поставщика) ниже)
4. Вызываете `.AddYourProvider()` в `InfrastructureExtensions` — одна строка
5. Фабрика находит поставщика через keyed DI и резолвит его в runtime
6. Если ключ не совпадает, `UnknownProviderException` выводит список доступных

## Параметры поставщика

Поставщики могут объявлять параметры, специфичные для подключения (например, Yandex.Tracker требует org ID, GitLab — нет). Параметры открываются и валидируются через API поставщика:

**`GET /api/providers/vcs`** — Список зарегистрированных VCS-поставщиков и их схемы параметров:
```json
[
  {
    "ProviderType": "github",
    "Settings": []
  },
  {
    "ProviderType": "gitlab",
    "Settings": []
  }
]
```

**`GET /api/providers/trackers`** — Список зарегистрированных tracker-поставщиков и их схемы параметров:
```json
[
  {
    "ProviderType": "yandex-tracker",
    "Settings": [
      {
        "Key": "orgId",
        "Label": "Organization ID",
        "Description": "Org UUID из URL трекера",
        "Required": true,
        "Secret": false
      }
    ]
  }
]
```

### Объявление параметров

Каждый поставщик объявляет свои параметры, реализуя `IVcsProviderFactory.GetSettingsSchema(providerType)` или `ITrackerProviderFactory.GetSettingsSchema(providerType)`. Схема возвращает список записей `ProviderSettingSchema`:

```csharp
public record ProviderSettingSchema(
    string Key,                           // e.g. "orgId"
    string Label,                         // "Organization ID" (для пользователей)
    string? Description = null,           // "Org UUID из URL трекера..."
    bool Required = false,                // True если подключение не может быть сохранено без него
    bool Secret = false                   // True для write-only полей (не возвращаются API)
);
```

**Зачем?** Раньше параметры каждого поставщика были hard-coded в форме UI и API контракте. Добавление нового поставщика требовало правок в трёх местах. Теперь UI запрашивает API, чтобы узнать, какие параметры нужны каждому поставщику, и динамически строит форму.

### Хранение параметров

Параметры поставщика хранятся в виде непрозрачного JSON в сущности подключения:
- `VcsConnection.ProviderSettingsJson` (для VCS-поставщиков)
- `TrackerConnection.ProviderSettingsJson` (для tracker-поставщиков)

Адаптер поставщика владеет схемой и логикой разбора:

```csharp
public class YandexTrackerOptions
{
    public required string OrgId { get; init; }

    public static YandexTrackerOptions From(TrackerProviderContext context)
    {
        var settings = context.ProviderSettings;  // Dict<string, string?>
        if (!settings.TryGetValue("orgId", out var orgId) 
            || string.IsNullOrWhiteSpace(orgId))
        {
            throw new InvalidOperationException(
                $"Трекер '{context.ConnectionName}' имеет отсутствующий параметр 'orgId'.");
        }

        return new YandexTrackerOptions { OrgId = orgId.Trim() };
    }
}
```

API-слой никогда не разбирает параметры поставщика напрямую — он передаёт dict адаптеру, который интерпретирует его.

## Добавление VCS-поставщика (например, GitHub)

### Шаг 1: Создание проекта

```bash
mkdir src/ReleaseOrchestrator.Providers.GitHub
cat > src/ReleaseOrchestrator.Providers.GitHub/ReleaseOrchestrator.Providers.GitHub.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Providers.Abstractions\ReleaseOrchestrator.Providers.Abstractions.csproj" />
  </ItemGroup>
</Project>
EOF
```

Добавьте проект в решение:

```bash
dotnet sln src/ReleaseOrchestrator.sln add src/ReleaseOrchestrator.Providers.GitHub/ReleaseOrchestrator.Providers.GitHub.csproj
```

### Шаг 2: Реализация `IVcsProviderAdapter`

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitHub;

/// <summary>
/// Подключается к репозиторию GitHub и создаёт экземпляр <see cref="IVcsProvider"/>.
/// </summary>
internal class GitHubProviderAdapter : IVcsProviderAdapter
{
    private readonly HttpClient _client;

    public GitHubProviderAdapter(HttpClient client)
    {
        _client = client;
    }

    public async Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct = default)
    {
        // Опционально: определить версию сервера и собрать возможности
        var capabilities = new VcsCapabilities(
            ServerVersion: null,
            SupportsMergeRequestLabels: true // GitHub всегда поддерживает labels
        );

        return new GitHubProvider(_client, context, capabilities);
    }
}

internal class GitHubProvider : IVcsProvider
{
    private readonly HttpClient _client;
    private readonly VcsProviderContext _context;

    public GitHubProvider(HttpClient client, VcsProviderContext context, VcsCapabilities capabilities)
    {
        _client = client;
        _context = context;
        Capabilities = capabilities;
    }

    public VcsCapabilities Capabilities { get; }

    public async Task<VcsMergeRequest?> GetMergeRequestAsync(
        string repositoryExternalId,
        string mergeRequestExternalId,
        CancellationToken ct = default)
    {
        // GitHub API: GET /repos/{owner}/{repo}/pulls/{number}
        var url = $"{_context.ApiUrl}/repos/{repositoryExternalId}/pulls/{mergeRequestExternalId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"token {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParsePullRequest(json);
    }

    public async Task<IEnumerable<VcsMergeRequest>> GetOpenMergeRequestsAsync(
        string repositoryExternalId,
        CancellationToken ct = default)
    {
        var url = $"{_context.ApiUrl}/repos/{repositoryExternalId}/pulls?state=open&per_page=100";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"token {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParsePullRequests(json);
    }

    public string? ParseTaskKeyFromBranch(string branchName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            branchName,
            @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static VcsMergeRequest? ParsePullRequest(string json) => null;
    private static IEnumerable<VcsMergeRequest> ParsePullRequests(string json) => [];
}
```

### Шаг 3: Расширение для регистрации

```csharp
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitHub;

public static class GitHubProviderExtensions
{
    public const string ProviderType = "github";

    public static IServiceCollection AddGitHubProvider(this IServiceCollection services)
    {
        services
            .AddHttpClient<GitHubProviderAdapter>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddKeyedScoped<IVcsProviderAdapter, GitHubProviderAdapter>(ProviderType);
        services.AddSingleton(new VcsProviderRegistration(ProviderType));

        return services;
    }
}
```

### Шаг 4: Регистрация в корне композиции

Откройте `src/ReleaseOrchestrator.Infrastructure/InfrastructureExtensions.cs`:

```csharp
public static IServiceCollection AddInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // ... существующий код ...

    services.AddGitLabProvider();
    services.AddGitHubProvider();         // ← Добавьте эту строку
    services.AddYandexTrackerProvider();

    return services;
}
```

Добавьте ссылку на проект в `Infrastructure.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\ReleaseOrchestrator.Providers.GitLab\ReleaseOrchestrator.Providers.GitLab.csproj" />
  <ProjectReference Include="..\ReleaseOrchestrator.Providers.GitHub\ReleaseOrchestrator.Providers.GitHub.csproj" />
</ItemGroup>
```

## Добавление поставщика трекера (например, Jira)

### Шаг 1: Создание проекта

```bash
mkdir src/ReleaseOrchestrator.Providers.Jira
dotnet sln src/ReleaseOrchestrator.sln add src/ReleaseOrchestrator.Providers.Jira/ReleaseOrchestrator.Providers.Jira.csproj
```

### Шаг 2: Реализация `ITrackerProviderAdapter`

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.Jira;

internal class JiraOptions
{
    public required string ProjectKey { get; init; }

    public static JiraOptions From(TrackerProviderContext context)
    {
        var settings = context.ProviderSettings;
        if (!settings.TryGetValue("projectKey", out var projectKey)
            || string.IsNullOrWhiteSpace(projectKey))
        {
            throw new InvalidOperationException(
                $"Трекер '{context.ConnectionName}' требует параметра 'projectKey'.");
        }

        return new JiraOptions { ProjectKey = projectKey.Trim() };
    }
}

internal class JiraProviderAdapter : ITrackerProviderAdapter
{
    private readonly HttpClient _client;

    public JiraProviderAdapter(HttpClient client) => _client = client;

    public async Task<ITrackerProvider> ConnectAsync(
        TrackerProviderContext context,
        CancellationToken ct = default)
    {
        var options = JiraOptions.From(context);
        var capabilities = new TrackerCapabilities(ServerVersion: null);
        return new JiraProvider(_client, context, options, capabilities);
    }
}

internal class JiraProvider : ITrackerProvider, ITrackerDependencySource
{
    private readonly HttpClient _client;
    private readonly TrackerProviderContext _context;
    private readonly JiraOptions _options;

    public JiraProvider(HttpClient client, TrackerProviderContext context, JiraOptions options, TrackerCapabilities capabilities)
    {
        _client = client;
        _context = context;
        _options = options;
        Capabilities = capabilities;
    }

    public TrackerCapabilities Capabilities { get; }

    public async Task<TrackerIssue?> GetIssueAsync(string externalTaskId, CancellationToken ct = default)
    {
        var url = $"{_context.ApiUrl}/rest/api/3/issue/{externalTaskId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        return !response.IsSuccessStatusCode ? null : ParseIssue(await response.Content.ReadAsStringAsync(ct));
    }

    public bool IsClosedStatus(string? statusKey) =>
        statusKey?.Equals("Done", StringComparison.OrdinalIgnoreCase) ?? false;

    public async Task<IEnumerable<TrackerIssueDependency>> GetIssueDependenciesAsync(
        string externalTaskId,
        CancellationToken ct = default)
    {
        var url = $"{_context.ApiUrl}/rest/api/3/issue/{externalTaskId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        return !response.IsSuccessStatusCode ? [] : ParseIssueDependencies(await response.Content.ReadAsStringAsync(ct));
    }

    private static TrackerIssue? ParseIssue(string json) => null;
    private static IEnumerable<TrackerIssueDependency> ParseIssueDependencies(string json) => [];
}
```

### Шаг 3: Расширение для регистрации

```csharp
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.Jira;

public static class JiraProviderExtensions
{
    public const string ProviderType = "jira";

    public static IServiceCollection AddJiraProvider(this IServiceCollection services)
    {
        services
            .AddHttpClient<JiraProviderAdapter>()
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddKeyedScoped<ITrackerProviderAdapter, JiraProviderAdapter>(ProviderType);
        services.AddSingleton(new TrackerProviderRegistration(ProviderType));

        return services;
    }
}
```

## Механизм возможностей

Поставщики выражают способности тремя способами:

### 1. Опциональные интерфейсы

Не все трекеры умеют извлекать зависимости. Вместо метода, возвращающего пустой список:

```csharp
public interface ITrackerDependencySource
{
    Task<IEnumerable<TrackerIssueDependency>> GetIssueDependenciesAsync(
        string externalTaskId,
        CancellationToken ct = default);
}
```

Потребитель проверяет перед вызовом:

```csharp
if (provider is ITrackerDependencySource source)
{
    var deps = await source.GetIssueDependenciesAsync(issueKey, ct);
}
```

### 2. Возможности по соединению

Связаны с конкретным соединением:

```csharp
public record VcsCapabilities(
    string? ServerVersion,
    bool SupportsMergeRequestLabels
);
```

Строятся в `ConnectAsync`, только для чтения после этого.

### 3. Кэширование определения версии

Для дорогих API-вызовов кэшируйте по URL:

```csharp
internal class GitLabVersionDetector
{
    private readonly Dictionary<Uri, GitLabServerVersion?> _cache = new();

    public async Task<GitLabServerVersion?> DetectAsync(Uri apiUrl, string token, CancellationToken ct)
    {
        if (_cache.TryGetValue(apiUrl, out var cached))
            return cached;

        try
        {
            var response = await _client.GetAsync($"{apiUrl}/api/v4/version", ct);
            var version = ParseVersion(response);
            _cache[apiUrl] = version;
            return version;
        }
        catch
        {
            _cache[apiUrl] = null; // Консервативно: null = неизвестно
            return null;
        }
    }
}
```

## Сравнение версий

Используйте числовое сравнение, не текстовое:

```csharp
public record GitLabServerVersion(int Major, int Minor, int Patch)
{
    public bool IsAtLeast(int major, int minor) =>
        Major > major || (Major == major && Minor >= minor);
}
```

Текстовое сравнение "16.11" < "16.9" даёт неправильный результат.

## Паттерны из существующих поставщиков

### GitLab

**Отображение состояний:**

```csharp
public static MergeRequestStatus? FromState(string? state) => state?.ToLowerInvariant() switch
{
    "opened" => MergeRequestStatus.Opened,
    "merged" => MergeRequestStatus.Merged,
    "closed" => MergeRequestStatus.Closed,
    _ => null,
};
```

Неизвестные состояния возвращают `null` — домен их обрабатывает.

**Парсинг веток:** Скомпилированный regex с таймаутом. Паттерн: `PROJ-123`, не `proj123`.

**Переиспользование в вебхуках:** И endpoint, и сервис вызывают `GitLabMergeRequestState.FromState`, гарантируя согласованное интерпретирование.

### Yandex.Tracker

**Правила закрытого статуса:**

```csharp
private static readonly HashSet<string> ClosedStatuses =
    new(new[] { "closed", "cancelled", "rejected", "resolved" }, StringComparer.OrdinalIgnoreCase);

public static bool IsClosed(string? statusKey) =>
    !string.IsNullOrWhiteSpace(statusKey) && ClosedStatuses.Contains(statusKey);
```

Единый источник истины. До этого две копии списка расходились — одна включала "resolved", другая нет, оставляя задачи в тупике.

**Типизированные опции:**

```csharp
public static YandexTrackerOptions From(TrackerProviderContext context)
{
    var settings = context.ProviderSettings;
    if (!settings.TryGetValue("orgId", out var orgId) || string.IsNullOrWhiteSpace(orgId))
        throw new InvalidOperationException($"Трекер '{context.ConnectionName}' требует orgId");
    return new YandexTrackerOptions { OrgId = orgId.Trim() };
}
```

Конфигурация, специфичная для поставщика, живёт в JSON базы; адаптер владеет схемой.

## Тестирование

```csharp
public class GitLabProviderTests
{
    [Theory]
    [InlineData("PROJ-123", "feature/PROJ-123-add-foo", true)]
    [InlineData("PROJ-123", "PROJ-123-add-foo", true)]
    [InlineData("PROJ-123", "myPROJ-123-branch", false)]
    public void ParsesTaskKeyFromBranch(string expected, string branch, bool shouldMatch)
    {
        var provider = new GitLabProvider(new HttpClient(), 
            new VcsProviderContext("...", new Uri("..."), "token", "ready"),
            new VcsCapabilities());
        
        var result = provider.ParseTaskKeyFromBranch(branch);
        if (shouldMatch)
            Assert.Equal(expected, result);
        else
            Assert.Null(result);
    }
}
```

## Итоговый поток

1. **Пользователь создаёт соединение** в UI, устанавливает `ProviderType = "github"`
2. **API проверяет** против `IVcsProviderFactory.AvailableProviders`
3. **Сущность сохраняется** в БД с `ProviderType = "github"`
4. **Синхронизация запускается:** вызывает `factory.CreateAsync(connection)` → keyed DI резолвит адаптер → `ConnectAsync` → возвращает `IVcsProvider`
5. **Домен вызывает** методы поставщика без знания о `apiUrl`, `token`, типе провайдера
6. **Неизвестный тип:** `UnknownProviderException` называет зарегистрированные поставщики

Ключевое: **Порты не знают о провайдерах; адаптеры знают диалект.** Учётные данные привязаны в фабрике, не передаются в сигнатурах методов.
