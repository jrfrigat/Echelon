# Echelon

Echelon plans and runs releases around the unit people actually work in: the **task**.
It reads tasks from an issue tracker and merge requests from one or more VCS connections, works out
everything a task waits on, orders the work into deploy waves, and drives the rollout into each
environment.

This is one of the Echelon libraries. Reference them only if you are writing a provider - a VCS, a
tracker, a deploy strategy or an action handler. Most users just run the container image
(`docker.io/frigat/echelon`).

The package IDs match the namespaces, so `dotnet add package Echelon.Providers.Abstractions` gives
you `using Echelon.Providers.Abstractions`.

Library packages:

| Package | What is in it |
| :-- | :-- |
| `Echelon.Core` | Enums and pure parsing: task-key extraction, label sets, status vocabularies. No dependencies |
| `Echelon.Providers.Abstractions` | The provider ports and the normalized models they exchange |
| `Echelon.Application` | Ports, message contracts and the planning algorithm, with no Entity Framework |

## Writing a provider

A provider adapter references `Providers.Abstractions` and nothing else of ours, so a breaking change
to a port is a compile error in your adapter on the next build rather than a surprise at runtime.
Providers are registered at compile time through keyed services; there is no runtime assembly
scanning, because a container image is rebuilt anyway.

See [docs/en/providers.md](https://github.com/jrfrigat/echelon/blob/main/docs/en/providers.md).

## Links

- Source and documentation: https://github.com/jrfrigat/echelon
- Container image: https://hub.docker.com/r/frigat/echelon
- License: MIT
