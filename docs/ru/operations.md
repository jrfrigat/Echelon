# Echelon - Эксплуатация и развёртывание

> [English version ->](../en/operations.md) - [← Вернуться к документации](../README.md)

---

## Обзор

Этот документ охватывает production-развёртывание, мониторинг и операционные заботы Echelon.

**⚠️ Предупреждение:** Это приложение **никогда не запускалось и не развёртывалось в живой среде**. Следующие рекомендации основаны на анализе кода, не на production-опыте. Рассматривайте рекомендации как отправные точки; тщательно тестируйте в staging перед production.

---

## Развёртывание

### Требования

- **Kubernetes 1.20+** или Docker Swarm (или standalone server)
- **Microsoft SQL Server 2019+** (может быть управляемый сервис облака)
- **RabbitMQ 3.8+** (или облачный managed service)
- **Redis 6.0+** (или облачный managed service)
- **Reverse proxy** (Nginx, Traefik, API Gateway) с HTTPS терминацией
- **OpenID Connect провайдер** (Azure AD, Keycloak, Auth0 и т.д.)

### Docker образы

Приложение включает `docker-compose.yml` для локальной разработки. Для production:

```dockerfile
# Multi-stage build (пример)
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS builder
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.8
WORKDIR /app
COPY --from=builder /app .
EXPOSE 5000
ENTRYPOINT ["dotnet", "Echelon.Web.dll"]
```

**Base image:** `mcr.microsoft.com/dotnet/aspnet:10.0.8`
**SDK:** `mcr.microsoft.com/dotnet/sdk:10.0.300`

**Примечание:** Образы не были собраны в dev-среде (реестр заблокирован прокси). Проверьте доступность образа в вашей среде перед развёртыванием.

### Пример Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: echelon
spec:
  replicas: 3
  selector:
    matchLabels:
      app: echelon
  template:
    metadata:
      labels:
        app: echelon
    spec:
      containers:
      - name: web
        image: your-registry/echelon:latest
        ports:
        - containerPort: 5000
        env:
        - name: ConnectionStrings__Default
          valueFrom:
            secretKeyRef:
              name: db-credentials
              key: connection-string
        - name: ConnectionStrings__Archive
          valueFrom:
            secretKeyRef:
              name: db-credentials
              key: archive-connection-string
        - name: Queue__Username
          valueFrom:
            secretKeyRef:
              name: queue-credentials
              key: username
        - name: Queue__Password
          valueFrom:
            secretKeyRef:
              name: queue-credentials
              key: password
        - name: Redis__ConnectionString
          valueFrom:
            secretKeyRef:
              name: cache-credentials
              key: connection-string
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: ASPNETCORE_FORWARDEDHEADERS_ENABLED
          value: "true"
        livenessProbe:
          httpGet:
            path: /health
            port: 5000
          initialDelaySeconds: 10
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 5000
          initialDelaySeconds: 5
          periodSeconds: 5
        resources:
          requests:
            cpu: 100m
            memory: 256Mi
          limits:
            cpu: 500m
            memory: 1Gi

---
apiVersion: v1
kind: Service
metadata:
  name: echelon
spec:
  type: ClusterIP
  ports:
  - port: 80
    targetPort: 5000
  selector:
    app: echelon
