# Release Orchestrator — Конфигурация

> [English version ->](../en/configuration.md) - [← Вернуться к документации](../README.md)

---

## Обзор

Вся конфигурация читается из переменных окружения при старте. Приложение выполняет **fail-fast валидацию**: если требуемая переменная отсутствует, приложение откажется запускаться до инициализации любой зависимости (RabbitMQ, Redis, БД).

Этот документ перечисляет каждую переменную окружения, статус обязательности/опциональности и что произойдёт при отсутствии.

---

## Обязательная конфигурация

Эти переменные должны быть установлены, иначе приложение не запустится.

### Подключение к базе данных

**`ConnectionStrings__Default`** (ОБЯЗАТЕЛЬНО)
- **Что:** Connection string оперативной БД
- **Формат:** SQL Server connection string
- **Пример:** `Server=localhost;Database=ReleaseOrchestrator;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **При отсутствии:** `InvalidOperationException` при старте — "ConnectionStrings:Default is required"
- **Retry при транзиентных ошибках:** Включен (timeout SQL Server, deadlock → автоматический retry, exponential backoff)

**`ConnectionStrings__Archive`** (ОБЯЗАТЕЛЬНО)
- **Что:** Connection string архивной БД (исторические данные >90 дней)
- **Формат:** SQL Server connection string
- **Пример:** `Server=localhost;Database=ReleaseOrchestratorArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;`
- **При отсутствии:** `InvalidOperationException` при старте
- **Примечание:** Может быть той же инстанцией SQL Server (но другая БД) или полностью отдельным сервером

### Очередь сообщений (RabbitMQ)

Брокер задаётся либо полной строкой подключения, **либо** по частям. Части — это то, что ставит
`docker-compose.yml`; строка подключения нужна деплою, которому проще передать один URI (TLS,
кластер, не-дефолтный vhost).

**`Queue__ConnectionString`** (опционально — форма строки подключения)
- **Что:** Полный AMQP URI, напр. `amqps://user:pass@rabbit.company.com:5671/vhost`
- **Поведение:** Если задана, используется дословно, а части ниже игнорируются
- **Когда:** TLS (`amqps`), список хостов кластера или vhost, который не хочется собирать вручную

**`Queue__Username`** (обязательно, если не задан `Queue__ConnectionString`)
- **Что:** Имя пользователя для аутентификации RabbitMQ
- **Пример:** `guest`
- **При отсутствии:** `InvalidOperationException` при старте, до подключения к шине
- **Примечание безопасности:** Никогда не используйте `guest` в production

**`Queue__Password`** (обязательно, если не задан `Queue__ConnectionString`)
- **Что:** Пароль для аутентификации RabbitMQ
- **При отсутствии:** `InvalidOperationException` при старте
- **Примечание безопасности:** Используйте стойкий пароль в production

**`Queue__Host`**
- **Что:** Хостнейм или IP брокера RabbitMQ
- **Пример:** `rabbitmq.company.com` или `localhost`
- **По умолчанию:** `localhost`

**`Queue__Port`**
- **Что:** AMQP-порт RabbitMQ
- **Пример:** `5672` (стандартный)
- **По умолчанию:** 5672

**`Queue__VirtualHost`**
- **Что:** Виртуальный хост RabbitMQ
- **Пример:** `/` (дефолтный vhost) или `release`
- **По умолчанию:** `/`

### Кэш (Redis)

**`Redis__ConnectionString`** (ОБЯЗАТЕЛЬНО)
- **Что:** Redis connection string для кэширования прав доступа
- **Формат:** `{host}:{port}` или `{host}:{port},password={password}`
- **Пример:** `redis.company.com:6379` или `localhost:6379,password=MySecurePassword`
- **При отсутствии:** `InvalidOperationException` при старте
- **Важно:** Права кэшируются здесь; без Redis каждый API-вызов опрашивает БД. **Не выставляйте Redis в неtrusted networks** — кэшированные права не переверяются на каждый запрос.

---

## Опциональная конфигурация со значениями по умолчанию

### Окружение приложения

**`ASPNETCORE_ENVIRONMENT`**
- **Что:** Управляет логированием, деталями ошибок, поведением middleware
- **Допустимые значения:** `Development`, `Production`, `Staging`
- **По умолчанию:** `Production`
- **Production-поведение:** Нет stack traces в ответах об ошибках, HSTS включён
- **Development-поведение:** Полные детали ошибок, middleware логирование

### Хостинг

**`ASPNETCORE_URLS`**
- **Что:** HTTP listener addresses
- **Пример:** `https://localhost:5173;http://localhost:5172`
- **По умолчанию:** `http://localhost:5000`

