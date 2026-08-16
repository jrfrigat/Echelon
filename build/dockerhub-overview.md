# Echelon

Release planning and rollout across issue trackers and repositories. It reads tasks from a tracker
and merge requests from one or more VCS connections, works out everything a task waits on, orders the
work into deploy waves, and drives the rollout into each environment. Built on .NET 10, with a Blazor
admin PWA.

This image is the application host: the ASP.NET Core API plus the admin PWA, served from the same
origin. A second image, `frigat/echelon-ingress`, receives provider webhooks and can be
exposed separately so the API does not have to be. You provide the database, the broker and,
optionally, Redis; the image bundles none of them.

## Run

```sh
docker run -d --name echelon -p 8081:8080 \
  -e ConnectionStrings__Default="Server=db;Database=Echelon;User Id=sa;Password=...;TrustServerCertificate=True" \
  -e ConnectionStrings__Archive="Server=db;Database=EchelonArchive;User Id=sa;Password=...;TrustServerCertificate=True" \
  -e Queue__Host=rabbitmq -e Queue__Username=... -e Queue__Password=... \
  -e Coordination__Provider=memory -e Coordination__SingleInstance=true \
  -e DataProtection__CertificatePath=/certs/dataprotection.pfx \
  frigat/echelon:latest
```

The full stack, including SQL Server, RabbitMQ and Redis, is a `docker compose up` away with the
compose files in the repository. PostgreSQL is supported through
`docker-compose.postgres.yml`; a single replica without Redis through
`docker-compose.single-instance.yml`.

## Things worth knowing before you run it

- **Migrations are applied at startup** by default. With more than one replica turn that off
  (`Database__MigrateOnStartup=false`) and apply them from CI or an init container, or the replicas
  race each other.
- **The Data Protection key ring lives in your database**, next to the provider access tokens it
  encrypts, so the host refuses to start without `DataProtection__CertificatePath`. Set
  `DataProtection__AllowUnprotectedKeys=true` only if you accept that a database dump then contains
  usable tokens.
- **`/health` is liveness, `/health/ready` is readiness.** Readiness fails while migrations are
  pending, so an orchestrator will not send traffic to an instance whose database is not ready.

## Configuration

Every setting and environment variable is documented in
[docs/en/configuration.md](https://github.com/jrfrigat/echelon/blob/main/docs/en/configuration.md).

## Links

- Source and documentation: https://github.com/jrfrigat/echelon
- License: MIT
