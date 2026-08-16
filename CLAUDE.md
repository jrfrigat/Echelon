# Echelon - Claude Code Configuration

## Rules

- Do what has been asked; nothing more, nothing less
- NEVER create files unless absolutely necessary - prefer editing existing files
- NEVER create documentation files unless explicitly requested
- NEVER save working files or tests to root - use `/src`, `/tests`, `/docs`, `/config`, `/scripts`
- ALWAYS read a file before editing it
- NEVER commit secrets, credentials, or .env files
- Keep files under 500 lines
- Validate input at system boundaries
- **Коммиты БЕЗ трейлера co-author** (`Co-authored-by: ...`) - никогда не добавлять
- **XML-документация обязательна** на public типах/членах/параметрах - там, где комментарий говорит больше имени. Держится на ревью, не на компиляторе: CS1591 заглушён (как в Flare), иначе он заставляет писать "Gets or sets the Id" и обесценивает комментарии как жанр
- **Версии пакетов - ТОЛЬКО в `Directory.Packages.props`** (Central Package Management). В `.csproj` - `<PackageReference Include="X" />` без `Version`. Одна правка вместо восьми, и версии не расходятся молча
- **Перед коммитом** - `bash scripts/clean-empty-files.sh`. Незаэкранированный `>` в shell-команде создаёт пустой файл с именем следующего токена (`_success`, `t.ClosedAt`); такие файлы уже трижды попадали в коммиты
- Правки, ломающие публичный контракт, допустимы, если оправданы - но должны быть видны на `dotnet build`, а не в рантайме

## Архитектура

Onion / ports-adapters, зависимости **только внутрь** (эталон - репозиторий Flare):

```
Core (enum'ы, чистый разбор; ноль зависимостей - ни одной)
  ← Application (порты, контракты сообщений, алгоритм планирования - без EF)
      ← Infrastructure (EF-модели, DbContext, адаптеры: Rebus, Redis, DataProtection)
      ← Providers.Abstractions (контракты провайдеров) ← Providers.GitLab / Providers.YandexTracker
          ← Web (корень композиции, API) / Ingress.Webhooks
```

Правила:

- **Ядро не знает ни об одном конкретном провайдере.** Имена GitLab/YandexTracker, их словари статусов и форматы ключей задач живут в адаптерах, не в `Core`. В домене - только нормализованные значения
- **EF-сущности живут в `Infrastructure/Persistence/Models` и наружу не выходят.** Ни Application, ни Providers.Abstractions их не видят: планировщик принимает `PlanMergeRequest`, фабрики - `*ConnectionDescriptor`. Раньше сущности торчали в обоих, и `ReleasePlanGraph` читал навигации EF - то есть требовал от вызывающего цепочку `Include`, забыть звено в которой означало не ошибку, а пустую коллекцию и молча неверный план
- **Маппинг - атрибутами на модели, а не в `OnModelCreating`.** Открыл модель - видишь ключи, длины, индексы и каскады; бегать по файлам не нужно. Fluent остаётся ровно для двух вещей, которым атрибута не существует: **фильтрованный** индекс (`[Index]` не умеет `HasFilter`) и **конвертация значений** (`MergeRequestStatus` хранится строкой). Обе живут в `Persistence/Configurations` и объясняют в комментарии, почему они там
- **`Restrict` - не конвенция.** Обязательная связь по умолчанию `Cascade`, поэтому каждый `[DeleteBehavior(DeleteBehavior.Restrict)]` - осознанный запрет. Потерять его - значит превратить заблокированное удаление в успешное, уносящее чужие строки. Держится тестом `ModelMappingTests`
- **Дубль индекса `has-pending-model-changes` не ловит.** Атрибут плюс забытая fluent-строка дают два индекса в модели и один в БД - схема идентична, проверка зелёная. Ловится только `ModelMappingTests.NoEntityDeclaresTheSameIndexTwice`
- **Алгоритм планирования (`Application/ReleasePlanning`) не зависит от EF** - он чистый и тестируется без БД. Именно недостижимость этого кода для теста скрыла инверсию рёбер графа до аудита
- **Провайдеры регистрируются на этапе компиляции** (keyed services + фабрика), без динамической загрузки сборок. Динамическая загрузка и контейнерная поставка взаимно обнуляются: единственный выигрыш - "добавить провайдера без пересборки", а образ пересобирается всё равно
- **Локализация** - `Resources/*.resx` (нейтральная культура = en) + `*.ru.resx`. Логи не локализуются: они для операторов и должны быть на одном языке

## Build & Test

- ALWAYS run tests after code changes
- ALWAYS verify build succeeds before committing

```bash
dotnet build Echelon.slnx -v q --nologo   # must be 0 errors, 0 warnings
dotnet test Echelon.slnx
bash scripts/clean-empty-files.sh                        # before every commit
```

`TreatWarningsAsErrors=true`, поэтому "0 предупреждений" - не пожелание, а условие сборки.

### Ограничения среды (проверено, не тратьте время заново)

