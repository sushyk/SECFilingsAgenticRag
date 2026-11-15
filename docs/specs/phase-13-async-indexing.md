# Phase 13: Asynchronous (queue-based) indexing

> Depends on: Phases 00–07 | Est. size: M

This phase spec is self-contained: the rules, conventions and shared contracts you need are below. Notes go to `docs/notes/phase-13-notes.md`.

## Rules for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-13-notes.md`, under a `## Commands & operating notes` heading.
  - Later phases read all the notes files.
- **Microsoft Learn is the source of truth for facts.**
  - **What it covers:** how Microsoft SDKs and APIs behave (Microsoft.Extensions.AI, VectorData, DataIngestion, Agent Framework, Aspire, Azure Functions, Azure SDKs), plus RAG concepts, terminology and phase order.
  - **What it overrides:** this spec, `docs/02`, blog posts and your own prior knowledge. "Microsoft Learn" means the current docs on learn.microsoft.com, including the Azure Architecture Center RAG guide: https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide
  - **On a conflict:** follow Learn, keep the spec's intent, and record the conflict (spec text → Learn URL → what you did) in the notes evidence table (§11.0).
  - **What stays as specified:** project-specific choices (names, contracts, config keys, resource names), unless Learn shows they're technically impossible.
  - **Non-Microsoft technology** (pgvector, SEC EDGAR, PdfPig): use that project's official docs and mark them *external*.
- **Built-in first.** Use the SDK abstractions directly. Do not add a custom interface, wrapper, base class or framework when a built-in type covers the need. Every custom type you add must say, in a one-line code comment *and* in the notes, which built-in it couldn't use and why. The full audit is the "Custom types and why no built-in fits" table in docs/02. "Built-in" includes:
  - **Microsoft.Extensions.AI:** `IChatClient`, `IEmbeddingGenerator` and their middleware (`UseFunctionInvocation`, `UseDistributedCache`, `UseOpenTelemetry`, `UseLogging`, `ConfigureOptions`, `Use(...)`), plus structured output via `GetResponseAsync<T>`.
  - **Microsoft.Extensions.VectorData:** `VectorStoreCollection`, `IVectorSearchable<T>`, `IKeywordHybridSearchable<T>`, `VectorSearchOptions<T>.Filter`, `GetAsync(filter, top)`.
  - **Microsoft.Extensions.DataIngestion:** readers, chunkers, enrichers, `IngestionPipeline`, `VectorStoreWriter`.
  - **Microsoft.ML.Tokenizers.**
  - **Microsoft Agent Framework:** agents, middleware, context providers, hosting, `AgentSessionStore`, AG-UI.
  - **Aspire and ASP.NET Core built-ins:** health checks, rate limiting, ProblemDetails.
- **Microsoft Learn evidence log (required).** This phase's notes file must include a `## Microsoft Learn sources consulted` table with columns URL | what I checked | decision in code. List every Learn page or code sample used, any place where the docs disagreed with reality, and any external sources marked *external*. See `docs/02-microsoft-guidance-alignment.md`.

