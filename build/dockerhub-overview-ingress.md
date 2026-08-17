# Echelon Ingress

The webhook front door for [Echelon](https://hub.docker.com/r/frigat/echelon), which plans and runs
releases around a task rather than a branch. This image receives provider webhooks, verifies each
one, normalizes it and hands it to the broker; the application host picks it up from there.

It exists so the API does not have to be reachable from the internet. **The ingress holds no
database connection at all** - it publishes to RabbitMQ and nothing else, so the only thing you
expose is a front door that cannot read your data.

## Run

```sh
docker run -d --name echelon-ingress -p 8082:8080 \
  -e Queue__Host=rabbitmq -e Queue__Username=... -e Queue__Password=... \
  -e Webhooks__GitLab__main__Token=... \
  -e Webhooks__Tracker__main__Token=... \
  frigat/echelon-ingress:latest
```

`main` in those two variables is the connection name you gave the GitLab or tracker connection in the
admin PWA, and the routes carry the same name:

```
POST /webhooks/gitlab/{connectionName}    secret in X-Gitlab-Token
POST /webhooks/tracker/{connectionName}   secret in X-Tracker-Token
GET  /health
GET  /metrics
```

The whole stack, broker included, is a `docker compose up` away with the compose files in the
repository.

## Things worth knowing before you expose it

- **A connection with no configured secret is refused exactly like a wrong secret**: 401, no body. The
  difference between "unknown connection" and "wrong token" is not observable, so neither the
  connection list nor the secret can be probed by the response.
- **A downed broker answers 503, not 500.** Senders treat 500 as permanent and drop the event, so the
  distinction is what makes a redelivery happen instead of a lost webhook.
- **The endpoints are rate limited** because they are internet-facing and guarded by a shared secret,
  which is otherwise brute-forceable as fast as the network allows.
- **Forwarded headers are honoured from any peer**, because naming the real proxies is a deployment
  decision. Until you name them, `X-Forwarded-For` is caller-supplied - see
  [SECURITY.md](https://github.com/jrfrigat/echelon/blob/main/SECURITY.md).
- **Duplicates are expected and handled.** Every event carries a delivery identity, and the
  application's ingestion inbox drops a replay rather than acting on it twice.

## Configuration

Every setting and environment variable is documented in
[docs/en/configuration.md](https://github.com/jrfrigat/echelon/blob/main/docs/en/configuration.md).

## Links

- Source and documentation: https://github.com/jrfrigat/echelon
- Application image: https://hub.docker.com/r/frigat/echelon
- License: MIT
