# Contributing to Echelon

Thanks for your interest. This document is short on purpose: the conventions that matter are the
ones the build enforces, and the rest is explained where it applies.

## Prerequisites

- .NET 10 SDK
- Docker (for the compose stack, and for the database if you do not have one locally)
- SQL Server or PostgreSQL, if you want to run against a real database rather than the tests' SQLite

## Build and test

```bash
dotnet build Echelon.slnx
dotnet test Echelon.slnx
```

The build must be **0 errors and 0 warnings**: `TreatWarningsAsErrors` is on, so a warning is a
build failure, locally and in CI.

Before committing, run:

```bash
bash scripts/clean-empty-files.sh
```

An unescaped `>` in a shell command creates an empty file named after the next token. Three such
files have reached commits; this script removes them.

## Conventions

- **Package versions live only in `Directory.Packages.props`.** A `PackageReference` in a `.csproj`
  carries no `Version`.
- **XML documentation on public types and members** where a comment says more than the name does.
  CS1591 is suppressed on purpose: enforcing it wholesale produces "Gets or sets the Id" and teaches
  people that comments are paperwork.
- **Comments explain why, not what.** If a line is surprising, say what would go wrong without it.
- **Dependencies point inwards only.** `Core` knows nothing; `Application` knows no Entity Framework;
  no provider name appears outside its adapter.
- **Entity mapping lives in attributes on the model**, not in `OnModelCreating`. Fluent configuration
  is used only for the two things an attribute cannot express - a filtered index and a value
  conversion - and each says in a comment why it is there.
- **Migrations are written for both providers, always both.** Adding one to MsSql and forgetting
  Postgres breaks half the deployments, and only CI notices:

  ```bash
  dotnet ef migrations add <Name> --project src/Echelon.Migrations.MsSql --context AppDbContext
  dotnet ef migrations add <Name> --project src/Echelon.Migrations.Postgres --context AppDbContext
  ```

- **Use ASCII and Cyrillic only** in source, documentation and commit messages: no em dashes, no
  typographic quotes, no ellipsis characters. Everything should be typeable on an English or Russian
  keyboard.

## Commit messages

**One line, in English, in the imperative, and no body.** Write the subject so it carries the point
on its own:

```
Let the wait policy reach the deploy order, not just the closure
Refuse a launch whose plan has no steps
Understand a task key that is not written in Latin
```

Not `Fix ordering bug`, and not a subject followed by three paragraphs restating the diff. If the
subject cannot hold the point, the commit is usually doing two things.

A body is for the rare case where the reason lives outside the diff entirely - a constraint no
reviewer can see locally, or a defect whose symptom was nowhere near its cause. That is a handful of
commits in a hundred, not most of them.

Do not add `Co-authored-by` trailers.

## Workflow

1. Fork and branch from `main`.
2. Make the change, with tests. A bug fix needs a test that fails without it - check that it does.
3. Build and test locally; both must be clean.
4. Open a pull request describing what changed and why.

## CI and releasing

Two workflows drive the pipeline:

- **CI** (`.github/workflows/ci.yml`) - on every push and pull request to `main`: repository hygiene,
  build and test with warnings as errors, a check that the migrations match the model for both
  providers, and a dependency audit.
- **Release** (`.github/workflows/release.yml`) - on a pushed `v*` tag. It builds, tests, and produces:

  | Artifact | What | Where |
  | :-- | :-- | :-- |
  | **NuGet packages** | `Echelon.Core`, `Echelon.Providers.Abstractions`, `Echelon.Application` | NuGet.org, via Trusted Publishing (OIDC) |
  | **Container images** | the application host and the webhook ingress | `ghcr.io/jrfrigat/echelon[-ingress]` and `frigat/echelon[-ingress]`, tagged `X.Y.Z` and `latest` |
  | **GitHub Release** | the tag's page, with generated notes and the `.nupkg` files attached | `github.com/jrfrigat/echelon/releases` |

  The hosts, the PWA, the migration assemblies and the tests are `IsPackable=false`, so `pack` skips
  them: they ship as images, not as a dependency anyone should take. The version comes from the tag
  through MinVer (`v1.2.3` -> `1.2.3`), which is why the checkout uses `fetch-depth: 0`.

Setup, once per repository: a **Trusted Publisher** policy on nuget.org for this repository and
`release.yml`, plus the `NUGET_USER` variable naming the account; `DOCKERHUB_USERNAME` as a variable
and `DOCKERHUB_TOKEN` as a secret for Docker Hub. GHCR needs nothing beyond `packages: write`.

The Docker Hub overviews in `build/` are a separate matter: **an access token may push an image but
not edit a repository description** (0.1.0 got 403 Forbidden on that step while its pushes succeeded).
The two sync steps therefore stay off until `DOCKERHUB_DESCRIPTION_SYNC` is set to `true` and
`DOCKERHUB_DESCRIPTION_PASSWORD` holds the account password; without them, paste
`build/dockerhub-overview.md` and `build/dockerhub-overview-ingress.md` into Docker Hub by hand.

To cut a release: close the version in both changelogs, then push the commit and the tag together, so
a tag can never point at a commit `main` does not have.

```bash
git push --atomic origin main v0.1.2
```

## Reporting bugs and requesting features

Open an issue. For a bug, the useful ones say what you did, what happened, and what you expected;
the version and the database provider matter more often than not.

For security issues, do not open an issue - see [SECURITY.md](SECURITY.md).