```

---

## Health Checks

### Liveness (`/health`)

Указывает, работает ли процесс и может ли обрабатывать запросы.

```bash
curl http://localhost:5000/health
# Response: 200 OK, пустое тело
```

**Случай использования:** Kubernetes liveness probe, обнаружение рестарта процесса

### Readiness (`/health/ready`)

Проверяет три вещи: рабочую БД, архивную БД и координационный кэш.

Брокер намеренно **не** проверяется. При недоступности RabbitMQ ingress отвечает 503, который
отправители ретраят, поэтому простой брокера - не повод выводить API из ротации.

Архивная БД сообщает **Degraded**, а не Unhealthy: архивация фоновая, и вывести из ротации из-за
неё было бы хуже самого сбоя.

```bash
curl http://localhost:5000/health/ready
# Если здорово: 200 OK
# Если обязательная зависимость недоступна: 503 Service Unavailable
```

**Пример body ответа (недоступная БД):**
```json
{
  "status": "Unhealthy",
  "checks": {
    "database": {
      "status": "Unhealthy",
      "description": "Cannot connect to the operational database."
    },
    "archive-database": {
      "status": "Healthy"
    },
    "coordination": {
      "status": "Healthy"
    }
  }
}
```

**Случай использования:**
- Kubernetes readiness probe (pod удаляется из load balancer если не ready)
- Развёртывание: подождите 200 перед маркировкой healthy
- Мониторинг: alert если остаётся 503 >5 минут

---

## Мониторинг

### Метрики для наблюдения

| Метрика | Как проверить | Warning порог | Действие |
|--------|---|---|---|
| **API response time** | Логи, APM tool | >500ms p99 | Проверьте slow queries БД |
| **Queue depth (RabbitMQ)** | RabbitMQ admin, логи | >10,000 messages | Масштабируйте consumers или исследуйте stall |
| **Database connections** | `SELECT COUNT(*) FROM sys.dm_exec_sessions` | >80 (если max 100) | Найдите long-running queries, масштабируйте connections |
| **Archive job runtime** | Логи | >30 минут (если hourly) | Исследуйте slow deletes, рассмотрите smaller batches |
| **Redis memory** | `redis-cli INFO memory` | >80% of limit | Исследуйте memory leaks, purge old cache entries |
| **Permission cache hit rate** | Monitor hits vs. DB queries | <80% | Рассмотрите cache TTL tuning |
| **Active users** | Azure AD sign-in logs, app metrics | N/A | Baseline для capacity planning |

### Ключевые log patterns

Ищите эти в логах:

- **"Database connection failed"** - Проверьте доступность SQL Server
- **"RabbitMQ connection failed"** - Проверьте RabbitMQ, network, credentials
- **"Release plan recalculation failed"** - Проверьте логи для graph algorithm issues
- **"Archive batch failed"** - Проверьте foreign key constraints, disk space
- **"Permissions cache error"** - Проверьте Redis availability

### Метрики через Prometheus

Оба хоста отдают метрики на `/metrics` в текстовом формате Prometheus - включено по умолчанию и не
требует коллектора, Prometheus скрейпит их напрямую. Что экспортируется:

- **ASP.NET Core** - частота, длительность и число активных запросов (API Core, вебхуки Ingress)
- **.NET runtime** - GC, куча, thread pool, число исключений, CPU и working set
- **HTTP-клиент** - исходящие вызовы к GitLab и трекеру
- **Rebus** - тайминги отправки, приёма и обработки сообщений

Отключить endpoint - `Prometheus__Enabled=false`. Он анонимный и вне rate-лимитера, как health-пробы:
scrape доходит до процесса независимо от аутентификации и не тратит бюджет запросов API.

Готовый стек лежит рядом с compose-файлами:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
# Prometheus на http://localhost:9090 (уже скрейпит core и ingress)
# Grafana на http://localhost:3000 (Prometheus подключён как источник данных по умолчанию)
```

Цели скрейпа - в `observability/prometheus.yml`.

### Traces через OpenTelemetry

Traces уходят по OTLP при настроенном коллекторе (`OTEL_EXPORTER_OTLP_ENDPOINT` установлен); метрики
туда же дублируются, оставаясь при этом доступными для скрейпа:

```bash
# Пример: отправить traces и метрики в коллектор
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
dotnet run --project src/Echelon.Web
```

Traces захватывают:
- Webhook ingestion (Ingress)
- Отправку и обработку сообщений (Rebus проносит W3C trace context через RabbitMQ, поэтому путь
  Ingress -> queue -> Core остаётся одним trace)
- Database operations (EF Core)
- Release plan calculations

**Ограничение:** Prometheus хранит только метрики. Без OTLP-коллектора нет distributed tracing -
счётчик Prometheus скажет, *что* обработка вебхуков замедлилась, но не *какой* span.

---

## Масштабирование

### Horizontal Scaling (несколько реплик)

Все компоненты stateless:

- **Web pods:** Автоматически масштабируются за load balancer
- **RabbitMQ:** Требует cluster setup (см. RabbitMQ docs)
- **Database:** Shared между всеми pods (SQL Server replication если desired)
- **Redis:** Shared cache (Redis Cluster если масштабирование beyond single instance)
- **Archive service:** Запускается в каждом поде (идемпотентно, no coordination needed)

**Concurrency note:** Archive service регистрируется в каждом поде, но гейтится распределённой арендой - за ночь цикл проходит один раз на весь деплой, а не по разу на под. Это взаимное исключение, а не консенсус: корректность по-прежнему держится на идемпотентной вставке и ретрае, а при недоступности хранилища аренды цикл пропускается (fail-closed), а не выполняется всеми. То же верно для координатора выкаток, поллеров и реконсиляции задач.

### Database Connection Pooling

EF Core автоматически управляет pool (default 100 connections). Мониторьте:

