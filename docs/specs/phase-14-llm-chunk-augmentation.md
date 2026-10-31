# Phase 14: LLM chunk augmentation (the deferred half of chunk enrichment)

> Depends on: Phases 00–07 (Phase 02 enrichment, Phase 06 evals) | Est. size: M

This phase spec is self-contained: the rules, conventions and shared contracts you need are below. Notes go to `docs/notes/phase-14-notes.md`.

## Rules for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-14-notes.md`, under a `## Commands & operating notes` heading.
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

- **Authority.** This spec and `docs/01-architecture-and-design.md` are authoritative. If the code on disk conflicts, **the code on disk wins**; record the deviation in `docs/notes/phase-14-notes.md`.
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


**Goal.** Add LLM-generated metadata (**summary**, **keywords**, **questions this chunk answers**) to chunks. This is the *augment* half of the enrichment phase in Microsoft's RAG guide: https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-enrichment-phase. Phase 02 shipped only the *clean* half.

**Why it was deferred, and why to do it now.**
- The guide's section *"Calculate the cost of augmenting"* says to multiply the per-chunk cost by the number of chunks.
- Here that's about 1,500 chunks × 10–20 s per local CPU LLM call, roughly **4–8 hours per full index**. So Phase 02 shipped cleaning first.
- This item adds augmentation **with budget controls**, so it's affordable. Then **measure** whether it helps, using the Phase 06 evals, as the guide's "evaluate each step" advice says.

**Design.**
- **Use the SDK's built-in enrichers** from `Microsoft.Extensions.DataIngestion`. Both are `IngestionChunkProcessor<string>` implementations backed by an `IChatClient`:
  - **`KeywordEnricher`** ([Learn](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion.keywordenricher)). It "adds 'keywords' metadata to each chunk. It's an array of strings."
    - Constructor: `KeywordEnricher(EnricherOptions options, ReadOnlySpan<string> predefinedKeywords, int? maxKeywords = 5, double? confidenceThreshold = 0.7)`.
    - Use it **as-is**. The metadata key comes from the enricher itself (read its `MetadataKey`), so it lands in a `string[]` column.
    - **Recommended:** pass `predefinedKeywords` = a fixed **10-K risk-topic taxonomy**, e.g. `supply chain, cybersecurity, competition, regulation, litigation, interest rates, foreign exchange, AI, climate, labor, liquidity, tax`. Learn notes that with no predefined keywords the results vary more between models. A fixed taxonomy gives consistent, filterable tags.
  - **`SummaryEnricher`**, which writes a `summary` string.
  - Verify the type names, `EnricherOptions`, the constructor overloads and **the metadata keys they write** on Learn ("Data ingestion building blocks → Chunk processing and enrichment"), and record them in the evidence table.
- **`ClassificationEnricher` was considered and rejected:** it assigns **one** label per chunk from a predefined set. A 10-K risk paragraph usually spans several topics, so the multi-valued `KeywordEnricher` with a predefined taxonomy fits better. Record this in the notes. `SentimentEnricher` adds nothing for retrieval.
- Write **one tiny** custom `QuestionsEnricher : IngestionChunkProcessor<string>`. It's justified because MEDI has no questions enricher. It produces 3 questions per chunk as a `string[]` under the metadata key `questions`.
  - Use MEAI **structured output**: `await chat.GetResponseAsync<QuestionList>(prompt, new ChatOptions { Temperature = 0 }, cancellationToken: ct)`, with `public sealed record QuestionList(string[] Questions);`. There's no hand-written JSON parsing or `<think>` stripping.
  - Small-model fallback: retry once with `useJsonSchemaResponseFormat: false`. On any failure, return an empty array and count it.
- **Order:** enrichers (DI, registration order) → Phase 02 `ChunkCleaningProcessor` → `FilingMetadataProcessor`.
- A **separate enrichment chat client**: a keyed `IChatClient` `"enrichment"` (`services.AddKeyedChatClient("enrichment", …)`), configured from `Indexing:Enrichment:Chat:{Provider, Model, Endpoint}`. It falls back to `AI:Chat` when unset; model names come from configuration only. A small local model (e.g. `qwen2.5:1.5b`) keeps it cheap, and an OpenAI-compatible cloud model makes it fast. Its pipeline uses only built-in middleware:
  ```csharp
  services.AddKeyedChatClient("enrichment", sp => providerClient)
      .UseDistributedCache(configure: c => c.CacheKeyAdditionalValues = [model, enrichment.PromptVersion])   // verify the property name
      .Use((msgs, opts, next, ct) => { calls.Add(1); return next(msgs, opts, ct); })                         // counts calls that reach the model
      .UseLogging().UseOpenTelemetry(sourceName: TelemetryNames.AI);
  ```
  - **Caching is built in** (`UseDistributedCache` → `DistributedCachingChatClient`). **No `enrichment_cache` table.**
  - The key is computed from the messages + `ChatOptions`, i.e. the chunk text and the prompt. Add the model and `PromptVersion` as additional key values. **Fallback:** if `CacheKeyAdditionalValues` doesn't exist, set `ChatOptions.ModelId` and put `PromptVersion` in the system prompt, so they're part of the hashed messages and options.
  - Learn: the generated key "is not guaranteed to be stable across releases". A package upgrade may cause one full re-pay; note it.
  - **Store:**
    - **Development (default):** `services.AddDistributedMemoryCache()`. It lives as long as the Functions host process, which covers "second forced run while the stack is up". That's enough for the acceptance check.
    - **Durable (optional, justified only if re-pay after restarts hurts):** an Aspire Redis resource plus `builder.AddRedisDistributedCache("cache")` (Aspire StackExchange.Redis distributed-caching integration; verify the names). It survives restarts, needs no schema, and is still the built-in `IDistributedCache` contract. Don't write a Postgres cache table.
  - `cacheHits` = enricher invocations − `calls` (both counted with a `Meter`). No custom cache wrapper.
