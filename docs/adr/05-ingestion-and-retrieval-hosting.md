# ADR 05: Ingestion and retrieval hosting

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

The system has two workloads that touch the same index:

- **Ingestion** reads staged filings, chunks them, embeds the chunks and writes them to the vector database ([ADR 03](03-ingestion-pipeline-library.md), [ADR 04](04-vector-database.md)).
- **Retrieval** embeds a user's question, searches the index and hands the best chunks to the agents ([ADR 01](01-agent-framework-sdk.md)), which stream an answer back.

Microsoft's RAG guidance treats these as separate phases: preparation and indexing come before inference. The question is whether they should also be separate processes, and if so, which host each one runs in.

The two workloads optimize for different things:

- **Ingestion is an ETL pipeline.** It extracts filings, transforms them into clean, embedded chunks and loads them into the index. Nobody waits on it, so how long a run takes matters much less than what it costs and how accurate the resulting index is. A run that takes an hour on cheap capacity and produces better chunks beats a fast, expensive one.
- **Retrieval is latency-sensitive.** A user is waiting for an answer. Every millisecond spent embedding the question, searching and building the prompt delays the first token they see.

The rest of their behavior follows from that:

| | Ingestion | Retrieval |
|---|---|---|
| **When it runs** | Rarely and in bursts: when new filings arrive, about once a year per company, or when the pipeline changes | Continuously, whenever a user asks a question |
| **Shape of the work** | A batch of long-running jobs: minutes per filing on CPU, mostly HTML processing and embedding calls | Many short, interactive requests: one embedding and one search per question, plus the model's answer |
| **Optimizes for** | Cost and accuracy of the index. Total run time is secondary | Latency: time to the first token of the answer |
| **Someone waiting?** | No. It runs in the background | Yes. The user is waiting for the answer |
| **Scales with** | The number of documents to index | The number of concurrent users |
| **Idle cost** | Should be zero between runs | Should always be ready |
| **Failure impact** | A failed run can be retried. The previous index keeps serving | A failure is visible to a user immediately |

The hosting design must meet these requirements:

- **Each workload is tuned for its own goal.** Ingestion is tuned for cost and accuracy, and retrieval for latency. Neither should inherit the other's trade-offs.
- **Each workload scales on its own.** Indexing a large batch must not slow down users' answers, and a spike in users must not require more ingestion capacity.
- **Each workload deploys on its own.** Changing the chunking strategy must not redeploy or restart the chat API, which would drop users' streams.
- **No idle capacity for ingestion.** Ingestion runs rarely, so it shouldn't need a running server between runs.
- **Explicit, repeatable triggering.** Indexing starts when asked (a script, a dashboard command, later a queue message), not as a side effect of starting the system.
- **Long runs are supported.** A full re-index takes minutes, so the host must allow long executions.
- **Same code locally and in production.** Both hosts must run on a laptop, with the code unchanged in production.
- **One shared index contract.** Both sides must agree on the chunk schema and the embedding model, or searches silently return nothing useful.

## Considered options

### Process boundary

| Option | Why it is rejected |
|---|---|
| One process: the query API also runs indexing inline, on an endpoint | A re-index competes with users' requests for CPU, memory and outbound model calls. The two workloads have to scale and deploy together |
| One process: indexing as a background service (`BackgroundService`) inside the query API | The same coupling, with ingestion work hidden inside the API. Scaling the API out for users also multiplies indexing workers that then contend for the same documents |
| **Separate processes, sharing libraries and the index contract** | **Chosen** |

### Ingestion host

| Option | Why it is rejected |
|---|---|
| A long-running worker service | Runs all year to do work a few times a year. We would write our own triggering, queuing and retry handling |
| A console app run as a container job (for example a Container Apps job) | A reasonable fit for batch work. But trigger handling, retries and local invocation become our plumbing, where Functions provides them as bindings |
| **Azure Functions, isolated worker model** | **Chosen** |

### Retrieval host

| Option | Why it is rejected |
|---|---|
| Azure Functions | An answer streams for longer than an HTTP-triggered function may take to respond on Azure, and cold starts add latency to interactive requests. Agent Framework's AG-UI hosting targets ASP.NET Core |
| **ASP.NET Core** | **Chosen** |

