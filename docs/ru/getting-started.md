# Release Orchestrator — Начало работы

> [English version ->](../en/getting-started.md) - [← Вернуться к документации](../README.md)

---

## Требования

- **.NET SDK 10** (для сборки)
- **SQL Server 2019+** (оперативная и архивная БД)
- **RabbitMQ 3.8+** (очередь сообщений)
- **Redis 6.0+** (кэш вычисленных прав)
- **Docker & Docker Compose** (рекомендуется для локальной разработки)
- Система поддерживающая OpenID Connect (например, Azure AD, keycloak или любой OIDC-провайдер)

---

## 1. Локальная установка с Docker Compose

Самый удобный способ запуска локально — через Docker Compose. Все необходимые сервисы включены.

### 1.1 Подготовка файла `.env`

В корне проекта создайте `.env` на основе `.env.example`:

```bash
# Пример: C:\Job\Projects\FrigaT\ReleaseOrchestrator\.env

# SQL Server
MSSQL_SA_PASSWORD=YourComplexPassword123!
SA_PASSWORD=YourComplexPassword123!

# RabbitMQ
RABBITMQ_DEFAULT_USER=guest
RABBITMQ_DEFAULT_PASS=guest

# Redis (оставьте пусто для разработки без пароля)
REDIS_PASSWORD=

# OpenID Connect (настройте вашего провайдера)
OIDC_AUTHORITY=https://your-oidc-provider/.well-known/openid-configuration
OIDC_CLIENT_ID=your-client-id
OIDC_CLIENT_SECRET=your-client-secret
OIDC_REDIRECT_URI=https://localhost:5173/authentication/login-callback

# Release Orchestrator
ASPNETCORE_ENVIRONMENT=Development
CONNECTION_STRING_DEFAULT=Server=sqlserver;Database=ReleaseOrchestrator;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
CONNECTION_STRING_ARCHIVE=Server=sqlserver;Database=ReleaseOrchestratorArchive;User Id=sa;Password=YourComplexPassword123!;TrustServerCertificate=true;
REDIS_CONNECTION_STRING=redis:6379
QUEUE_USERNAME=guest
QUEUE_PASSWORD=guest
QUEUE_HOST=rabbitmq
QUEUE_PORT=5672
```

**⚠️ ВАЖНО для production:**
- Используйте стойкие пароли (не `guest`)
- Включите аутентификацию Redis с флагом `--requirepass`
- Используйте HTTPS с правильными сертификатами
- Установите `ASPNETCORE_ENVIRONMENT=Production`
- Настройте реальные OIDC-credentials

### 1.2 Запуск сервисов

```bash
docker-compose up -d
```

Дождитесь готовности SQL Server (30-60 секунд):

```bash
docker-compose logs -f sqlserver | grep "Recovery is complete"
```

### 1.3 Применение миграций БД

```bash
# Из корня проекта
dotnet ef database update --project src/ReleaseOrchestrator.Migrations.MsSql \
  --startup-project src/ReleaseOrchestrator.Web \
  --context AppDbContext

# Повторите для архивного контекста
dotnet ef database update --project src/ReleaseOrchestrator.Migrations.MsSql \
  --startup-project src/ReleaseOrchestrator.Web \
  --context ArchiveDbContext
```

### 1.4 Инициализация админа

При первом запуске у никого нет прав. Используйте режим bootstrap-админа для создания первого админа:

```bash
# Установите переменную окружения (временно, только для этого запуска)
$env:AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS="your-oid-value"

# Затем запустите приложение
dotnet run --project src/ReleaseOrchestrator.Web
```

**Как найти свой OID:**
1. Войдите с помощью OpenID-провайдера
2. Приложение создаст пользователя (сохранится в БД)
3. Перейдите в **Admin → Users** (будет пусто из-за отсутствия прав)
4. Проверьте логи приложения — там будет ваше значение `oid`
5. Установите `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` на это значение и перезагрузитесь

Когда вы войдёте как bootstrap-админ:
- Ваш пользователь получит все права автоматически
- Вы можете добавить других админов через **Admin → Permissions**
- Удалите `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` для production (fail-safe: если установлена, любой с совпадающим OID становится админом)

---

## 2. Конфигурация внешних подключений

Перед построением плана релиза подключитесь к вашему VCS и трекеру.

### 2.1 Добавление VCS-подключения (GitLab)

1. **Перейдите в Admin → VCS Connections**
2. **Нажмите "+ New Connection"**
3. **Заполните:**
   - **Name:** `my-gitlab` (используется в YAML и конфигурации стеков)
   - **Type:** `GitLab`
   - **API URL:** `https://gitlab.company.com` или `https://gitlab.com`
   - **Access Token:** Personal access token с правами `api`, `read_repository`
   - **Ready-for-Deploy Label:** (опционально) Имя лейбла в GitLab, который помечает MR'ы готовыми к деплою (например, `ready-deploy`)

