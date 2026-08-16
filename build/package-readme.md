# Release Orchestrator

Release Orchestrator plans and runs releases around the unit people actually work in: the **task**.
It reads tasks from an issue tracker and merge requests from one or more VCS connections, works out
everything a task waits on, orders the work into deploy waves, and drives the rollout into each
environment.

This is one of the Release Orchestrator libraries. Reference the `FrigaT.ReleaseOrchestrator.*`
packages only if you are writing a provider - a VCS, a tracker, a deploy strategy or an action
handler. Most users just run the container image (`docker.io/frigat/release-orchestrator`).

The NuGet IDs are prefixed `FrigaT.ReleaseOrchestrator.*`; the assemblies and namespaces stay
`ReleaseOrchestrator.*`, so `dotnet add package FrigaT.ReleaseOrchestrator.Providers.Abstractions`
gives you `using ReleaseOrchestrator.Providers.Abstractions`.

Library packages:

| Package | What is in it |
| :-- | :-- |
| `FrigaT.ReleaseOrchestrator.Core` | Enums and pure parsing: task-key extraction, label sets, status vocabularies. No dependencies |
| `FrigaT.ReleaseOrchestrator.Providers.Abstractions` | The provider ports and the normalized models they exchange |
| `FrigaT.ReleaseOrchestrator.Application` | Ports, message contracts and the planning algorithm, with no Entity Framework |

## Writing a provider

A provider adapter references `Providers.Abstractions` and nothing else of ours, so a breaking change
to a port is a compile error in your adapter on the next build rather than a surprise at runtime.
Providers are registered at compile time through keyed services; there is no runtime assembly
scanning, because a container image is rebuilt anyway.

See [docs/en/providers.md](https://github.com/jrfrigat/release-orchestrator/blob/main/docs/en/providers.md).

## Links

- Source and documentation: https://github.com/jrfrigat/release-orchestrator
- Container image: https://hub.docker.com/r/frigat/release-orchestrator
- License: MIT
