# ADR 07: Component orchestration

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

The earlier decisions add up to a distributed system, even on one laptop:

| Component | Kind | Decided in |
|---|---|---|
| PostgreSQL with pgvector | Container | [ADR 04](04-vector-database.md) |
| Azurite (blob storage emulator) | Container | [ADR 02](02-document-preparation-and-storage.md) |
| Ollama (chat and embedding models) | Native process, so it can use the machine's hardware acceleration | [ADR 01](01-agent-framework-sdk.md) |
| Filing download and upload | Run-to-completion init job | [ADR 02](02-document-preparation-and-storage.md) |
| Ingestion | Azure Functions host | [ADR 05](05-ingestion-and-retrieval-hosting.md) |
| Retrieval and agents | ASP.NET Core API | [ADR 05](05-ingestion-and-retrieval-hosting.md) |
| Agent tools | MCP server | [ADR 06](06-agent-tool-hosting.md) |

Something has to start these in the right order, wire them together and show what they are doing. The question is which tool orchestrates the system for development, and gives it a path to deployment.

The orchestrator must meet these requirements:

- **One command to start everything.** A developer clones the repo and runs one command, not a page of setup steps.
- **Start order and readiness.** The database and storage must be ready before the hosts that use them start, and the init job must run to completion once per start.
- **Wiring without hand-copied settings.** Connection strings, endpoints and shared settings (such as the embedding model, which both ingestion and retrieval must agree on) are passed to each component by the orchestrator, not copied into each project's config.
- **Secrets stay out of the repo.** The SEC `User-Agent` holds a name and an email, and must never be committed.
- **Mixed resource types.** Containers, .NET projects, an Azure Functions host and an external native process all have to fit one model.
- **A fast inner loop.** Code changes should run and debug as ordinary .NET processes, with no container image rebuild per change.
- **Observability across processes.** One request crosses the API, the MCP server, the database and the model. Its logs, traces and metrics should be visible in one place, locally.
- **A path to production.** The same description of the system should help produce deployment artifacts, so local and deployed wiring don't drift apart.

## Considered options

| Option | Why it is rejected |
|---|---|
| Scripts and several terminals (`dotnet run` per project, `docker run` per container) | No dependency ordering, no shared configuration and no combined telemetry. Every developer's setup drifts |
| Docker Compose | Good at containers, but every .NET project would need an image rebuilt on each change, which slows the inner loop. `depends_on` handles ordering, but there is no telemetry dashboard, and running the Functions host in a container adds work |
| A local Kubernetes cluster (kind, minikube or Docker Desktop's) with manifests or Helm, optionally with Tilt | Closest to some production environments, but far heavier to set up and run on a laptop: cluster setup, image builds per change, manifests, ingress and secrets. Too much machinery for a developer's inner loop on this project |
| Project Tye | Microsoft's earlier experiment in the same space, now archived. Aspire is its successor |
| **Aspire** | **Chosen** |

## Decision

Orchestrate the system with **Aspire**. A C# **AppHost** project describes every component and its relationships, and the Aspire CLI runs it:

| Need | How Aspire meets it |
|---|---|
| One command | `aspire run` starts the AppHost, which starts every resource and opens the dashboard |
| Containers | Hosting integrations run PostgreSQL (with the pgvector image) and the Azure Storage emulator as containers, with data volumes |
| .NET projects | The API, the MCP server and the init job run as ordinary .NET processes, so they build, run and debug without images |
| Azure Functions | The Functions hosting integration runs the ingestion host locally |
| External processes | Ollama is referenced through a connection string, so the AppHost wires it without running it |
| Start order | `WaitFor` makes a resource wait until its dependencies are healthy. Nothing waits for the init job, because indexing is requested separately |
| Wiring | `WithReference` passes connection strings and endpoints to each component, and service discovery resolves names such as `http://mcp` |
| Shared settings and secrets | AppHost parameters are set once and passed to every component that needs them. Secret parameters live in .NET user secrets, outside the repo |
| Operations | Custom resource commands, such as a "Re-index filings" button in the dashboard |
| Observability | Every project uses shared service defaults (OpenTelemetry, health checks, service discovery and resilience), and the dashboard shows logs, traces and metrics across all components |
| Testing | `Aspire.Hosting.Testing` starts the whole AppHost in an end-to-end test |
| Deployment | The same application model can produce deployment artifacts, for example for Docker Compose, Kubernetes or Azure Container Apps |

Aspire is chosen because it is much simpler to set up than a local Kubernetes cluster. It needs only the .NET SDK, Docker and the Aspire CLI, and its whole configuration is one C# project in the same solution as the code it runs.

## Consequences

- **Onboarding is one command.** After installing the prerequisites and setting one user secret, a developer runs `aspire run` and gets the whole system and its dashboard.
- **The system's topology is code.** The AppHost is the single place that lists every component, its dependencies and its configuration, so it is reviewed and versioned like any other code.
- **The inner loop stays fast.** .NET components run as local processes, and only infrastructure runs in containers.
- **Cross-process debugging is built in.** One trace shows a question's path through the API, the MCP server, the database and the model.
- **Configuration has one source.** Shared settings such as model names are parameters in the AppHost, which keeps ingestion and retrieval on the same embedding model.
- **The AppHost is more code to maintain.** It grows with every component, and custom commands or wiring helpers live there.
- **Versions must line up.** The Aspire CLI, the AppHost SDK and the hosting packages move together, so upgrades are deliberate and done as one change.
- **Docker is required.** The containers need Docker Desktop or another container runtime on every developer machine.
- **Production deployment is a separate decision.** Aspire can generate artifacts for several targets, but choosing the target, its networking and its security is still its own decision.
- **We revisit this decision** if production standardizes on Kubernetes and the local environment needs closer parity with it than Aspire's generated artifacts provide.

## References

- [What is Aspire?](https://aspire.dev/get-started/what-is-aspire/): the orchestrator, its integrations and its tooling
- [The AppHost](https://aspire.dev/get-started/app-host/): describing resources, `WithReference` and `WaitFor`
- [External parameters](https://aspire.dev/fundamentals/external-parameters/): shared settings and secret parameters
- [Service defaults](https://aspire.dev/get-started/csharp-service-defaults/): OpenTelemetry, health checks, service discovery and resilience for every project
- [Aspire dashboard](https://aspire.dev/dashboard/overview/): local logs, traces and metrics across components
- [PostgreSQL hosting integration](https://aspire.dev/integrations/databases/postgres/postgres-host/), [Azure Blob Storage integration](https://aspire.dev/integrations/cloud/azure/azure-storage-blobs/azure-storage-blobs-get-started/) and [Azure Functions integration](https://aspire.dev/integrations/cloud/azure/azure-functions/azure-functions-get-started/): the hosting integrations for the system's components
- [Testing with Aspire](https://aspire.dev/testing/overview/): end-to-end tests that start the AppHost
- [How Aspire deployment works](https://aspire.dev/deployment/deploy-with-aspire/): generating deployment artifacts from the application model
- [Docker Compose](https://docs.docker.com/compose/), [kind](https://kind.sigs.k8s.io/), [minikube](https://minikube.sigs.k8s.io/docs/) and [Tilt](https://tilt.dev/): the rejected alternatives
- [Project Tye](https://github.com/dotnet/tye): the archived predecessor