4. **Нажмите Save** — приложение валидирует подключение и определяет версию GitLab

**Примечание:** Если оставить "Ready-for-Deploy Label" пустым, в план попадут только MR'ы, явно помеченные через API.

### 2.2 Добавление Tracker-подключения (Yandex Tracker)

1. **Перейдите в Admin → Tracker Connections**
2. **Нажмите "+ New Connection"**
3. **Заполните:**
   - **Name:** `my-tracker`
   - **Type:** `Yandex Tracker`
   - **API URL:** `https://api.tracker.yandex.net` или ваша self-hosted инстанция
   - **Access Token:** OAuth-токен с правом `write:tracker`
   - **Organization ID:** Числовой ID организации из Yandex Tracker

4. **Нажмите Save** — приложение валидирует подключение

### 2.3 Добавление репозитория

1. **Перейдите в Admin → Repositories**
2. **Нажмите "+ New Repository"**
3. **Заполните:**
   - **Name:** (отображаемое имя, например "API Backend")
   - **External ID:** (путь в GitLab, например `my-group/my-project`)
   - **VCS Connection:** Выберите из выпадающего списка
   - **Tracker Connection:** (опционально) Выберите, если MR'ы репозитория содержат ссылки на задачи

4. **Нажмите Save** — приложение проверит доступ к репозиторию

### 2.4 Добавление стеков

1. **Перейдите в Admin → Stacks**
2. **Создайте стеки для ваших групп релиза:**
   - `backend` (все микросервисы)
   - `frontend` (веб-интерфейс)
   - `data-migrations` (только изменения БД)
3. **Назначьте репозитории стекам:**
   - Выберите стек
   - Добавьте репозитории (много репозиториев на стек)
4. **Установите зависимости между стеками:**
   - `data-migrations` → `backend` (Hard: миграции должны завершиться)
   - `backend` → `frontend` (Soft: предпочтительный порядок, но не обязателен)

---

## 3. Запуск первого плана релиза

### 3.1 Откройте MR в вашем репозитории

Помечите MR как готовый к деплою:

**Вариант A: Через лейбл (если настроен)**
- Добавьте лейбл, указанный в "Ready-for-Deploy Label"
- Приложение автоматически обнаружит это и включит MR в план

**Вариант B: Через API**
```bash
curl -X PATCH https://localhost:5173/api/merge-requests/{mr-id}/status \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"status": "ReadyForDeploy"}'
```

### 3.2 Просмотр плана

1. **Перейдите в UI:** `https://localhost:5173`
2. **Откройте Release → Plans**
3. Вы должны увидеть план со стадиями, показывающими, какие MR'ы можно катить параллельно

### 3.3 Ручное редактирование

1. **Перетаскивайте** MR'ы между стадиями для переупорядочивания
2. **Нажмите "+ Stage"** для вставки новой стадии
3. **Нажмите "Save & Export"** для скачивания YAML в систему контроля версий

---

## 4. Остановка сервисов

```bash
docker-compose down
```

Для удаления данных:
```bash
docker-compose down -v
```

---

## 5. Интеграция с вашей CI/CD

Приложение предоставляет:
- **REST API:** `/api` (документация по адресу `/swagger`)
- **BFF (Backend for Frontend):** `/bff` (для PWA)

Пример: Запуск пересчёта плана после успешного прохождения всех тестов:
```bash
curl -X POST https://your-release-orchestrator/api/plans/recalculate \
  -H "Authorization: Bearer <api-token>"
```

---

## 6. Решение проблем

### "Database connection failed"
- Убедитесь, что SQL Server запущен: `docker-compose logs sqlserver`
- Проверьте `CONNECTION_STRING_DEFAULT` в `.env`
- Убедитесь, что пароль SA совпадает

### "RabbitMQ connection failed"
- Проверьте: `docker-compose logs rabbitmq`
- Убедитесь, что credentials совпадают с `.env`

### "У меня нет прав доступа"
- Установите `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` с вашим OID
- Проверьте логи: `"User {oid} initialized as bootstrap admin"`
- Перезагрузите приложение

### "Нет связи задач с MR'ами"
- Убедитесь, что имена веток следуют шаблону: `{TRACKER_KEY}-{number}` (например, `TASK-123-fix-bug`)
- Проверьте, что задача существует в трекере и не закрыта
- Подождите ~30 секунд для task sync consumer

---

## См. также

- [Архитектура](architecture.md) - Дизайн системы и поток данных
- [Конфигурация](configuration.md) - Все переменные окружения
- [Эксплуатация](operations.md) - Health-check'и и мониторинг