```sql
SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID('Echelon');
```

Если approaching limit, увеличьте в connection string:

```
Server=...;Max Pool Size=200;
```

### RabbitMQ Tuning

Текущая конфигурация (из кода):
- Consumers per deployment: Default (проверьте `Program.cs`)
- Retry policy: Exponential backoff (initial 0s, max 1h)
- Dead letter queue: Not configured

**Рекомендация для production:**
- Включите RabbitMQ Dead Letter Queue (DLQ) для failed messages
- Установите мониторинг на DLQ depth
- Конфигурируйте consumer concurrency based on workload (3-5 per pod recommended)

---

## Maintenance

### Database Backups

**Частота:** Daily (adjust based on criticality)
**Scope:** Обе БД `Echelon` и `EchelonArchive`

**Пример (SQL Server):**
```bash
sqlcmd -S your-server -U sa -P password -Q "
BACKUP DATABASE Echelon 
TO DISK = '/var/opt/mssql/backup/Echelon.bak'
WITH FORMAT, COMPRESSION;
"
```

### Archive Database Maintenance

Archive DB растёт ~3.6М rows/year при 10K tasks/day throughput. После 2+ лет рассмотрите:

1. **Index maintenance:** Rebuild indexes на `TaskItem`, `MergeRequest`
2. **Clean up old archives:** Опционально delete records >2 лет (not implemented in code)
3. **Separate storage:** Archive DB может переместиться на дешёвый tier (cold storage)

### Periodic Tasks

| Task | Frequency | Owner | Notes |
|---|---|---|---|
| **Archival** | Hourly (configurable) | Automatic (Archive service) | Перемещает closed tasks/MRs >90 дней |
| **Task sync** | Every 30 minutes (configurable) | Automatic (Task Reconciliation) | Загружает open task dependencies |
| **Permission cache invalidation** | On-demand | Automatic (permission changes) | Нет TTL - invalidate на change only |
| **Health check** | Continuous | Kubernetes/monitoring | `/health` и `/health/ready` |

---

## Известные ограничения и workarounds

### Обработка недоступности RabbitMQ Broker

Если RabbitMQ down, webhooks возвращают 503 Service Unavailable с заголовком Retry-After. Большинство систем VCS (например GitLab) уважают это и переотправляют доставку вебхука. Event buffering не реализован. Рекомендация:

- Реализуйте buffering в reverse proxy или API gateway
- Или: Accept event loss и monitor RabbitMQ health closely

### Распределённая аренда для Archive (не leader election)

Archive service запускается в каждом поде, но гейтится распределённой арендой на Redis. За раз только один под может держать аренду и запускать архивацию. Это **не алгоритм консенсуса**, а механизм взаимного исключения: один Redis - единственная точка отказа. Однако это приемлемо, так как архивация идемпотентна. Рекомендация:

- Убедитесь Redis доступен и здоров (часть `/health/ready`)
- Если Redis недоступен, архивация пропускается (fail-closed) - планы не архивируются до восстановления
- Мониторьте excessive locking на archive tables (признак длительных циклов архивации)
- Если performance деградирует из-за lock contention, рассмотрите более длительный Lease Duration в коде

### SQL Server требует регистрозависимой коллации на двух колонках

`RepositoryBranches.Name` и `Repositories.ExternalId` принудительно переводятся миграцией в
`Latin1_General_100_BIN2`: обе колонки лежат под уникальным индексом и хранят идентификаторы,
регистр которых значим у источника - имена веток Git и пути проектов GitLab. При обычном
регистронезависимом умолчании инстанса `feature/Login` и `feature/login` - один дублирующийся ключ,
и вставка падает внутри консьюмера, который затем переотправляется и падает бесконечно.

Миграция это закрывает. Следить нужно за базой, созданной или восстановленной **в обход** миграций:
собранная руками схема или восстановление, вернувшее умолчание инстанса. Проверка:

```sql
SELECT name, collation_name FROM sys.columns WHERE object_id = OBJECT_ID('RepositoryBranches') AND name = 'Name';
```

PostgreSQL ничего не требует: его коллация по умолчанию уже сравнивает регистрозависимо.

### Limited Observability без OTEL

Async paths имеют poor visibility. Рекомендация:

- Включите OTEL + Jaeger/DataDog/similar
- Или: Monitor через RabbitMQ admin + database query logs
- Установите alerts для `/health/ready` returning 503

### PostgreSQL поддержан, но ни разу не запускался

