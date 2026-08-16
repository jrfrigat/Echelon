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

One line saying what changed, in the imperative. Add a body only when the diff does not explain
itself - a constraint that is not visible locally, a decision with an alternative worth naming, a
defect whose symptom differed from its cause. Do not add a body that restates the diff.

Do not add `Co-authored-by` trailers.

## Workflow

1. Fork and branch from `main`.
2. Make the change, with tests. A bug fix needs a test that fails without it - check that it does.
3. Build and test locally; both must be clean.
4. Open a pull request describing what changed and why.

## Reporting bugs and requesting features

Open an issue. For a bug, the useful ones say what you did, what happened, and what you expected;
the version and the database provider matter more often than not.

For security issues, do not open an issue - see [SECURITY.md](SECURITY.md).