**`ASPNETCORE_FORWARDEDHEADERS_ENABLED`**
- **Что:** Доверять ли заголовкам `X-Forwarded-For`, `X-Forwarded-Proto` (устанавливает reverse proxy)
- **Допустимые значения:** `true` или `false`
- **По умолчанию:** `false`
- **Важно:** Должно быть `true` если запущено за Nginx/Traefik и используется HTTPS redirect или HSTS

**`ASPNETCORE_FORWARDEDHEADERS_KNOWNNETWORKS`**
- **Что:** CIDR-сети, с которых доверять forwarded headers
- **Пример:** `172.16.0.0/12;10.0.0.0/8`
- **По умолчанию:** localhost, link-local ranges
- **Безопасность:** Если пусто, доверяется только localhost

### Archive Service

**`Archiving__Enabled`** (раздел: `Archiving`)
- **Что:** Запускать ли архивный сервис
- **Допустимые значения:** `true`, `false`
- **По умолчанию:** `true` (запускается в каждом поде)
- **Если false:** Архивация не происходит; оперативная БД растёт бесконечно
- **Примечание:** Нет leader election — архивация запускается во всех репликах (идемпотентно)

**`Archiving__CutoffDays`**
- **Что:** Порог возраста для архивации (дни)
- **Пример:** `90`
- **По умолчанию:** 90
- **Поведение:** Задачи/MR'ы/планы, закрытые >90 дней назад, переносятся в архивную БД

**`Archiving__BatchSize`**
- **Что:** Количество записей для архивации в один батч
- **Пример:** `1000`
- **По умолчанию:** 1000
- **Примечание:** Большие батчи = меньше раундов в БД, но более высокая lock contention

**`Archiving__RunIntervalMinutes`**
- **Что:** Как часто запускается архивный сервис
- **Пример:** `60`
- **По умолчанию:** 60 (ежечасно)

### Task Reconciliation Service

**`TaskReconciliation__Enabled`** (раздел: `TaskReconciliation`)
- **Что:** Периодически ли синхронизировать зависимости задач из трекера
- **Допустимые значения:** `true`, `false`
- **По умолчанию:** `true`
- **Если false:** Зависимости задач загружаются только при создании или смене статуса

**`TaskReconciliation__RunIntervalMinutes`**
- **Что:** Как часто синхронизировать открытые задачи
- **Пример:** `30`
- **По умолчанию:** 30
- **Поведение:** Каждые N минут загружать зависимости открытых задач из всех трекеров

**`TaskReconciliation__BatchSize`**
- **Что:** Задачи, загружаемые на трекер за запуск
- **Пример:** `100`
- **По умолчанию:** 100

### Опрос при приёме (Polling)

Для соединения, чей тип провайдера — *poll* (`gitlab-poll`, `yandextracker-poll`), приложение
перечитывает его по таймеру вместо приёма вебхуков. У каждого соединения свой интервал в настройках;
эти глобальные параметры задают, работает ли поллер и как часто он просыпается — это пол (floor) для
интервала соединения.

**`VcsPolling__Enabled`** / **`TrackerPolling__Enabled`** (разделы: `VcsPolling`, `TrackerPolling`)
- **Что:** Работает ли поллер VCS / трекера
- **Значения:** `true`, `false`
- **По умолчанию:** `true`

**`VcsPolling__IntervalSeconds`** / **`TrackerPolling__IntervalSeconds`**
- **Что:** Как часто поллер просыпается и обходит poll-соединения — пол для собственного интервала соединения
- **Пример:** `60`
- **По умолчанию:** 60

**`TrackerPolling__MaxTasksPerRun`**
- **Что:** Сколько открытых задач перечитывается на соединение трекера за проход
- **Пример:** `500`
- **По умолчанию:** 500

### Миграции БД

**`Database__MigrateOnStartup`** (раздел: `Database`)
- **Что:** Накатывать ли незавершённые миграции EF Core при запуске
- **Значения:** `true`, `false`
- **По умолчанию:** `true`
- **Если false:** Применяйте миграции вручную (или из init-контейнера / CI) — рекомендуется для развёртывания на нескольких репликах, где одновременная авто-миграция гонялась бы

### Авторизация и Bootstrap

**`Authorization__BootstrapAdminObjectIds`** (раздел: `Authorization`)
- **Что:** Точка с запятой разделённый список OID'ов пользователей для bootstrap-админа
- **Пример:** `00000000-0000-0000-0000-000000000001;00000000-0000-0000-0000-000000000002`
- **По умолчанию:** Пусто (нет bootstrap-админов)
- **Поведение:** Пользователи, чьи `oid` claims совпадают с этими значениями, получают полные права автоматически
- **Безопасность:** Удалите после настройки; это постоянный админ-bypass если установлено
- **Случай использования:** Свежий деплой, где у никого нет начальных прав