- **Budget controls** (`Indexing:Enrichment`). The whole section is part of the Phase 02 index fingerprint:
  - `Enabled` (default false);
  - `Sections` (heading-prefix allow-list, e.g. `["Item 1.", "Item 1A.", "Item 7."]`); other chunks skip enrichment;
  - `MaxChunksPerFiling`;
  - `MaxConcurrency` (default 1 on CPU);
  - `PromptVersion`.

  One custom `BudgetedEnrichmentProcessor` (justified: MEDI processors have no per-chunk section filter or budget) decides per chunk whether to call the inner enricher, or to emit the empty defaults.
- **Cost estimate first.** Before enriching, log `eligible chunks × measured average latency` from the first 5 calls as an ETA. Log structured counts: `calls`, `cacheHits`, `skippedByBudget`.
- **Schema:**
  - MEDI's `VectorStoreWriter<string>` (Phase 02) turns **every `chunk.Metadata` entry into a column** automatically:
    - `keywords`: `string[]`, from `KeywordEnricher`;
    - `summary`: `string`;
    - `questions`: `string[]`.

    Learn's Postgres connector page lists **string enumerables** among the supported data property types, so arrays are fine.
  - The writer derives the schema from the **first chunk** and calls `value.GetType()`, so every key must be present and **non-null on every chunk**, including chunks in non-enriched sections. `FilingMetadataProcessor` fills the defaults: `""` for `summary`, and an **empty array** (never null) for `keywords` and `questions`.
  - Extend `FilingChunkMetadataKeys`, `FilingMetadataProcessor` and the typed `FilingChunkRecord` (`Summary` string, `Keywords` and `Questions` as `string[]`, with lower-case `StorageName`s). Re-run Phase 02's check 3b (`\d+`) on the new collection: the three new columns appear under those names.
  - The column set changes, so **re-index into a new collection** by changing `VectorStore:CollectionPrefix` (e.g. `filing_chunks_v2`). Never alter the live table in place.
- **Use the fields:**
  - the Phase 06 hybrid full-text vector covers `content || ' ' || summary || ' ' || array_to_string(keywords, ' ')` (column names resolved by `FilingChunkTableInfo`);
  - `search_filings` tool results include `Summary`;
  - optional: a second vector `QuestionsEmbedding`, from the joined questions. The PgVector connector supports multiple vectors per record, **but `VectorStoreWriter<string>` writes only one vector (`embedding` from `Content`)**. Populate the second vector with a small follow-up upsert through the typed collection after the writer finishes, or keep this item out of scope. Vector-search it alongside `Embedding` and fuse the results with RRF, as the guide's "questions that the chunk can answer" field suggests.

**Steps.**
1. Verify the MEDI enricher APIs and metadata keys, and log the URLs in the evidence table.
2. Add the options, the keyed enrichment client (with `UseDistributedCache`), the `IDistributedCache` registration and the enrichers, registered before the cleaners.
3. Make the schema and writer mapping changes, plus the new `CollectionPrefix`.
4. Index with `Sections` limited to Item 1A only. Confirm the ETA log line and the cache hits on a second forced run.
5. Extend the Phase 06 eval matrix: `Vector` and `Hybrid` × {base collection, enriched collection}.

**Acceptance.**
- With `Enabled=false`, nothing changes. The `Indexing:Enrichment` section is hashed only once added, so record whether adding the section (still disabled) triggered one re-index. Everything after that is **Skipped**.
- With `Enabled=true` and `Sections=["Item 1A."]`:
  - only Item 1A chunks have a non-empty `Summary` and `Keywords` (SQL check by section);
  - `aspire logs indexing` shows the ETA line and `calls` > 0;
  - a second `./scripts/Invoke-Indexing.ps1 -Force` logs `cacheHits` ≈ the number of calls from the first run;
  - chunks outside Item 1A have `keywords = '{}'` (an empty array, not NULL).
- The eval report contains rows for both collections, so the decision to keep or drop augmentation is **data-driven**. Record it in `docs/02-microsoft-guidance-alignment.md` §5.

**Gotchas.**
- LLM output is non-deterministic. Use temperature 0 and cache aggressively, and keep prompts versioned, since the version is part of the cache key and the fingerprint.
- Small models drift from the JSON format. Structured output (`GetResponseAsync<T>`) plus the `useJsonSchemaResponseFormat: false` fallback handles most of it; count the failures and don't write a parser.
- Summaries can **hallucinate**. They're for retrieval and display, **never cited as source text**: citations always point at `Text`.
- Cost scales with chunk count. Re-check the ETA whenever the corpus grows.