- **Authority.** This spec and `docs/01-architecture-and-design.md` are authoritative. If the code on disk conflicts, **the code on disk wins**; record the deviation in `docs/notes/phase-13-notes.md`.
- **Read first:** all `docs/notes/phase-*-notes.md` and `CLAUDE.md`.
- **Verify APIs** with the Microsoft Learn MCP tools (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`) when available, else with package docs and compiler errors.
- **Central package management** (`Directory.Packages.props`): latest stable version, or preview only where nothing stable exists. Check with `dotnet package search <id> --exact-match --prerelease`.
- **Environment.** Windows + PowerShell.
- **Style.** Match the existing code style.
- **Configuration-only models.** No model names in C#; defaults in `appsettings.json`; environment-variable overrides.
- **Contracts.** Don't break earlier public contracts.
- **Process.** Don't `git commit`. Ask nothing; decide, and document.
- **Done means** a clean `dotnet build` (0 errors, 0 warnings), the phase's acceptance checks, a notes file, and a suggested commit message. Automated tests for this phase are specified in [Phase 09](phase-09-tests.md).

### Cross-cutting conventions (from the spec review)

- **Shell:** PowerShell **7.3+** (`pwsh`; check `$PSVersionTable.PSVersion`). Recursive file searches use `Get-ChildItem -Recurse`, never `**` in `Select-String -Path` (it isn't recursive in PowerShell and silently passes). Web calls that send a custom `User-Agent` add `-SkipHeaderValidation`.
- **Scaffolding under central package management (CPM):** after every `dotnet new …`:
  - remove the `Version="…"` attributes from the generated `<PackageReference>`s and put the versions in `Directory.Packages.props` (otherwise you get error NU1008);
  - delete the generated `<TargetFramework>`, which comes from `Directory.Build.props`.

  `global.json` pins the SDK. All `Aspire.*` packages **and** the `Aspire.AppHost.Sdk` version stay aligned on the same 13.6.x version. `CommunityToolkit.VectorData.PgVector` is **stable** (1.0.x); `Microsoft.Extensions.DataIngestion.*` is preview.
- **Running the system headlessly:**
  1. `aspire start` runs the AppHost in the background.
  2. `aspire wait <resource>` waits for a resource (e.g. `aspire wait api`).
  3. `aspire describe` and `aspire logs <resource>` inspect state and output.
  4. `aspire resource <resource> <command>` runs a resource command.
  5. `aspire stop` shuts everything down.

  `aspire run` blocks the terminal, and the dashboard is only for **optional** human checks. If a subcommand's flags differ from what's shown, run `aspire <cmd> --help` and adapt.
- **Docker:** find containers by **name**, not image. Leftover containers from other runs can share the image.
  ```powershell
  $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
  ```
- **JSON on the wire:** use camelCase with enums as strings (`JsonStringEnumConverter`). AgentServer sets this in `ConfigureHttpJsonOptions`. **Every client** (AgentClient.Cli, evals, MCP, UI) deserializes with matching `JsonSerializerOptions`.
- **Table name for raw SQL:** always resolve it with `FilingChunkSchema.CollectionName(vectorStoreOptions, embeddingOptions)` from `SECFilingsAgenticRag.Common.VectorStore`. It is ≤ 63 chars (the Postgres identifier limit), and `AI:Embedding:Dimensions` ≤ 2000 (the HNSW limit). Never hard-code it. Examples show the resolved default `filing_chunks_nomic_embed_text_768`.
- **Long HTTP calls** (LLM, indexing, chat streaming):
  - retries × attempt timeout ≤ total request timeout;
  - circuit-breaker `SamplingDuration` ≥ 2 × `AttemptTimeout`, or options validation throws;
  - for streamed responses the attempt timeout covers only the response **headers**, and the body is bounded by the request `CancellationToken`.
- **Indexing state may be absent or busy.** Code that reads blob or indexing state must treat a missing `filings` container as "no filings" (empty), and HTTP 409 from `POST /api/index` as "indexing already running", not an error. Indexing DTOs (`IndexingRequest`, `IndexingReport`, `DocumentIndexingResult`, `DocumentIndexingStatus`) live in **`SECFilingsAgenticRag.Common.Ingestion`**, with `IndexingJson.Options`. `IIndexingService` stays in `SECFilingsAgenticRag.IngestionPipeline`.
- **AppHost model parameters** are injected with one helper (from Phase 00):
  ```csharp
  public static IResourceBuilder<T> WithAIModels<T>(this IResourceBuilder<T> builder,
      IResourceBuilder<ParameterResource> chatModel,
      IResourceBuilder<ParameterResource> embeddingModel,
      IResourceBuilder<ParameterResource> embeddingDimensions) where T : IResourceWithEnvironment;
  ```

### Shared contracts referenced below (copied from docs/02; the code on disk wins)

```csharp
// SECFilingsAgenticRag.Common.Filings
public sealed record FilingMetadata(string Ticker, string CompanyName, string Cik, string FormType, string AccessionNumber,
    DateOnly FilingDate, DateOnly ReportDate, int FiscalYear, string PrimaryDocument, string SourceUrl)
{ public string DocumentId => $"{Ticker}/{FormType}/{AccessionNumber}"; }
// FilingBlobConventions.BlobName(m, ".htm") => "AAPL/10-K/0000320193-25-000079.htm"; blob metadata keys: ticker, companyname, cik,
// formtype, accession, filingdate, reportdate, fiscalyear, primarydocument, sourceurl, contentsha256

// SECFilingsAgenticRag.Common.Ingestion: indexing DTOs live in Common, so .NET clients (later phases, Phase 09 E2E tests) don't reference SECFilingsAgenticRag.IngestionPipeline.
public sealed record IndexingRequest(IReadOnlyList<string>? Tickers = null, bool Force = false);
public enum DocumentIndexingStatus { Indexed, Skipped, Failed }
public sealed record DocumentIndexingResult(string DocumentId, DocumentIndexingStatus Status, int ChunkCount, TimeSpan Duration, string? Error);  // per-document statistics are structured logs, not report fields
public sealed record IndexingReport(DateTimeOffset StartedAt, TimeSpan Duration, IReadOnlyList<DocumentIndexingResult> Documents);
public static class IndexingJson { public static JsonSerializerOptions Options { get; } } // camelCase + JsonStringEnumConverter; use for ALL (de)serialization of these

// SECFilingsAgenticRag.IngestionPipeline
// Indexing is MEDI's IngestionPipeline<string> over STAGED FILES (blobs downloaded to <staging>/<TICKER>/<FORM>/<accession>.<ext>).
// Readers are KEYED IngestionDocumentReader services resolved by media type: services.AddKeyedSingleton<IngestionDocumentReader, HtmlFilingReader>("text/html");
// A reader must create the IngestionDocument with Identifier = DocumentId (path relative to the staging root), never a temp path.
public interface IIndexingService { Task<IndexingReport> IndexAsync(IndexingRequest request, CancellationToken ct = default); }
// POST /api/index returns 409 while another run is in progress; a missing "filings" container means "no filings" (empty report).

// SECFilingsAgenticRag.Common.VectorStore: FilingChunkSchema.CollectionName(VectorStoreOptions, EmbeddingOptions) is the table name for raw SQL
// (≤ 63 chars; default "filing_chunks_nomic_embed_text_768").

// SECFilingsAgenticRag.Common.AI: EmbeddingKeys.Document / .Query — KEYED IEmbeddingGenerator<string, Embedding<float>> ("search_document: " / "search_query: ");
// AddSecFilingsVectorStore(services, configuration, embeddingKey) — query-side hosts pass EmbeddingKeys.Query.

// SECFilingsAgenticRag.RetrievalPipeline — the query-phase contract is BUILT IN: Microsoft.Extensions.VectorData.IVectorSearchable<FilingChunkRecord>
//   (unkeyed = composed strategy: Vector | Hybrid (+IKeywordHybridSearchable) [+MultiQuery] [+Rerank]; Score higher is better).
public static class FilingSearch     // THE one search function (TextSearchProvider delegate, search_filings tool, /api/search, evals)
{ public static Task<IReadOnlyList<VectorSearchResult<FilingChunkRecord>>> SearchFilingsAsync(this IVectorSearchable<FilingChunkRecord> search,
      RetrievalOptions options, string query, IReadOnlyCollection<string>? tickers = null, IReadOnlyCollection<string>? formTypes = null,
      IReadOnlyCollection<int>? fiscalYears = null, int? top = null, CancellationToken ct = default); }
public static class FilingFilters  { /* Build(tickers, formTypes, fiscalYears, excludeInstructionLike) → Expression<Func<FilingChunkRecord,bool>>? */ }
public static class FilingCitation { public static string Title(FilingChunkRecord record); }

// SECFilingsAgenticRag.AgentServer — named agents via MAF hosting (AddSecFilingsAgents): AgentNames.ChatOnly "chat-only", StandardRag "standard-rag",
//   AgenticRag "agentic-rag"; sessions via AgentSessionStore (PostgresAgentSessionStore from Phase 07); guardrails = agent-run middleware.
// FilingTools: SearchFilingsAsync / ListAvailableFilingsAsync (model-facing text with cite tags), ListFilingsAsync → FilingSummary,
//   AsTools() → AIFunctionFactory tools "search_filings", "list_available_filings".
public sealed record FilingSummary(string Ticker, string CompanyName, string FormType, int FiscalYear, string DocumentId, int ChunkCount);

// SECFilingsAgenticRag.AgentServer — HTTP surface:
//   POST /agents/{chat-only|standard-rag|agentic-rag}   AG-UI protocol (RunAgentInput with threadId → AG-UI SSE events), MapAGUI
//   POST /api/search  SearchRequest → SearchResponse { query, strategy, hits[], elapsed }      GET /api/filings → FilingSummary[]
```

**Aspire resources:** `postgres` → `secfilings`, `storage` → `filings`, `ollama` (connection string), `indexing` (Function, `POST /api/index`), `ingestion-init` (one-shot), `api` (port 5180). Common registration helpers: `AddSecFilingsAI`, `AddSecFilingsVectorStore(…, embeddingKey)`, `AddSecFilingsRetrieval`, `AddSecFilingsIndexing` (each on `IServiceCollection`), and `AddSecFilingsAgents` (on `IHostApplicationBuilder`). Hosts register `NpgsqlDataSource` (with `UseVector()`) and `BlobContainerClient` themselves.

---

## Phase specification


**Goal.** Replace the synchronous, long-running `POST /api/index` call with a production-style async job: **202 Accepted** plus a status endpoint, with work processed from a queue.

**Why.**
- HTTP requests held open for many minutes are fragile: gateway timeouts, client retries, Functions limits.
- Queues give retries, poison handling and scale-out.

**Design.**
- Azurite supports **Queues** (and Tables, in preview). Under Aspire: `var queues = storage.AddQueues("queues");`. Verify the Aspire 13 API names, e.g. `AddQueues` / `AddQueue("indexing-requests")`.
- Job store: a Postgres table `indexing_jobs(id uuid pk, status text, requested_at timestamptz, completed_at timestamptz, request jsonb, report jsonb, error text)`, created idempotently. Postgres is simpler than Tables in Azurite.
- Functions:
  - `POST /api/index`:
    1. Insert a job with status `Queued`.
    2. Enqueue one message per ticker (or one per job) to `indexing-requests` with `{ jobId, tickers, force }`.
    3. Return `202` with `Location: /api/index/jobs/{id}` and the body `{ jobId }`.
  - `[QueueTrigger("indexing-requests", Connection = "queues")]`:
    1. Mark the job `Running`.
    2. Call `IIndexingService.IndexAsync`.
    3. Merge the per-message reports into the job's report.
    4. Mark it `Completed` or `Failed` when all of the job's messages are done (track the expected/finished message counts).
  - `GET /api/index/jobs/{id}` → the job row (status plus `IndexingReport`).
    - Serialize `report jsonb` and the response with `IndexingJson.Options` (from `SECFilingsAgenticRag.Common.Ingestion`): camelCase, enums as strings.
    - Clients must deserialize with the same options.
  - Keep the old synchronous behavior available behind `?wait=true` so the existing `Invoke-Indexing.ps1` flow still works during migration. That path keeps the **409** "already running" response.
  - The queue path accepts concurrent requests: they become separate jobs. Document whether a job for the same tickers is deduplicated.
- `scripts/Invoke-Indexing.ps1`: `POST` → poll `GET /api/index/jobs/{id}` every 5 s until `Completed` or `Failed`, then print the report.
  - A failed job exits non-zero.
  - Each HTTP call is now short; only the overall `-TimeoutSec` bounds the polling.
  - The IngestionInit job is unchanged: it never calls indexing.
- The queue worker must tolerate a missing `filings` container: an empty report with a `Completed` job.
- `host.json`: queue `visibilityTimeout`, `maxDequeueCount` (5), and `batchSize` (1, because embeddings on CPU are heavy). After `maxDequeueCount`, messages move to `indexing-requests-poison`: log, and mark the job `Failed`.
- Alternative (document only, or implement if preferred): **Durable Functions** fan-out/fan-in, one activity per filing. It needs a task hub on Azure Storage (Azurite works). Compare the trade-offs in the notes.

**Acceptance.**
- `POST /api/index` returns 202 in under 1 s.
- The status moves `Queued` → `Running` → `Completed`, with an `IndexingReport` identical in shape to before.
- `Invoke-Indexing.ps1` exits 0 after polling a `Completed` job. Nothing else waits on indexing.
- Killing the Functions host mid-run and restarting it resumes the message (it's redelivered after the visibility timeout). Idempotent indexing means no duplicates.
- The "Re-index" resource command still works, and now returns 202. Run it with `aspire resource indexing reindex` (command name from Phase 02), then `aspire logs indexing`.
- Agent-executable flow:
  1. Get the Function's URL from `aspire describe indexing`.
  2. `Invoke-RestMethod -Method Post "$fn/api/index" -ContentType 'application/json' -Body '{"force":false}'` returns `jobId` in under 1 s.
  3. Poll `Invoke-RestMethod "$fn/api/index/jobs/$jobId"` until `status` is `Completed`. The status comes back as a **string**.

**Gotchas.**
- **Queue messages are Base64-encoded by default** in the Functions storage extension. Configure `messageEncoding` consistently between the producer (the `QueueClient` in the Function) and the trigger.
- **A visibility timeout shorter than the processing time** causes duplicate processing. Process per ticker to keep each message short, and rely on idempotency.
- Azurite queues need the queue to exist: create it on startup with `CreateIfNotExistsAsync`.
- Under Aspire, trigger connections need `WithEnvironment("queues", queues.Resource.ConnectionStringExpression)`, or the binding-specific setting name. `WithReference` alone may not satisfy trigger bindings (see "Azure Functions with Aspire: connection configuration" on Microsoft Learn).