**`Authorization__PermissionBootstrapEnabled`**
- **Что:** Автоматически ли заполнять permission claims при первом входе
- **Допустимые значения:** `true`, `false`
- **По умолчанию:** `true`
- **Поведение:** При первом запуске таблица `PermissionClaims` заполняется стандартными claims (`release.plan.approve`, `config.edit` и т.д.)

---

## Конфигурация внешних провайдеров

Провайдеры конфигурируются на основе **подключения** (хранится в БД), не глобально. Однако некоторые hints окружения существуют:

### Конфигурация VCS-провайдера

Конфигурируется при добавлении VCS-подключения в Admin UI. Общие поля:
- **API URL** — endpoint GitLab или другого VCS
- **Access Token** — зашифрован при сохранении

Всё остальное объявляет выбранный провайдер, и форма строится по его схеме, поэтому набор полей
зависит от типа:
- **Тип** — `gitlab-webhook` (GitLab шлёт события в ingress) или `gitlab-poll` (оркестратор опрашивает сам; добавляется **интервал опроса**, в секундах)
- **Правило связи** — как входящий merge request сопоставляется с задачей трекера: **источник ключа** (ветка, заголовок или метка) и **шаблон** (regex). Форма подключения показывает, какой ключ правило извлечёт из примера.

Готовность к выкатке — **не** поле подключения. Она настраивается на окружение (и, при желании, на
репозиторий) как именованное **правило готовности** над нормализованными сигналами — метка, статус
merge request'а или результат пайплайна — на страницах «Окружения» и «Правила готовности».

### Конфигурация Tracker-провайдера

Конфигурируется при добавлении Tracker-подключения. Общие поля — **API URL** и **Access Token**
(зашифрован при сохранении); остальное объявляет провайдер, и форма строится по его схеме:
- **Organization ID** — (Yandex Tracker) отправляется в заголовке `X-Org-Id`
- **Closed statuses** — (Yandex Tracker) список статусов через запятую, означающих, что задача завершена; пусто — используются значения по умолчанию (`closed, cancelled, rejected, resolved`)

Настройки провайдера хранятся как JSON в `TrackerConnection.ProviderSettingsJson`; секретные
настройки шифруются тем же key ring, что и токен доступа.

Пример:
```json
{
  "customFieldMap": {
    "dependency": "depends_on_field_id",
    "sprint": "sprint_field_id"
  }
}
```

Это провайдер-специфично и документируется в [Провайдеры](providers.md).

---

## OpenID Connect

Release Orchestrator полагается на внешний OIDC-провайдер. Конфигурация обычно находится в конфигурации вебприложения (например, `appsettings.json` или Azure AD в admin-портале), не в переменных окружения.

**Используемые claims:**
- `oid` — Уникальный идентификатор пользователя (обязателен)
- `name` — Отображаемое имя (опционально)
- `email` — Email адрес (опционально)

Убедитесь, что ваш OIDC-провайдер включает `oid` в ID tokens.

---

## Логирование и Наблюдаемость

### Логирование

**Пункт назначения:** Console (JSON-формат в production)

**Управление уровнем:** Установите через стандартный .NET Core:
```bash
LOGLEVEL_ReleaseOrchestrator=Debug
LOGLEVEL_Microsoft.EntityFrameworkCore=Warning
```

### Структурированное логирование

Все операционные события логируются как JSON. Пример:
```json
{
  "timestamp": "2025-01-15T10:30:00Z",
  "level": "Information",
  "logger": "ReleaseOrchestrator.Application.ReleasePlanning.ReleasePlanner",
  "message": "Release plan recalculated",
  "planId": "550e8400-e29b-41d4-a716-446655440000",
  "stageCount": 5,
  "conflictCount": 2
}
```

### Метрики (Prometheus)

**`Prometheus__Enabled`**
- **Что:** Выставлять ли endpoint скрейпа `/metrics`
- **Допустимые значения:** `true`, `false`
- **По умолчанию:** `true`
- **Endpoint:** `GET /metrics` на обоих хостах, текстовый формат Prometheus, анонимный и без rate-лимита
- **Экспортирует:** метрики запросов ASP.NET Core, .NET runtime (GC, thread pool, память), исходящие
  вызовы HTTP-клиента и тайминги сообщений Rebus
- **Если false:** нет endpoint `/metrics` и не регистрируются связанные с ним счётчики

### Экспорт traces и метрик (OpenTelemetry / OTLP)

**`OTEL_EXPORTER_OTLP_ENDPOINT`** (опционально)
- **Что:** Endpoint OpenTelemetry collector (также задаётся как `Otel__Endpoint`)
- **Пример:** `http://localhost:4317`
- **Если установлено:** traces экспортируются в collector, а метрики дублируются туда в дополнение к
  скрейпу на `/metrics`
- **Если не установлено:** traces не излучаются; метрики продолжают работать через Prometheus