## Decision

Run ingestion and retrieval as **separate processes**:

| | Ingestion | Retrieval |
|---|---|---|
| **Host** | Azure Functions (.NET isolated worker) | ASP.NET Core minimal API |
| **Trigger** | An HTTP trigger to start, so a script or a developer command can request a run. A queue trigger later, for asynchronous production runs | HTTP: the agents' AG-UI endpoints and a search endpoint |
| **Scaling** | Scales to zero between runs; scales out with queued work | Always running; scales out with concurrent users |

Keep the real logic out of the hosts:

- **Host-agnostic libraries.** The ingestion pipeline and the retrieval code live in class libraries. Each host is a thin adapter that wires configuration and exposes a trigger. The same libraries can later run from a CLI, a test or another host.
- **One shared contract.** The chunk record type, its vector store schema and the embedding-model settings live in a shared library that both sides reference. Both hosts read the embedding model from the same configuration setting, set in one place.
- **The vector database is the only meeting point.** The two processes never call each other. Ingestion writes the index, and retrieval reads it.

## Consequences

- **Each side scales for its own load.** A re-index can't slow down answers, and adding users never adds ingestion capacity.
- **Each side tunes for its own goal.** Ingestion can run on the cheapest capacity, process filings one at a time and retry freely, because only cost and accuracy count. It can also afford expensive, accuracy-improving steps, such as LLM enrichment of chunks, that would be unacceptable on a user's request. Retrieval keeps the work per question to a minimum and stays warm. Any step added to the query path, such as reranking or query rewriting, has to earn its latency.
- **Each side deploys for its own changes.** A new chunker or reader redeploys only the Functions app. Prompt and agent changes redeploy only the API.
- **Ingestion costs nothing when idle.** Consumption-style Functions plans scale to zero between runs.
- **Search is empty until the first indexing run.** Retrieval doesn't wait for ingestion, so a fresh environment answers with no sources until someone requests indexing.
- **The index contract can drift.** If the two sides disagree on the embedding model or the schema, searches degrade without an error. The shared library and a single source for the embedding-model setting guard against that, and the contract needs tests.
- **HTTP-triggered runs have a response limit in Azure.** An HTTP-triggered function must respond within 230 seconds, whatever the function timeout setting. Long production runs therefore need an asynchronous trigger, a queue message that the function processes after the request has been accepted. Locally there is no such limit.
- **Runs must be idempotent.** A retried or duplicated run must leave the same index. The writer replaces a document's chunks, and unchanged documents are skipped.
- **There is more to operate.** Two deployables, two sets of configuration and two health checks, instead of one. Locally, something has to start both hosts and their dependencies in the right order, and both need the same telemetry setup.
- **We revisit this decision** if ingestion becomes continuous, for example streaming new filings as they are published, in which case a long-running worker may fit better than Functions.

## References

- [RAG solution design and evaluation guide](https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide): preparation and indexing as phases separate from inference
- [Azure Functions hosting options](https://learn.microsoft.com/azure/azure-functions/functions-scale): timeouts per plan, the 230-second limit for HTTP-triggered responses, scale to zero and cold starts
- [Flex Consumption plan](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan): scale to zero and per-function scaling
- [.NET isolated worker model](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide): running Functions as a .NET process with standard dependency injection and configuration
- [Queue storage trigger](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue-trigger): the asynchronous trigger for production indexing, with retries
- [Durable Functions](https://learn.microsoft.com/azure/durable-task/durable-functions/durable-functions-overview): the async HTTP pattern for work that outlasts an HTTP response
- [Run Azure Functions locally](https://learn.microsoft.com/azure/azure-functions/functions-run-local): Core Tools, for running the Functions host on a laptop
- [Azure Container Apps jobs](https://learn.microsoft.com/azure/container-apps/jobs): the rejected container-job alternative and its trigger types
- [Worker services in .NET](https://learn.microsoft.com/dotnet/core/extensions/workers): the rejected long-running worker alternative