Обе БД поддержаны на равных: одна модель, один набор тестов и по сборке миграций на каждую
(`Echelon.Migrations.MsSql`, `...Migrations.Postgres`). Три места, где они действительно
расходятся, изолированы в `ProviderSpecificMapping` - токен конкурентности (`rowversion` против
системной колонки `xmin`), диалект фильтрованного индекса и регистрозависимая коллация выше, - и
каждое закреплено тестом, который строит обе модели офлайн.

Честная оговорка не про поддержку, а про обкатку: **сервер PostgreSQL для этого приложения не
запускался ни разу**, то есть его миграции не накатывались ни на что. Миграции SQL Server -
накатывались, начисто, на живой инстанс 2022. Сборка модели и генерация SQL - сильнейшая проверка,
возможная без сервера, но не замена ему.

---

## Security Checklist

- [ ] HTTPS enabled (reverse proxy с valid certificate)
- [ ] `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` установлен
- [ ] Redis requires password (`Redis__ConnectionString` includes password)
- [ ] RabbitMQ credentials strong (not default guest/guest)
- [ ] SQL Server connections используют strong SA password + firewall
- [ ] OIDC credentials хранятся в secure secret management (not `.env` files)
- [ ] `AUTHORIZATION__BOOTSTRAPADMINOBJECTIDS` пусто в production (или removed)
- [ ] API rate limiting enabled (consider reverse proxy)
- [ ] Audit logging enabled (check logs для permission changes)
- [ ] Data Protection keys backed up (хранятся в `DataProtectionKeys` table)

---

## Disaster Recovery

### Data Loss Scenarios

| Scenario | Impact | Recovery |
|---|---|---|
| **SQL Server database deleted** | Complete data loss | Restore из backup |
| **Redis cache cleared** | Permissions re-computed на next request (slow) | No action needed (cache refill) |
| **RabbitMQ messages lost** | Webhook events потеряны (no retry) | Manual re-trigger из VCS/tracker |
| **Active plan потеряна** | Users видят no plan до auto-recalculation | Manually import YAML backup |

### Backup Strategy

```bash
# Weekly full backup
sqlcmd -S server -U sa -P pwd -Q "
BACKUP DATABASE Echelon 
TO DISK = '/mnt/backups/full_$(date +%Y%m%d).bak' 
WITH FORMAT, COMPRESSION;
"

# Daily incremental (если using full backup model)
BACKUP DATABASE Echelon 
TO DISK = '/mnt/backups/incr_$(date +%Y%m%d).bak' 
WITH DIFFERENTIAL;
```

### Restore Procedure

```bash
# Restore latest full backup
RESTORE DATABASE Echelon 
FROM DISK = '/mnt/backups/full_20250115.bak' 
WITH REPLACE;

# Restore latest incremental (если applicable)
RESTORE DATABASE Echelon 
FROM DISK = '/mnt/backups/incr_20250117.bak' 
WITH RECOVERY;
```

---

## Support & Troubleshooting

### Common Issues

**Issue:** API возвращает 500 с "Database connection timeout"
- **Cause:** SQL Server overloaded или unreachable
- **Check:** `SELECT COUNT(*) FROM sys.dm_exec_sessions`, network connectivity
- **Fix:** Увеличьте connection pool, масштабируйте БД, restart container

**Issue:** Webhooks возвращают 503 "RabbitMQ unavailable"
- **Cause:** RabbitMQ down или network issue
- **Check:** RabbitMQ admin UI (port 15672), network policies
- **Fix:** Restart RabbitMQ, check firewall rules, масштабируйте если queue depth high

**Issue:** План не обновляется после создания MR
- **Cause:** Task не linked, либо не отработал пересчёт. Обратите внимание: "готового" статуса для
  попадания в план MR *не* требуется - готовность проверяется по окружению на запуске, а не при
  планировании, поэтому неготовый MR всё равно виден в своём плане
- **Check:** `/health/ready` (должно быть 200), check logs для sync errors
- **Fix:** Manually check branch name (должно include task key), verify label config

---

## См. также

- [Архитектура](architecture.md) - Дизайн системы и компоненты
- [Конфигурация](configuration.md) - Все переменные окружения
- [Начало работы](getting-started.md) - Локальная настройка

---

## Полезные ссылки

- [.NET 10 Deployment Guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [SQL Server Backup & Restore](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/back-up-and-restore-of-sql-server-databases)
- [RabbitMQ Clustering](https://www.rabbitmq.com/clustering.html)
- [Redis Cluster](https://redis.io/docs/management/replication/)
- [Kubernetes Best Practices](https://kubernetes.io/docs/concepts/configuration/overview/)