**`Otel__Enabled`**
- **Что:** Выключатель OTLP-экспорта, приоритетнее заданного endpoint
- **По умолчанию:** `true`
- **Если false:** OTLP-экспорт выключен даже при заданном endpoint; на Prometheus не влияет

**`OTEL_EXPORTER_OTLP_PROTOCOL`** (опционально)
- **Что:** `grpc` (по умолчанию) или `http/protobuf`, по именам из спецификации OTLP

**Известное ограничение:** Prometheus хранит только метрики. Без OTLP-коллектора нет distributed
tracing по пути webhook → queue → processing.

---

## Безопасность

### HTTPS и Proxy

Приложение предполагает, что HTTPS терминируется на reverse proxy (Nginx, Traefik, API Gateway). Используйте:

```bash
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

Proxy должен устанавливать:
- `X-Forwarded-For` (client IP)
- `X-Forwarded-Proto` (scheme, должен быть `https`)
- `X-Forwarded-Host` (hostname)

### Безопасность Redis

**Критически:** Redis кэширует вычисленные права. Без аутентификации:
```bash
# Небезопасно — кто угодно в сети это админ
REDIS_CONNECTION_STRING=redis.company.com:6379
```

**Рекомендуемо:**
```bash
# С паролем
REDIS_CONNECTION_STRING=redis.company.com:6379,password=YourStrongPassword
```

Также конфигурируйте Redis с `--requirepass` и отключите `FLUSHALL` и `CONFIG` команды.

### Защита credentials

- **VCS tokens, tracker tokens:** Зашифрованы при сохранении используя **ASP.NET Core Data Protection**, хранятся в той же БД, где находятся сами ключи шифрования. **Критично:** Без сертификата (`DataProtection__CertificatePath`), ключи остаются незашифрованными в БД, и dump + restore эквивалентно хранению токенов в plaintext. Конфигурация отказывает при запуске вне Development, если не предоставлен сертификат или не установлено `DataProtection__AllowUnprotectedKeys=true`.
- **Не логируйте secrets:** Приложение избегает логирования значений токенов
- **Не коммитьте `.env`:** Добавьте в `.gitignore`

---

## Оптимизация производительности

### Connection Pool БД

EF Core's connection pool конфигурируется автоматически. Для production настройте в connection string:
```
Server=...;Max Pool Size=100;Min Pool Size=10;
```

### RabbitMQ Concurrency

Обработчики Rebus работают конкурентно — пул воркеров, каждый читает из входной очереди.
Настраивается через окружение:

| Переменная | По умолчанию | Смысл |
|---|---|---|
| `Queue__Workers` | 4 | Потоков-воркеров, каждый обрабатывает одно сообщение за раз |
| `Queue__PrefetchCount` | 16 | Сколько сообщений забирается из RabbitMQ наперёд, и максимальный параллелизм |

### Redis Connection Pool

StackExchange.Redis автоматически управляет pooling. Явная конфигурация не требуется.

---

## Чек-лист мониторинга

| Элемент | Как проверить | Что мониторить |
|---|---|---|
| **Здоровье БД** | `GET /health/ready` | 503 если БД недоступна |
| **Здоровье RabbitMQ** | `GET /health/ready` | 503 если очередь недоступна |
| **Здоровье Redis** | `GET /health/ready` | 503 если кэш недоступен |
| **Место на диске** | `df -h` на хосте | Архивная БД растёт ~3.6М rows/year при 10K задач/день |
| **Кэш прав** | `redis-cli DBSIZE` | Растёт → проверить memory leaks |
| **Активные подключения** | Логи + DB metrics | Должна стабилизироваться после начальной нагрузки |

---

## Шаблон переменных окружения

```bash
# Database
ConnectionStrings__Default=Server=sqlserver;Database=ReleaseOrchestrator;User Id=sa;Password=MyPassword;TrustServerCertificate=true;
ConnectionStrings__Archive=Server=sqlserver;Database=ReleaseOrchestratorArchive;User Id=sa;Password=MyPassword;TrustServerCertificate=true;

# RabbitMQ
Queue__Username=guest
Queue__Password=guest
Queue__Host=rabbitmq
Queue__Port=5672

# Redis
Redis__ConnectionString=redis:6379

# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=https://0.0.0.0:443
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# Archival
Archiving__Enabled=true
Archiving__CutoffDays=90
Archiving__RunIntervalMinutes=60

# Task Sync
TaskReconciliation__Enabled=true
TaskReconciliation__RunIntervalMinutes=30

# Authorization
Authorization__BootstrapAdminObjectIds=

# Observability (опционально)
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
LOGLEVEL_ReleaseOrchestrator=Information
```

---

## См. также

- [Начало работы](getting-started.md) - Как настроить локально
- [Архитектура](architecture.md) - Дизайн системы
- [Эксплуатация](operations.md) - Развёртывание и мониторинг
