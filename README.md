# Release Orchestrator

Enterprise release planning and orchestration platform for heterogeneous VCS and issue tracker environments.

Release Orchestrator помогает автоматически выстраивать последовательность merge request'ов, управлять ручными правками плана и архивировать завершённые задачи.

## Содержание

1. [Введение и цели проекта](#1-введение-и-цели-проекта)
2. [Архитектурные принципы и принятые решения](#2-архитектурные-принципы-и-принятые-решения)
3. [Компонентная архитектура](#3-компонентная-архитектура)
4. [Модель данных](#4-модель-данных)
5. [Автоматическое построение плана релиза](#5-автоматическое-построение-плана-релиза)
6. [Ручное редактирование плана и YAML](#6-ручное-редактирование-плана-и-yaml)
7. [Аутентификация и авторизация](#7-аутентификация-и-авторизация)
8. [Архивирование выполненных задач](#8-архивирование-выполненных-задач)
9. [Оценка нагрузки, масштабирование и требования к инфраструктуре](#9-оценка-нагрузки-масштабирование-и-требования-к-инфраструктуре)
10. [Технологический стек](#10-технологический-стек)
11. [Развёртывание и конфигурация](#11-развёртывание-и-конфигурация)
12. [План разработки](#12-план-разработки)

---

## 1. Введение и цели проекта

**Release Orchestrator** — это корпоративный инструмент планирования и контроля выкатки релизов в гетерогенной среде с множеством репозиториев, систем контроля версий и трекеров задач.  
Система автоматически выстраивает упорядоченный план деплоя merge request'ов (MR) на основе зависимостей задач из трекеров и конфигурации «стеков» — вручную дополняемых и корректируемых правил.

Ключевые показатели:

- До 10 000 задач и соответствующих MR в сутки, пиковый поток вебхуков 5–10 событий/сек.
- Одновременная работа до 100 пользователей (UI/API).
- Хранение горячих данных за последние 90 дней, архив исторических данных для отчётности.
- Полная контейнеризация, запуск на Linux, приоритетная СУБД — Microsoft SQL Server, с возможностью перехода на PostgreSQL без изменения бизнес-логики.

**Цель документа:** зафиксировать архитектурное решение и предоставить руководство для команды разработки.

---

## 2. Архитектурные принципы и принятые решения

### 2.1. Модульный монолит

Выделение отдельных сервисов под каждое подключение к VCS или трекеру отвергнуто.  
Задачи, MR и конфигурация стеков тесно связаны; построение плана релиза требует одновременного доступа к этим данным. Распределение привело бы к распределённым запросам и усложнению транзакционности.

**Решение:** единый Core Service с модулями `VcsModule`, `TaskModule`, `ReleasePlanning`, `Authorization`, взаимодействующими напрямую. Это даёт:

- Транзакционную целостность на уровне EF Core.
- Простоту разработки и отладки.
- Возможность выделить `ReleasePlanning` в отдельный сервис при экстремальном росте через очередь сообщений.

### 2.2. Выделенный Ingress-сервис для вебхуков

Приём вебхуков вынесен в `Ingress.Webhooks` (ASP.NET Core Minimal API). Причины:

- Быстрая отдача `200 OK` независимо от бизнес-обработки.
- Независимое масштабирование.
- Единая точка нормализации событий.

### 2.3. Очередь сообщений RabbitMQ

Используется для надёжной асинхронной доставки событий от Ingress к Core, а также для фоновых пересчётов. При пиковых нагрузках очередь сглаживает всплески, Core может обрабатывать события конкурентно (включая многопоточность в рамках одного процесса или несколько реплик).

### 2.4. Приоритет MSSQL при сохранении независимости от СУБД

Целевая СУБД — Microsoft SQL Server. Архитектура абстрагирует работу с данными через репозитории, миграции EF Core вынесены в отдельные сборки (`Migrations.MsSql` и `Migrations.PostgreSql`). Выбор провайдера осуществляется переменной окружения `DatabaseProvider`.

### 2.5. Архивирование как отдельный процесс

При 10 000 задачах/день таблица `TaskItem` будет расти на ~3.6 млн строк в год. Вводится выделенная архивная БД и фоновый сервис `ArchiveService`. Подробнее — в разделе 8.

---

## 3. Компонентная архитектура

```
                    +-------------------+
                    |  GitLab           |
                    |  (несколько экз.)|
                    +---------+---------+
                              |
                              | Webhook (HTTP POST)
                              v
                    +---------+---------+       +------------------+
                    | Ingress.Webhooks  |<------+ Яндекс.Трекер    |
                    | (Minimal API)     |       +------------------+
                    +---------+---------+
                              |
                              | Публикация нормализованных событий
                              v
                    +---------+---------+
                    | RabbitMQ          |
                    +---------+---------+
                              |
                              | Потребление
                              v
                    +---------+---------+
                    | Core Service      |
                    | (модульный        |
                    |  монолит)         |
                    |  - VcsModule      |
                    |  - TaskModule     |
                    |  - ReleasePlanning|
                    |  - Authorization  |
                    |  - BFF/API        |
                    +----+------+-------+
                         |      |
                         |      +-------- Вызов архивной БД
                         v
                    +----+------+
                    |  MSSQL    |
                    | (основная |
                    |  и архив) |
                    +-----------+
                             |
                             v
                    +-------------------+
                    | PWA (Blazor)      |
                    | через BFF         |
                    +-------------------+
```

### 3.1. Ingress.Webhooks

- **Технология:** ASP.NET Core Minimal API, .NET 10 (или актуальная LTS).
- **Масштабирование:** несколько реплик за обратным прокси (Nginx/Traefik).
- **Безопасность:** каждый вебхук валидируется по токену из конфигурации.
- **Нормализация:** из специфичных payload GitLab/Яндекс.Трекер формируются универсальные сообщения:
  ```csharp
  public record MrOpened(Guid MrId, string ExternalMrId, Guid RepositoryId, string SourceBranch, string TaskExternalId);
  public record TaskCreated(Guid TaskId, string ExternalId, string Title);
  ```
- **Обработка ошибок:** при недоступности RabbitMQ сообщения буферизуются в памяти ограниченное время; при превышении — вебхук возвращает 503. Рекомендуется настроить Dead Letter Queue (DLQ) на стороне RabbitMQ и мониторинг DLQ.

### 3.2. Core Service

Структура решения:

```
ReleaseOrchestrator.Core/              # Domain layer
  Entities/
  Interfaces/
ReleaseOrchestrator.Application/       # Application layer
  Vcs/                                 # VcsModule
  Tasks/                               # TaskModule
  ReleasePlanning/                     # Планировщик
  Authorization/                       # Управление доступом
ReleaseOrchestrator.Infrastructure/    # Infrastructure layer
  Persistence/                         # EF Core DbContext, миграции
  Vcs/                                 # GitLab API клиент
  Tracker/                             # Клиент Яндекс.Трекера
  Queue/                               # RabbitMQ consumer/producer (MassTransit)
  Archive/                             # ArchiveService
ReleaseOrchestrator.Web/               # Web layer
  Api/                                 # REST endpoints
  Bff/                                 # Backend for Frontend
```

Фоновые обработчики RabbitMQ построены на MassTransit (конкурентное потребление, повторные попытки с exponential backoff). Пересчёт плана выполняется асинхронно через выделенный потребитель, чтобы не блокировать приём других событий.

### 3.3. База данных

Основная БД (MSSQL) хранит оперативные сущности.  
Архивная БД (также MSSQL) размещается в отдельной базе `ReleaseOrchestratorArchive`. В первой версии допустимо использование той же инстанции SQL Server, но для production рекомендуется вынести архив на отдельный сервер (или файловую группу на отдельных дисках) с целью снижения влияния на оперативные бэкапы и производительность.

### 3.4. Веб-интерфейс (PWA)

Выбран Blazor WebAssembly (PWA) — единый стек разработки (C#), переиспользование моделей, встроенная офлайн-поддержка.  
Допустим React при наличии соответствующих компетенций; тогда BFF остаётся на .NET.

Основные функции UI:
- Просмотр плана релиза (последовательность стадий, графическое дерево).
- Drag-and-drop ручное редактирование.
- Экспорт/импорт YAML.
- Административные настройки (VCS, трекеры, стеки, права доступа).

---

## 4. Модель данных

### 4.1. Основные сущности (оперативная БД)

Все первичные ключи — `Guid`, генерируются автоматически.

**VcsConnection**
- `Id` (PK)
- `Name` (string, **уникальное**, используется в YAML)
- `VcsType` (enum: GitLab)
- `ApiUrl` (string)
- `EncryptedAccessToken` (byte[])

**TrackerConnection**
- `Id` (PK)
- `Name` (string)
- `TrackerType` (enum: YandexTracker)
- `ApiUrl` (string)
- `EncryptedAccessToken` (byte[])
- `OrgId` (string) — для Яндекс.Трекера

**Repository**
- `Id` (PK)
- `Name` (string)
- `ExternalId` (string) — полный путь в VCS, напр. `group/project`
- `ConnectionId` (FK -> VcsConnection)

**TrackerProject** *(задел на будущее, в первой версии не используется)*
- `Id` (PK)
- `ExternalId` (string)
- `Name` (string)
- `ConnectionId` (FK -> TrackerConnection)

**Stack**
- `Id` (PK)
- `Name` (string, уникальное)

**RepositoryStack** (M2M)
- `RepositoryId` (FK)
- `StackId` (FK)

**StackDependency**
- `Id` (PK)
- `FromStackId` (FK -> Stack, зависимый)
- `ToStackId` (FK -> Stack, требуемый)
- `Type` (enum: Hard=1, Soft=2)

**TaskItem** (задачи из трекера)
- `Id` (PK)
- `ExternalId` (string, индексирован) — например "TASK-123"
- `Title` (string)
- `Status` (string)
- `ClosedAt` (DateTime?, nullable)
- `TrackerConnectionId` (FK -> TrackerConnection, NOT NULL)
- `TrackerProjectId` (FK -> TrackerProject, NULL — задел)

**TaskDependency**
- `Id` (PK)
- `DependentTaskId` (FK -> TaskItem — задача, которая зависит)
- `DependsOnTaskId` (FK -> TaskItem — задача-предшественник)

**MergeRequest**
- `Id` (PK)
- `ExternalId` (string) — iid MR в VCS-проекте
- `SourceBranch` (string)
- `TargetBranch` (string)
- `RepositoryId` (FK -> Repository)
- `TaskId` (FK -> TaskItem, nullable) — связь с задачей (парсится из имени ветки)
- `Status` (enum `MergeRequestStatus`: Opened, Reviewed, ReadyForDeploy, Merged, Closed; хранится как int/string через EF value converter)
- `CreatedAt` (DateTime)
- `MergedAt` (DateTime?, nullable)

**ReleasePlan**
- `Id` (PK)
- `Name` (string)
- `Version` (string)
- `IsActive` (bool)
- `AutoGenerated` (bool)
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)
- `YamlHash` (string) — контрольная сумма импортированного YAML

**ReleaseStage**
- `Id` (PK)
- `PlanId` (FK -> ReleasePlan)
- `Sequence` (int) — порядковый номер стадии
- `Name` (string, nullable)
- `IsManualOverride` (bool)

**StageItem**
- `Id` (PK)
- `StageId` (FK -> ReleaseStage)
- `MergeRequestId` (FK -> MergeRequest)
- `ManualInclusion` (bool) — true, если добавлен/убран вручную

**Разрешения (claims)**
- `PermissionClaims` (`Id`, `Name` — уникальная строка, напр. `release.plan.approve`)
- `GroupPermissionMapping` (`Id`, `AdGroupSid`, `PermissionClaimId`)
- `UserPermissionOverride` (`Id`, `UserId`, `PermissionClaimId`)

**Пользователи:**
Стандартная `AspNetUsers`. Роли в стандартном понимании не используются.

### 4.2. Индексы и ограничения

- `IX_TaskItem_ExternalId`
- `IX_TaskItem_TrackerConnectionId_ExternalId`
- `IX_MergeRequest_RepositoryId_Status`
- `IX_MergeRequest_TaskId`
- `IX_StageItem_MergeRequestId`
- `IX_ReleasePlan_IsActive`
- `UQ_VcsConnection_Name` — уникальность `VcsConnection.Name`
- Каскадное удаление: при удалении `ReleasePlan` удаляются его `ReleaseStage` и `StageItem`. `MergeRequest` и `TaskItem` удаляются только через процесс архивирования.

---

## 5. Автоматическое построение плана релиза

Алгоритм реализован в `ReleasePlanner` модуля `ReleasePlanning`. Запускается асинхронно по событиям (новый MR, изменение статуса на `ReadyForDeploy`, изменение зависимостей задач/стеков).

**Вход:**
- Множество MR со статусом `ReadyForDeploy` (или `Opened`, помеченный специальным лейблом).
- Задачи, привязанные к этим MR.
- Зависимости задач (`TaskDependency`).
- Зависимости стеков (`StackDependency`).
- Маппинг репозиторий → стеки.

**Этапы:**

1. **Построение графа:** вершина — один `MergeRequest`.
   - Ребро MR2 → MR1, если задача MR2 зависит от задачи MR1.
   - Ребро MR2 → MR1, если стек репозитория MR2 зависит от стека репозитория MR1 (Hard).
2. **Учёт типов зависимостей:**
   - Hard — жёсткое требование, не может быть нарушено.
   - Soft — рекомендательное, нарушение выводит предупреждение.
3. **Разрешение циклов:** последовательно удаляются наименее критичные рёбра (Soft, затем рёбра из зависимостей задач), пока граф не станет ацикличным. Информация о конфликтах сохраняется для отображения в UI.
4. **Топологическая сортировка:** алгоритм Кана группирует вершины по уровням, каждый уровень становится `ReleaseStage`.
5. **Сохранение:** создаётся или обновляется автоматический `ReleasePlan` (`AutoGenerated = true`).

**Производительность:** для графа до 10 000 вершин и ~20 000 рёбер время расчёта не превышает 500 мс. Пересчёт дебаунсится (не чаще одного раза в 10–20 секунд).

---

## 6. Ручное редактирование плана и YAML

### 6.1. Ручные корректировки через UI

Пользователи с правом `release.plan.approve` могут:
- Менять порядок стадий.
- Перемещать MR между стадиями.
- Добавлять/убирать MR из плана.
- Утверждать план.

Все ручные изменения проставляют `IsManualOverride = true` и `ManualInclusion`. При новом автоматическом пересчёте отображается diff; пользователь выбирает, принять автоматическую версию или сохранить ручную.

### 6.2. YAML-формат

Экспорт/импорт плана в YAML для хранения в Git и массовых правок.

Пример:
```yaml
release_plan:
  name: "Release 24.3"
  version: "1.0.0"
  created: "2026-05-10T10:00:00Z"
  stages:
    - seq: 1
      name: "Pre-deploy: SQL.vv03"
      items:
        - mr_id: "gitlab-sql:automacon/mssql-databases/vv03!123"
          task: "TASK-456"
    - seq: 2
      name: "Main deploy"
      items:
        - mr_id: "gitlab-sql:automacon/mssql-databases/loyalty!78"
          task: "TASK-456"
        - mr_id: "gitlab-sql:automacon/oms/backend!45"
          task: "TASK-789"
  manual_overrides:
    - type: reorder
      reason: "Инфраструктурная задержка"
```

**Формат `mr_id`:** `<connectionName>:<projectFullPath>!<iid>`.  
`connectionName` соответствует `VcsConnection.Name` (уникальное).

**Валидация при импорте:**
- Проверка существования `connectionName` и доступности MR через API VCS.
- Hard-зависимости не должны нарушаться (иначе импорт отклоняется; возможен режим `force` с предупреждениями).

---

## 7. Аутентификация и авторизация

### 7.1. Поток аутентификации

1. Пользователь без сессии перенаправляется на ADFS/Azure AD (OpenID Connect).
2. После логина приложение получает id_token/access_token, содержащий группы AD.
3. Custom `IClaimsTransformation` (или событие `OnTokenValidated`) обогащает `ClaimsPrincipal` разрешениями из кэшированного маппинга.
4. Middleware `UseAuthorization` применяет политики, построенные на permission claims.

### 7.2. Хранение маппинга

```sql
PermissionClaims (Id, Name UNIQUE)         -- 'release.plan.approve', 'config.edit' и т.д.
GroupPermissionMapping (Id, AdGroupSid, PermissionClaimId)
UserPermissionOverride (Id, UserId, PermissionClaimId)
```

- Группам AD и отдельным пользователям назначаются разрешения (claims).
- Маппинг кэшируется в распределённом кэше (Redis) с TTL 5 минут.
- При изменении маппингов через UI публикуется событие инвалидации кэша (`PermissionMappingChanged`).

### 7.3. Управление через UI

Административная панель позволяет просматривать и редактировать разрешения групп и пользователей. Все изменения аудируются.

---

## 8. Архивирование выполненных задач

### 8.1. Критерии архивации

- Статус `Closed`, `Merged` или `Cancelled`.
- `ClosedAt` / `MergedAt` старше `ArchiveAfterDays` (по умолчанию 90 дней).
- Сущность не входит ни в один активный `ReleasePlan`.
- MR: связанная задача уже архивирована или отсутствует.

### 8.2. Архивная база данных

Отдельная БД `ReleaseOrchestratorArchive` (на том же или выделенном сервере). Структура денормализована для быстрых исторических запросов:

- `ArchivedTask` — `Id`, `ExternalId`, `Title`, `Status`, `ClosedAt`, `DependenciesJson`.
- `ArchivedMergeRequest` — `Id`, `ExternalId`, `RepositoryName`, `SourceBranch`, `TargetBranch`, `Status`, `TaskExternalId`, `ClosedAt`.
- `ArchivedReleasePlan` — полные копии исторических планов.

### 8.3. Процесс архивации

- Фоновый сервис `ArchiveHostedService` запускается по cron (ночью).
- Выбирает записи пакетами (TaskItem — по 1000, MergeRequest — по 500).
- В одной транзакции на пакет:
  - Вставка в архивную БД (`SqlBulkCopy` для больших объёмов).
  - Удаление из оперативной БД.
- Между пакетами пауза 1 секунда для снижения блокировок.
- При ошибке пакет повторяется до 3 раз, затем пропускается с записью в лог.
- Старые архивы (старше 2 лет) удаляются ежемесячным заданием.

### 8.4. Влияние на производительность

Оперативная БД хранит только ~90 дней горячих данных (≈900 000 задач и MR). Запросы к архиву выполняются редко и не пересекаются с оперативной нагрузкой.

---

## 9. Оценка нагрузки, масштабирование и требования к инфраструктуре

### 9.1. Расчёт нагрузки

- **Вебхуки:** до 20 000 событий/день (пик 5–10/с). Ingress выдерживает >50 запросов/с.
- **API Core:** 50–100 RPS, большинство ответов кэшируется.
- **Пересчёт плана:** 5–10 раз в минуту, <500 мс каждый.
- **БД:** 100–200 транзакций/с на пике.

### 9.2. Требования к оборудованию (на 3 года)

- **Сервер:** 8 vCPU (x86-64, 2.5+ GHz), 32 ГБ RAM, 512 ГБ SSD NVMe.
- Распределение контейнеров:

| Компонент                | vCPU        | RAM        |
|--------------------------|-------------|------------|
| Ingress (3 реплики)      | 0.5/реплика | 512 МБ     |
| Core Service (2-3 реплики)| 1.5/реплика| 2 ГБ       |
| MSSQL                    | 6           | 16 ГБ      |
| RabbitMQ                 | 0.5         | 1 ГБ       |
| Nginx/Traefik            | 0.2         | 128 МБ     |
| **Итого**                | ~8          | ~23 ГБ     |

При использовании внешнего MSSQL хост контейнеров может быть 4 vCPU / 8 ГБ.

### 9.3. Масштабирование

- Ingress — горизонтальное масштабирование за балансировщиком.
- Core — конкурентное потребление RabbitMQ; реплики настраиваются через `Concurrency`.
- RabbitMQ — возможен кластер с mirrored queues.
- MSSQL — шардирование или облачный сервис при дальнейшем росте.

---

## 10. Технологический стек

| Слой              | Технология                                |
|-------------------|-------------------------------------------|
| Бэкенд            | .NET 10 (или актуальная LTS), ASP.NET Core|
| Язык              | C# (актуальная версия)                    |
| ORM               | Entity Framework Core 10                  |
| Очередь           | RabbitMQ + MassTransit                    |
| База данных       | Microsoft SQL Server 2022 (основная и архивная) |
| Кэш               | Redis 7+                                  |
| Контейнеризация   | Docker, Docker Compose, Kubernetes        |
| Веб-сервер        | Kestrel, Nginx/Traefik                    |
| Frontend          | Blazor WebAssembly PWA                    |
| Аутентификация    | Microsoft.Identity.Web (OIDC)             |
| YAML              | YamlDotNet                                |
| Логирование       | Serilog + Seq (или ELK)                   |
| Метрики           | Prometheus + Grafana                      |
| CI/CD             | GitLab CI / GitHub Actions                |

---

## 11. Развёртывание и конфигурация

### 11.1. Переменные окружения

**Ingress:**
- `Queue__Host`, `Queue__Username`, `Queue__Password`

**Core Service:**
- `DatabaseProvider` = `SqlServer`
- `ConnectionStrings__Default` (ReleaseOrchestrator)
- `ConnectionStrings__Archive` (ReleaseOrchestratorArchive)
- `Queue__Host`
- `AD__Authority`, `AD__ClientId`, `AD__ClientSecret`
- `Archiving__Enabled`, `Archiving__ScheduleCron`, `Archiving__ArchiveAfterDays`

### 11.2. docker-compose (фрагмент)

```yaml
services:
  ingress:
    image: registry.example.com/release-orchestrator/ingress:latest
    ports: ["8080:8080"]
    environment:
      Queue__Host: rabbitmq
    restart: unless-stopped

  core:
    image: registry.example.com/release-orchestrator/core:latest
    environment:
      DatabaseProvider: SqlServer
      ConnectionStrings__Default: Server=mssql;Database=ReleaseOrchestrator;User Id=sa;Password=...
      ConnectionStrings__Archive: Server=mssql;Database=ReleaseOrchestratorArchive;User Id=sa;Password=...
      Queue__Host: rabbitmq
      AD__Authority: https://adfs.company.com/adfs
    depends_on: [mssql, rabbitmq]
    restart: unless-stopped

  mssql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: Y
      MSSQL_SA_PASSWORD: ...
      MSSQL_MEMORY_LIMIT_MB: 14336
    volumes:
      - mssqldata:/var/opt/mssql
      - mssqllog:/var/opt/mssql/log
      - mssqlarchive:/var/opt/mssql/archive
    deploy:
      resources:
        limits: { cpus: '6', memory: 16G }
    restart: unless-stopped

  rabbitmq:
    image: rabbitmq:3-management
    volumes: [rabbitmqdata:/var/lib/rabbitmq]

volumes:
  mssqldata:
  mssqllog:
  mssqlarchive:
  rabbitmqdata:
```

### 11.3. Применение миграций

Init-контейнер или CI/CD вызывает `dotnet ef database update --assembly Migrations.MsSql`. Для PostgreSQL аналогично с `Migrations.PostgreSql`.

---

## 12. План разработки

**Фаза 1: Фундамент (недели 1–3)**
- Структура проектов, CI/CD, модели и миграции (MSSQL).
- Ingress.Webhooks: приём вебхуков GitLab и Яндекс.Трекера, публикация в RabbitMQ.

**Фаза 2: Основная функциональность (недели 4–7)**
- VcsModule, TaskModule: CRUD конфигураций, обработка событий.
- Парсинг связей «ветка-задача».
- Прототип ReleasePlanning.

**Фаза 3: Планирование и UI (недели 8–11)**
- Фоновые пересчёты, сохранение планов.
- BFF и API, PWA (Blazor) с отображением плана и ручным редактированием.
- YAML экспорт/импорт.

**Фаза 4: Аутентификация и безопасность (неделя 12)**
- Интеграция с AD, permission claims, кэширование маппингов.

**Фаза 5: Архивирование и production (недели 13–15)**
- ArchiveService, архивная БД.
- Мониторинг, логирование, нагрузочное тестирование.

**Фаза 6: Стабилизация (недели 16–20)**
- Пилотная эксплуатация, оптимизация, обработка edge cases.