- **`python3` - заглушка Windows Store: печатает `Python`, ничего не делает и возвращает `0`.** Самый опасный вид отказа: скрипт "отработал успешно", файл не изменился. Так молча не применились правки конфигураций EF, а сборка и `has-pending-model-changes` при этом оставались зелёными. Файлы править только Write/Edit; для текстовых замен - `perl -pi -e` или `sed`, и **проверять результат `grep`, а не кодом возврата**
- **nuget.org закрыт прокси, но доступен в обход него** (проверено 2026-08-09). В окружении заданы `HTTP_PROXY`/`HTTPS_PROXY` на `ai.comss.one:8888`, и он отвечает `403` на `api.nuget.org` -> `NU1301` и провал restore. Прямое соединение работает, поэтому снимайте переменные для одной команды, а не правьте источники:
  ```bash
  env -u HTTP_PROXY -u HTTPS_PROXY -u http_proxy -u https_proxy dotnet restore Echelon.slnx
  ```
  Так приехал `Flare 0.17.1`. **Не говорите, что пакет недостижим, не попробовав это** - заметка здесь дважды переворачивалась (было "заблокирован", стало "доступен" 17.07, снова закрылось к 09.08), то есть состояние прокси меняется и проверять надо каждый раз. `NU1900` (предупреждение, не ошибка) - признак, что аудит уязвимостей молча ничего не проверил; при рабочем обходе он не появляется, и **NU1902/NU1903 остаются ошибками**
- **Реестр Docker фильтруется** - образы не собрать, теги не проверить
- **Тесты с БД пишутся - через EF SQLite** (`DataSource=:memory:`, соединение держать открытым: база живёт ровно столько, сколько последнее соединение). Провайдера EF in-memory по-прежнему нет и он не нужен. При EF 9 версии расходились и это было невозможно; переход на .NET 10 ограничение снял. Образец - `tests/.../ReleasePlanning/PlannerTestBase.cs`
- **SQLite - не SQL Server.** Поведение FK и типов совпадает не во всём: цепочки `Include`, фильтрованные индексы и логика запросов проверяются, порядок каскадов - нет. Что не проверено, писать в тесте прямо
- **Локальный SQL Server доступен** (проверено 2026-07-17). Есть и LocalDB (`sqllocaldb`, `(localdb)\MSSQLLocalDB`), и настоящий сервер на `localhost`, и `sqlcmd`, и уже скачанный образ `mcr.microsoft.com/mssql/server:2022-latest` - фильтр реестра не мешает использовать имеющийся. То, что "интеграционных тестов нет, потому что негде", больше не аргумент: 17.07.2026 все шесть миграций накатились начисто, и на живой БД подтвердились два инварианта, которые SQLite не воспроизводит, - второй активный план отвергается (2601), удаление MR из плана блокируется `Restrict` (547)
- **Локальный PostgreSQL 16 доступен** (проверено 2026-08-07). Служба `postgresql-x64-16` запущена на 5432, бинарники в `C:\Program Files\PostgreSQL\16\bin`. **Пароль чужого кластера не нужен и спрашивать его не надо** (`scram-sha-256`): свой чистый кластер поднимается за минуту и не требует ничьих секретов -
  ```bash
  export PATH="/c/Program Files/PostgreSQL/16/bin:$PATH"
  initdb -D "$SCRATCH/pgdata" -U postgres --auth=trust --encoding=UTF8
  # ЗАПУСКАТЬ ТОЛЬКО через run_in_background: pg_ctl/postgres на переднем плане
  # убивается таймаутом Bash вместе с сервером (0xC0000142 в логе)
  postgres -D "$SCRATCH/pgdata" -p 5433
  ```
  "Миграции Postgres негде проверить" больше не аргумент: 07.08.2026 все 32 накатились начисто, подтвердились три расхождения провайдера (нет колонки `RowVersion` -> системная `xmin`; фильтр индекса `WHERE "IsActive"`; регистрозависимость по умолчанию), и на живой БД воспроизвелись оба инварианта ретенции - каскад выкатки на шаги и события и блокировка `Restrict` до неё
- **sqlcmd ставит `QUOTED_IDENTIFIER OFF`** (в отличие от SSMS), а фильтрованный индекс требует `ON` для любой записи в таблицу - иначе `Msg 1934`. Приложение через SqlClient ставит `ON` само; скриптам нужен флаг `-I` или явный `SET`
- **Миграции - на оба провайдера, всегда обе.** Добавить в MsSql и забыть Postgres - значит сломать вторую половину деплоев, и заметит это только CI:
  ```bash
  dotnet ef migrations add <Name> --project src/Echelon.Migrations.MsSql    --context AppDbContext
  dotnet ef migrations add <Name> --project src/Echelon.Migrations.Postgres --context AppDbContext
  ```
- **Расхождения SQL Server и PostgreSQL живут в `ProviderSpecificMapping`** и больше нигде. Их два, и оба найдены не чтением, а сборкой модели: токен конкурентности (`rowversion` против системной `xmin` - Npgsql молча принимает `[Timestamp]` и делает `bytea`, который никогда не заполняется, то есть проверка конкурентности **не срабатывает вообще**) и фильтр индекса (`[IsActive] = 1` против `"IsActive"` - фильтр это кусок SQL, EF передаёт его дословно). Добавляете третье - туда же, с объяснением
- **Даты: только `Kind=Utc`.** PostgreSQL кладёт `DateTime` в `timestamptz`, Npgsql пишет туда лишь `Utc` и бросает на `Local`/`Unspecified`; SQL Server проглатывает любой - поэтому баг невидим, пока не запустят вторую БД. В адаптерах разбирать даты через `DateTimeOffset` и отдавать `.UtcDateTime`
