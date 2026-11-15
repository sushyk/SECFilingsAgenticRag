# Phase 03: Retrieval library (the query phase) + Search API

> Depends on: Phase 00 (Common, AppHost), Phase 01 (data + init job), Phase 02 (indexing; chunks exist in pgvector) | Produces: `SECFilingsAgenticRag.RetrievalPipeline` library, `SECFilingsAgenticRag.AgentServer` host with `POST /api/search` | Est. size: M

---

## 0. Instructions for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-NN-notes.md`, under a `## Commands & operating notes` heading.
  - Later phases read all the notes files.
- **Microsoft Learn is the source of truth for facts.** For how Microsoft SDKs and APIs behave (Microsoft.Extensions.AI, VectorData, DataIngestion, Agent Framework, Aspire, Azure Functions, Azure SDKs) and for RAG concepts, terminology and phase order, current Microsoft Learn documentation (learn.microsoft.com, including the Azure Architecture Center RAG guide https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide) overrides this spec, docs/02, blog posts and your own prior knowledge. If this spec contradicts Learn, follow Learn, keep the spec's intent, and record the conflict (spec text → Learn URL → what you did) in the notes evidence table (§11.0). Project-specific choices (names, contracts, config keys, resource names) stay as specified unless Learn shows they're technically impossible. For non-Microsoft technology (pgvector, SEC EDGAR, PdfPig), use that project's official docs and mark them *external*.
- **Built-in first.** Use the SDK abstractions directly. Do not add a custom interface, wrapper, base class or framework when a built-in type covers the need. Every custom type you add must say, in a one-line code comment *and* in the notes, which built-in it couldn't use and why. The full audit is the "Custom types and why no built-in fits" table in docs/02. "Built-in" includes:
  - **Microsoft.Extensions.AI:** `IChatClient`, `IEmbeddingGenerator` and their middleware (`UseFunctionInvocation`, `UseDistributedCache`, `UseOpenTelemetry`, `UseLogging`, `ConfigureOptions`, `Use(...)`), plus structured output via `GetResponseAsync<T>`.
  - **Microsoft.Extensions.VectorData:** `VectorStoreCollection`, `IVectorSearchable<T>`, `IKeywordHybridSearchable<T>`, `VectorSearchOptions<T>.Filter`, `GetAsync(filter, top)`.
  - **Microsoft.Extensions.DataIngestion:** readers, chunkers, enrichers, `IngestionPipeline`, `VectorStoreWriter`.
  - **Microsoft.ML.Tokenizers.**
  - **Microsoft Agent Framework:** agents, middleware, context providers, hosting, `AgentSessionStore`, AG-UI.
  - **Aspire and ASP.NET Core built-ins:** health checks, rate limiting, ProblemDetails.
- This spec and `docs/01-architecture-and-design.md` are authoritative. If code already on disk conflicts with a contract written here, **the code on disk wins**. Adapt to it and record the deviation in `docs/notes/phase-03-notes.md`.
- Before starting, read every `docs/notes/phase-*-notes.md` file from earlier phases (00, 01, 02) and `CLAUDE.md`. They record the real names, versions and deviations.
- Verify preview or unfamiliar APIs before coding against them:
  - Use the Microsoft Learn MCP tools if you have them (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`).
  - Otherwise use the package XML docs, IntelliSense or compiler errors.
  - Verify the exact APIs of `Microsoft.Extensions.VectorData` and `CommunityToolkit.VectorData.PgVector` (stable 1.0.x, but young; filter translation details matter).
- **Central package management:** add package versions only to `Directory.Packages.props`. Use the latest stable version, or the latest preview only where no stable exists. Find it with `dotnet package search <id> --exact-match --prerelease`.
- Use Windows + PowerShell for all commands.
- Match the existing code style: file-scoped namespaces, nullable enabled, `sealed` by default, primary constructors where the existing code uses them.
- Don't change public contracts from earlier phases unless unavoidable. If you must, document it in the notes file.
- **Do NOT `git commit`.** Ask nothing; make reasonable decisions and document them.

### 0.1 Cross-cutting conventions (from the spec review; apply throughout this spec)

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

## 1. Goal and why

So far the system can **index** filings: chunks of 10-K text with their embeddings sit in Postgres/pgvector. This phase builds the other half of RAG, the **query phase**. Given a question, it finds the most relevant chunks.

We build it **on the built-in Microsoft.Extensions.VectorData contract**, `IVectorSearchable<FilingChunkRecord>`, instead of inventing a retriever interface. `VectorStoreCollection<TKey,TRecord>` already implements `IVectorSearchable<TRecord>` (https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/vector-search), so the plain vector strategy needs **no custom class at all**. Every consumer resolves `IVectorSearchable<FilingChunkRecord>`:
- the chat agent (Phase 4–5) calls it in-process, from `TextSearchProvider` or a tool;
- `/api/search` (this phase) exposes it over HTTP so you can inspect retrieval quality by hand;
- the evaluation runner (Phase 6) and a future MCP server call the same interface.

Phase 06 adds hybrid search, reranking and multi-query as classes that **implement the same built-in interface**, using decorators and a strategy selected by configuration. The transport is a detail; the retrieval logic is written once.

We also create the `SECFilingsAgenticRag.AgentServer` host now, with only `POST /api/search`. Before adding an LLM, you can then answer the question *"does search actually find the right text?"* That's the #1 cause of bad RAG answers.

## 2. Concepts in 60 seconds

- **Query embedding.** The question is turned into a 768-number vector with the **same** model used at indexing time (`nomic-embed-text`). The query gets the `"search_query: "` prefix; documents got `"search_document: "`. You pass the question as a `string` to `SearchAsync`; the vector store embeds it with Common's keyed `query` generator (MEVD's built-in auto-embedding).
- **Vector search.** pgvector finds the stored chunk vectors closest to the query vector, using an HNSW index (approximate nearest neighbour). The schema uses `DistanceFunction.CosineSimilarity`, so `VectorSearchResult.Score` is a similarity: higher means more similar.
- **Metadata filtering.** Restrict the search to `Ticker = 'MSFT'` or `FiscalYear = 2025` before ranking. This is cheap precision.
- **Citations.** Every result carries a human-readable source, so answers can be verified.
- **Decorator / Strategy.** Later phases wrap the retriever (rerank, multi-query) or swap it (hybrid) **by configuration only**. That's the Open/Closed principle.

## 3. Environment and prerequisites

- Windows 11, PowerShell 7.3+, **.NET 10 SDK** (`dotnet --list-sdks` shows `10.0.x`).
- **Docker Desktop running** (`docker info` succeeds). Postgres/pgvector and Azurite run as containers.
- **Native Ollama** at `http://localhost:11434` with the models pulled:
  ```powershell
  ollama pull qwen3:4b
  ollama pull nomic-embed-text
  ollama list   # both must appear
  ```
- **Aspire 13.x**: AppHost uses `Aspire.AppHost.Sdk` 13.x. Check with `aspire --version`. If the CLI is missing, `dotnet run --project src/SECFilingsAgenticRag.AppHost` works too.
- Filings downloaded (`data/filings/` populated by Phase 01) and indexed (Phase 02). Check:
  ```powershell
  # with the AppHost running (aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1):
  $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
  # table name = FilingChunkSchema.CollectionName(...); default shown:
  docker exec $pg psql -U postgres -d secfilings -c 'SELECT COUNT(*) FROM "filing_chunks_nomic_embed_text_768";'
  ```
  Expect several hundred to a few thousand rows. The table and column names may differ: check `phase-00-notes.md` and `phase-02-notes.md`.

## 4. Repo state at start

```
SECFilingsAgent/
├─ SECFilingsAgenticRag.slnx
├─ Directory.Build.props / Directory.Packages.props
├─ CLAUDE.md
├─ data/filings/...                         (git-ignored)
├─ src/
│  ├─ SECFilingsAgenticRag.AppHost/                   (postgres+secfilings, storage+filings, ollama conn string, indexing, ingestion-init)
│  ├─ SECFilingsAgenticRag.ServiceDefaults/
│  ├─ SECFilingsAgenticRag.Common/
│  ├─ SECFilingsAgenticRag.IngestionPipeline/
│  ├─ SECFilingsAgenticRag.IngestionPipeline.Functions/
│  └─ SECFilingsAgenticRag.IngestionInit/
```

### 4.1 Contracts you consume (from Phase 00, `SECFilingsAgenticRag.Common`), copied verbatim

```csharp
namespace SECFilingsAgenticRag.Common.VectorStore;

/// One chunk of one filing, as stored in pgvector. Shared by Indexing (writes) and Retrieval (reads).
public sealed class FilingChunkRecord   // mirrors the table MEDI VectorStoreWriter<string> writes (Phase 02); storage names in quotes
{
    public Guid Key { get; set; }                 // "key": random Guid from the writer; NOT stable across re-index (never reference it in golden data)
    public string Text { get; set; } = "";        // "content": the CLEANED chunk (the only text stored): embedded, returned, cited; no prefix
    public string? Context { get; set; }          // "context": MEDI chunk context
    public string DocumentId { get; set; } = "";  // "documentid" (indexed), e.g. "AAPL/10-K/0000320193-25-000079"
    public string Ticker { get; set; } = "";      // "ticker"
    public string CompanyName { get; set; } = ""; // "companyname"
    public string Cik { get; set; } = "";         // "cik"
    public string FormType { get; set; } = "";    // "formtype"
    public int FiscalYear { get; set; }           // "fiscalyear"
    public DateTime FilingDate { get; set; }      // "filingdate" (UTC)
    public string Section { get; set; } = "";     // "section", e.g. "Item 1A. Risk Factors"
    public int ChunkIndex { get; set; }           // "chunkindex"
    public int ChunkCount { get; set; }           // "chunkcount"
    public string IndexFingerprint { get; set; } = ""; // "indexfingerprint" (Phase 02 skip check)
    public bool ContainsInstructionLikeText { get; set; } // "containsinstructionliketext": prompt-injection flag; excluded from retrieval by default
    public string SourceUrl { get; set; } = "";   // "sourceurl": EDGAR URL, used for citations
    public string EmbeddingModel { get; set; } = ""; // "embeddingmodel"
    public ReadOnlyMemory<float>? Embedding { get; set; } // "embedding"
}

/// Builds the schema at runtime so the vector dimension and collection name follow configuration.
public static class FilingChunkSchema
{
    public static VectorStoreCollectionDefinition CreateDefinition(int dimensions);
    // Key "key" | Data with the StorageNames shown on FilingChunkRecord (DocumentId, Ticker, FormType, FiscalYear,
    // ContainsInstructionLikeText IsIndexed) | Vector "embedding": dimensions, CosineSimilarity, Hnsw. Compatible with the writer's table.
    public static string CollectionName(VectorStoreOptions store, EmbeddingOptions embedding);
    // => "filing_chunks_nomic_embed_text_768" (lower-case; non-alphanumerics -> '_')
}

public static class PgVectorSetup { public static Task EnsureExtensionAsync(NpgsqlDataSource dataSource, CancellationToken ct = default); }

public static class VectorStoreServiceCollectionExtensions
{
    /// DI glue. Requires an NpgsqlDataSource (built with UseVector()) to be registered by the host.
    /// Registers a PostgresVectorStore whose EmbeddingGenerator is the keyed generator `embeddingKey`,
    /// and VectorStoreCollection<Guid, FilingChunkRecord> (singleton). AgentServer passes EmbeddingKeys.Query.
    public static IServiceCollection AddSecFilingsVectorStore(this IServiceCollection services, IConfiguration configuration, string embeddingKey);
}
```

```csharp
namespace SECFilingsAgenticRag.Common.AI;

public static class EmbeddingKeys { public const string Document = "document", Query = "query"; }

public static class AIServiceCollectionExtensions
{
    /// Registers IChatClient (UseLogging + UseOpenTelemetry, NO UseFunctionInvocation) and two KEYED
    /// IEmbeddingGenerator<string, Embedding<float>> MEAI pipelines: EmbeddingKeys.Document and EmbeddingKeys.Query.
    /// Each applies the symmetric normalization (AI:Embedding:Normalization), then its task prefix, and validates dimensions.
    /// The Query pipeline adds UseDistributedCache() when AI:Embedding:CacheQueries = true.
    public static IServiceCollection AddSecFilingsAI(this IServiceCollection services, IConfiguration configuration);
}
```

### 4.2 Aspire resource names (canonical)

| Resource | Name | Notes |
|---|---|---|
| Postgres server | `postgres` | image `pgvector/pgvector:pg17` |
| Database | `secfilings` | connection string name used by clients |
| Storage (Azurite) | `storage` | |
| Blob container | `filings` | |
| Ollama | `ollama` | external connection string `Endpoint=http://localhost:11434` |
| Indexing Function | `indexing` | `POST /api/index` |
| IngestionInit | `ingestion-init` | one-shot; exits when done |
| **API (new in this phase)** | `api` | fixed host port **5180** (http) |

### 4.3 Configuration keys already in use, and the configuration rule

`ConnectionStrings:ollama`, `ConnectionStrings:secfilings`, `AI:Chat:*`, `AI:Embedding:*`, `VectorStore:CollectionPrefix=filing_chunks`.

**Rule (docs/02 §4.2): model names and every other deployment-specific value come from configuration, never from code.**
1. **No hard-coded model names, dimensions or endpoints in C#.** `ChatModelOptions.Provider/Model` and `EmbeddingOptions.Provider/Model/Dimensions` are `[Required]` with **no code defaults**, validated with `ValidateOnStart`. A missing setting fails at startup.
2. **Defaults live in each host's `appsettings.json`.** This phase adds the `AI`, `VectorStore` and `Retrieval` sections to `src/SECFilingsAgenticRag.AgentServer/appsettings.json`.
3. **Environment variables override `appsettings.json`.** After deployment, set for example `AI__Chat__Model=qwen3:8b`, `AI__Embedding__Model=nomic-embed-text`, `AI__Embedding__Dimensions=768`, `Retrieval__DefaultTop=8`. The double underscore `__` maps to `:`.
4. **Under Aspire, one source of truth.** The AppHost declares parameters in its own `appsettings.json` (`Parameters:chat-model`, `Parameters:embedding-model`, `Parameters:embedding-dimensions`) and injects them into every service with `WithEnvironment("AI__Chat__Model", chatModel)` and so on. That guarantees the indexing Function and the API **use the identical embedding model**. Earlier phases should already have created these parameters for the `indexing` Function; reuse the same parameter variables for `api`. If they don't exist yet, create them as shown in §6.8 and wire the Function the same way.
5. **Secrets** (`AI:Chat:ApiKey`) never go in `appsettings.json`. Use `dotnet user-secrets` or environment variables.

Similarly, `RetrievalOptions` defaults are written in `appsettings.json` (the C# initializers in §6.2 are convenience fallbacks for non-deployment tuning knobs only; they're not model names).

## 5. Scope

**In**
- `src/SECFilingsAgenticRag.RetrievalPipeline` class library: `RetrievalOptions` with validation, `FilingFilters`, `FilingCitation`, `FilingSearch.SearchFilingsAsync` (the one search function), `AddSecFilingsRetrieval` (registers the built-in `IVectorSearchable<FilingChunkRecord>`). No retriever interface, no strategy class for plain vector search.
- `src/SECFilingsAgenticRag.AgentServer` ASP.NET Core minimal API with `POST /api/search`, health endpoints, OpenAPI (Development), ProblemDetails, fixed port 5180.
- AppHost wiring for `api`.

**Out** (later phases)
- `list_available_filings` / `GET /api/filings` arrive in Phase 05. Don't create them now.
- Hybrid search, reranking, multi-query (Phase 06). Chat endpoints and agents (Phase 04–05). Persistence and hardening (Phase 07).

## 6. Design

### 6.1 The query-phase contract is built in: `IVectorSearchable<FilingChunkRecord>`

**Decision (built-in first; record the Learn URLs in your notes):**
- **The contract.** `IVectorSearchable<TRecord>` (Microsoft.Extensions.VectorData) exposes `SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<TRecord>? options, CancellationToken)` and returns `IAsyncEnumerable<VectorSearchResult<TRecord>>`.
  - `VectorStoreCollection<TKey,TRecord>` implements it.
  - When the store has an `IEmbeddingGenerator` for strings, `searchValue` can be the question **text**: see the `HybridSearchAsync` remarks at https://learn.microsoft.com/dotnet/api/microsoft.extensions.vectordata.ikeywordhybridsearchable-1.hybridsearchasync, and https://learn.microsoft.com/dotnet/ai/vector-stores/vector-search.
- **No custom types.** We don't define `IFilingRetriever`, `RetrievalRequest`, `RetrievalResult` or `RetrievedChunk`. Consumers use `IVectorSearchable<FilingChunkRecord>`, `VectorSearchOptions<FilingChunkRecord>` and `VectorSearchResult<FilingChunkRecord>` directly.
- **Phase 06 extensions implement the same built-in interface:**
  - `RerankingVectorSearch` and `MultiQueryVectorSearch` are decorators;
  - `PostgresHybridSearch` implements `IVectorSearchable<FilingChunkRecord>` **and** `IKeywordHybridSearchable<FilingChunkRecord>`.
  - So this phase's consumers never change.
- **Scores are similarities.** `FilingChunkSchema` uses `DistanceFunction.CosineSimilarity` (Phase 00 and Phase 02 use the same value), so `VectorSearchResult.Score` is a similarity, higher is better. Every Phase 06 implementation must also return higher-is-better scores.
  - Verify with check 4b in §9 (identical text scores ≈ 1.0).
  - If the PgVector connector only supports `CosineDistance` for HNSW, switch all three phases to `CosineDistance`, convert with `1 - score` at the two wire boundaries (`/api/search`, tool output), and record it.

```csharp
namespace SECFilingsAgenticRag.RetrievalPipeline;

/// Custom (no built-in fits): translates the user-facing filter parameters (tickers / form types / fiscal years from
/// HTTP and agent tools) into the MEVD VectorSearchOptions<T>.Filter expression. Static, pure.
public static class FilingFilters
{
    public static Expression<Func<FilingChunkRecord, bool>>? Build(
        IReadOnlyCollection<string>? tickers, IReadOnlyCollection<string>? formTypes, IReadOnlyCollection<int>? fiscalYears,
        bool excludeInstructionLike);
}

/// Custom (no built-in fits): citation title formatting is domain-specific. Static, pure.
public static class FilingCitation
{
    // "{CompanyName} {FormType} FY{FiscalYear} — {Section}"   e.g. "Apple Inc. 10-K FY2025 — Item 1A. Risk Factors"
    // If Section is empty/whitespace: "{CompanyName} {FormType} FY{FiscalYear}". Separator: em dash U+2014 with spaces.
    public static string Title(FilingChunkRecord record);
}

public static class RetrievalServiceCollectionExtensions
{
    /// DI glue. Requires from host: AddSecFilingsAI + AddSecFilingsVectorStore(config, EmbeddingKeys.Query).
    /// Registers RetrievalOptions (validated) and IVectorSearchable<FilingChunkRecord> composed from configuration (§6.3).
    public static IServiceCollection AddSecFilingsRetrieval(this IServiceCollection services, IConfiguration configuration);
}
```

### 6.2 Options (`SECFilingsAgenticRag.RetrievalPipeline.RetrievalOptions`, bound to section `Retrieval`)

```csharp
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    public string Strategy { get; set; } = "Vector";      // Vector | Hybrid (Hybrid arrives in Phase 6)
    [Range(1, 50)] public int DefaultTop { get; set; } = 5;
    [Range(1, 50)] public int MaxTop { get; set; } = 20;   // hard cap for a requested top
    public bool ExcludeInstructionLikeChunks { get; set; } = true;   // drop chunks flagged at indexing time (Phase 02 ChunkCleaningProcessor)
    public RerankOptions Rerank { get; set; } = new();
    public MultiQueryOptions MultiQuery { get; set; } = new();
    public HybridOptions Hybrid { get; set; } = new();
    public int ClampTop(int? requested) => Math.Clamp(requested ?? DefaultTop, 1, MaxTop);
    public string Describe() => /* "Vector" | "Hybrid" + "+MultiQuery" if enabled + "+Rerank" if enabled */;
}
public sealed class RerankOptions     { public bool Enabled { get; set; } public int CandidatePool { get; set; } = 20; }
public sealed class MultiQueryOptions { public bool Enabled { get; set; } public int Variants { get; set; } = 3; }
public sealed class HybridOptions     { public int CandidatePool { get; set; } = 40; public int RrfK { get; set; } = 60; }
```

Config JSON (defaults):

```jsonc
"Retrieval": {
  "Strategy": "Vector",
  "DefaultTop": 5,
  "MaxTop": 20,
  "ExcludeInstructionLikeChunks": true,
  "Rerank":    { "Enabled": false, "CandidatePool": 20 },
  "MultiQuery":{ "Enabled": false, "Variants": 3 },
  "Hybrid":    { "CandidatePool": 40, "RrfK": 60 }
}
```

**Validation** (`IValidateOptions<RetrievalOptions>`, registered with `.ValidateDataAnnotations().ValidateOnStart()`):
- `Strategy` must name a strategy **registered in this build**. Each strategy is a keyed `IVectorSearchable<FilingChunkRecord>` (key = strategy name, case-insensitive).
  - The validator asks the built-in `IServiceProviderIsKeyedService.IsKeyedService(typeof(IVectorSearchable<FilingChunkRecord>), name)`, so there's no custom registry.
  - Unknown → `OptionsValidationException` listing the valid names. Keep a static list of known names for the message: `["Vector"]` now, Phase 6 adds `"Hybrid"`.
- In Phase 3, `Rerank.Enabled == true` or `MultiQuery.Enabled == true` fails validation with `"Retrieval:Rerank is not available until Phase 6"` (same for MultiQuery). Phase 6 relaxes this.

### 6.3 Composition (`AddSecFilingsRetrieval`)

```
services.AddOptions<RetrievalOptions>().Bind(config.GetSection("Retrieval")).ValidateDataAnnotations().ValidateOnStart();
services.AddSingleton<IValidateOptions<RetrievalOptions>, RetrievalOptionsValidator>();
// "Vector" strategy = the built-in collection itself (zero custom code):
services.AddKeyedSingleton<IVectorSearchable<FilingChunkRecord>>("Vector",
    (sp, _) => sp.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>());
services.AddSingleton<IVectorSearchable<FilingChunkRecord>>(sp =>
{
    var o = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
    var s = sp.GetRequiredKeyedService<IVectorSearchable<FilingChunkRecord>>(o.Strategy /* normalized, case-insensitive */);
    // Phase 6: if (o.MultiQuery.Enabled) s = new MultiQueryVectorSearch(s, ...);
    // Phase 6: if (o.Rerank.Enabled)     s = new RerankingVectorSearch(s, ...);
    return s;
});
```

### 6.4 The one search function (`FilingSearch.SearchFilingsAsync`)

The Retrieval library exposes **one** search function. It is used three ways, and the Phase 06 evals use it too:
1. as the body of the built-in `TextSearchProvider` search delegate (standard-rag, Phase 04);
2. as the body of the `search_filings` tool, exposed through the built-in `AIFunctionFactory` (agentic-rag, Phase 05);
3. behind `POST /api/search` (this phase).

It's an **extension method over the built-in interface**, not a wrapper type or a new abstraction:

```csharp
namespace SECFilingsAgenticRag.RetrievalPipeline;
// Custom (no built-in fits): one place for validation, filter building, the flagged-chunk exclusion and top clamping on top
// of the built-in IVectorSearchable; every consumer calls this, so they can't drift apart.
public static class FilingSearch
{
    public static async Task<IReadOnlyList<VectorSearchResult<FilingChunkRecord>>> SearchFilingsAsync(
        this IVectorSearchable<FilingChunkRecord> search, RetrievalOptions options, string query,
        IReadOnlyCollection<string>? tickers = null, IReadOnlyCollection<string>? formTypes = null,
        IReadOnlyCollection<int>? fiscalYears = null, int? top = null, CancellationToken ct = default)
    {
        // validate query (non-blank, ≤ 2,000 chars) → ArgumentException; callers map it to 400 / an error string
        var vso = new VectorSearchOptions<FilingChunkRecord>
        {
            Filter = FilingFilters.Build(tickers, formTypes, fiscalYears, options.ExcludeInstructionLikeChunks),  // containsinstructionliketext = false
            IncludeVectors = false,
        };
        var hits = new List<VectorSearchResult<FilingChunkRecord>>();
        await foreach (var hit in search.SearchAsync(query.Trim(), options.ClampTop(top), vso, ct)) hits.Add(hit);
        return [.. hits.OrderByDescending(h => h.Score)];   // hit.Record, hit.Score (higher is better)
    }
}
```

- **Which `search`?** The **unkeyed** composed `IVectorSearchable<FilingChunkRecord>` (§6.3). Phase 06's hybrid search (which also implements the built-in `IKeywordHybridSearchable<FilingChunkRecord>`), rerank and multi-query compose **behind** it. They therefore apply inside the `TextSearchProvider` delegate, the tool and the endpoint alike, with no consumer change.
- **Which embedding generator?**
  - The `"Vector"` strategy is AgentServer's `VectorStoreCollection`, configured with Common's keyed **`query`** generator: `AddSecFilingsVectorStore(cfg, EmbeddingKeys.Query)`.
  - The indexing Function writes through a collection over the **same table** configured with the keyed **`document`** generator.
  - `SearchAsync(string, top)` embeds the text through the collection's generator.
- **Untrusted input.** Learn's `TextSearchProvider` security remarks: the search query may come from user input or be LLM-generated, so the search delegate should validate inputs (https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.textsearchprovider). Validate here, normalize filter values in `FilingFilters`, and let tool callers validate tickers against the index (Phase 05).
- **Validation.** `query` must not be null or whitespace (the endpoint returns 400; tools return an error string). Trim it.
- **The query is passed as text.** The store's keyed `query` generator applies the **same** normalization that was applied to documents, then the `search_query: ` prefix. **Do neither yourself.**
  - **Verify** that the PgVector connector embeds a `string` search value through the store's `EmbeddingGenerator` for a `ReadOnlyMemory<float>` vector property.
  - **Fallback:** resolve the keyed `EmbeddingKeys.Query` generator and pass `(await gen.GenerateAsync([question]))[0].Vector`. Record which path works.
- **Order and logging.** Results arrive ordered by score descending; re-sort defensively. Log at Information: question (truncated to 80 chars), filter, `retrievalOptions.Describe()`, count, elapsed ms.
- **Empty or missing table.** AgentServer creates the collection at startup (§6.7), so an empty index returns zero hits.

### 6.5 Filter translation (`FilingFilters.Build`)

- **Normalize** the inputs:
  - tickers → `Trim().ToUpperInvariant()`, distinct;
  - form types → `Trim().ToUpperInvariant()`;
  - years → distinct.

  Empty or null collections mean "no constraint".
- **Arrays.** Convert collections to **arrays** (`string[]`, `int[]`) before capturing them in the lambda. Connectors translate captured arrays far more reliably than `IReadOnlyList<T>`.
- **Single value** → equality (`r => r.Ticker == t`). **Multiple values** → `r => tickers.Contains(r.Ticker)`.
- **Flagged chunks.** When `excludeInstructionLike` is true (the default), AND in `r => !r.ContainsInstructionLikeText`.
  - That column is set by Phase 02's indexing-time prompt-injection flagging. Microsoft's RAG enrichment guidance says to "guard against prompt injection attacks" by flagging or excluding such content.
  - If the connector rejects `!bool`, use `r.ContainsInstructionLikeText == false`.
- **Combining.** AND the parts together with an expression combiner that replaces the second lambda's parameter with the first's, using a small `ExpressionVisitor`. Don't use `Expression.Invoke`: connectors usually reject it.
- **Returned text.** Retrieval returns `FilingChunkRecord.Text`, the stored `content` column, which is the **cleaned** chunk. `Key` is the writer-generated id: stable only until the next re-index, so never persist it.
- **Fallback if the PgVector connector rejects `Contains` on a captured array** (you'll see `NotSupportedException` in check 4b in §9):
  - Build one filter per value (Cartesian product, capped at 10).
  - Each consumer can't be asked to do that, so put the fan-out in a small **decorator** `PerValueFilterVectorSearch : IVectorSearchable<FilingChunkRecord>`, registered around the `"Vector"` strategy only when needed.
  - It merges results, de-duplicates by `Key`, sorts by score and takes `top`.
  - Record which path is active in phase-03-notes. That decorator is then justified in the docs/02 custom-types table.

### 6.6 Citation formatting (`FilingCitation.Title`)

`FilingCitation.Title(record)` → `"Apple Inc. 10-K FY2025 — Item 1A. Risk Factors"`; the link is `record.SourceUrl`. Phases 04–05 reuse it for `TextSearchProvider.TextSearchResult.SourceName` and tool output.

### 6.7 `SECFilingsAgenticRag.AgentServer`

- Project: `dotnet new web -n SECFilingsAgenticRag.AgentServer -o src/SECFilingsAgenticRag.AgentServer`. Add it to the slnx. Reference `SECFilingsAgenticRag.ServiceDefaults`, `SECFilingsAgenticRag.Common`, `SECFilingsAgenticRag.RetrievalPipeline`.
- Packages: `Aspire.Npgsql` (client integration), `Microsoft.AspNetCore.OpenApi`. `Pgvector` comes transitively via the connector; if `UseVector()` doesn't resolve, add the `Pgvector` package and the right `using`, verified in its docs.
- `appsettings.json` (the defaults; every value can be overridden by an environment variable):

```jsonc
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AI": {
    "Chat":      { "Provider": "Ollama", "Model": "qwen3:4b", "TimeoutSeconds": 600 },
    "Embedding": { "Provider": "Ollama", "Model": "nomic-embed-text", "Dimensions": 768,
                   "DocumentPrefix": "search_document: ", "QueryPrefix": "search_query: ",
                   "BatchSize": 16, "TimeoutSeconds": 300,
                   "Normalization": { "UnicodeNormalization": true, "CollapseWhitespace": true, "Lowercase": false } }
  },
  "VectorStore": { "CollectionPrefix": "filing_chunks" },
  "Retrieval": {
    "Strategy": "Vector", "DefaultTop": 5, "MaxTop": 20,
    "ExcludeInstructionLikeChunks": true,
    "Rerank": { "Enabled": false, "CandidatePool": 20 },
    "MultiQuery": { "Enabled": false, "Variants": 3 },
    "Hybrid": { "CandidatePool": 40, "RrfK": 60 }
  }
}
```

  Copy the `AI:Embedding` values from the indexing Function's `appsettings.json` so both hosts match even outside Aspire. Under Aspire the AppHost parameters override both anyway. Don't put `ConnectionStrings` here; Aspire injects them.
- `Properties/launchSettings.json`: a single `http` profile with `"applicationUrl": "http://localhost:5180"`, `"environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }`. Remove the https profile (keeps the local demo simple).
- `Program.cs` sketch:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: b => b.UseVector()); // verify parameter name in Aspire.Npgsql
builder.Services
    .AddSecFilingsAI(builder.Configuration)
    .AddSecFilingsVectorStore(builder.Configuration, EmbeddingKeys.Query)   // read side: query-prefix generator on the store
    .AddSecFilingsRetrieval(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();
// Startup: vector extension + collection (built-in EnsureCollectionExistsAsync) so search on an empty index returns nothing.
await PgVectorSetup.EnsureExtensionAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
await app.Services.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>().EnsureCollectionExistsAsync();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapDefaultEndpoints();          // /health, /alive from ServiceDefaults
app.MapSearchEndpoints();           // extension in Endpoints/SearchEndpoints.cs
app.Run();
```

- `Endpoints/SearchEndpoints.cs`: `POST /api/search` accepts a `SearchRequest` and returns `Results<Ok<SearchResponse>, ValidationProblem>`. Both are small **wire DTOs** in AgentServer project (custom, justified: the HTTP contract must not expose `FilingChunkRecord`'s vector or bookkeeping columns):
  ```csharp
  public sealed record SearchRequest(string Query, int? Top, string[]? Tickers, string[]? FormTypes, int[]? FiscalYears);
  public sealed record SearchHit(Guid Key, string DocumentId, string Ticker, string CompanyName, string FormType, int FiscalYear,
                                 string Section, int ChunkIndex, string Text, double Score, string CitationTitle, string SourceUrl);
  public sealed record SearchResponse(string Query, string Strategy, IReadOnlyList<SearchHit> Hits, TimeSpan Elapsed);
  ```
  The endpoint calls `search.SearchFilingsAsync(...)` (§6.4), maps each `VectorSearchResult<FilingChunkRecord>` to `SearchHit` (`CitationTitle = FilingCitation.Title(r)`), and sets `Strategy = retrievalOptions.Describe()`.
  - Validate manually: `query` required, at most 2,000 characters; `top` in 1..50 if present. Return `TypedResults.ValidationProblem(...)` with field errors.
  - Pass `HttpContext.RequestAborted` as the cancellation token.
- JSON shape (camelCase):

```json
// request
{ "query": "Apple supply chain risk", "top": 5, "tickers": ["AAPL"], "formTypes": null, "fiscalYears": null }
// response
{ "query": "...", "strategy": "Vector", "elapsed": "00:00:00.2140000",
  "hits": [ { "key": "…", "documentId": "AAPL/10-K/…", "ticker": "AAPL", "companyName": "Apple Inc.",
              "formType": "10-K", "fiscalYear": 2025, "section": "Item 1A. Risk Factors", "chunkIndex": 42,
              "text": "…", "score": 0.81, "citationTitle": "Apple Inc. 10-K FY2025 — Item 1A. Risk Factors", "sourceUrl": "https://www.sec.gov/…" } ] }
```

### 6.8 AppHost wiring

In `src/SECFilingsAgenticRag.AppHost/AppHost.cs` (or `Program.cs`, whichever exists), reuse the existing variables for `secfilings` (db) and `ollama` (connection string resource). The API does **not** wait for `ingestion-init` or for indexing:

```csharp
// Model parameters: probably already declared for the indexing Function. Reuse them; never duplicate.
// AppHost appsettings.json:
//   "Parameters": { "chat-model": "qwen3:4b", "embedding-model": "nomic-embed-text", "embedding-dimensions": "768" }
// (Phase 00 declared them: var chatModel = builder.AddParameter("chat-model"); etc. Reuse those variables.)

var api = builder.AddProject<Projects.SECFilingsAgenticRag_AgentServer>("api")
    .WithReference(secfilingsDb).WaitFor(secfilingsDb)
    .WithReference(ollama)
    .WithAIModels(chatModel, embeddingModel, embeddingDimensions)   // Phase 00 helper → AI__Chat__Model, AI__Embedding__Model, AI__Embedding__Dimensions
    .WithExternalHttpEndpoints();
```

The `indexing` Function must receive the **same** parameters via `.WithAIModels(chatModel, embeddingModel, embeddingDimensions)`. Check this, and add it if Phase 02 didn't. Changing `Parameters:embedding-model` then changes both sides together, which yields a new collection name (and requires re-indexing) instead of mismatched vectors.

**Fixed port 5180, the chosen approach.** Aspire creates the project's `http` endpoint from `launchSettings.json` and uses `applicationUrl`'s port (5180) as the **host/proxy port**. That's the port clients use. The app process itself gets a random internal port behind Aspire's proxy. So setting `applicationUrl` to `http://localhost:5180` is enough, and the AgentClient.Cli can always reach `http://localhost:5180`.
- Don't also call `.WithHttpEndpoint(port: 5180)`: that adds a second endpoint named `http` and fails with a duplicate-endpoint error.
- If 5180 turns out not to be honoured (check with `aspire describe api`, or by calling `http://localhost:5180/health`), fall back to `.WithEndpoint("http", e => e.Port = 5180)`. That edits the existing endpoint. Note which approach worked in phase-03-notes.

## 7. Implementation steps

1. **Create the projects.**
   ```powershell
   dotnet new classlib -n SECFilingsAgenticRag.RetrievalPipeline -o src/SECFilingsAgenticRag.RetrievalPipeline
   dotnet new web      -n SECFilingsAgenticRag.AgentServer       -o src/SECFilingsAgenticRag.AgentServer
   dotnet sln SECFilingsAgenticRag.slnx add src/SECFilingsAgenticRag.RetrievalPipeline src/SECFilingsAgenticRag.AgentServer
   ```
   Remove the template `Class1.cs`. Strip every `Version="…"` from the generated `<PackageReference>`s and move the versions into `Directory.Packages.props` (CPM; otherwise NU1008), and delete the generated `<TargetFramework>`. Add project references: RetrievalPipeline → Common; AgentServer → ServiceDefaults, Common, RetrievalPipeline. Check: `dotnet build` is green.
2. **Helpers.** Add `FilingFilters` and `FilingCitation` (§6.1). Check: build.
3. **Options + validator** (§6.2, using `IServiceProviderIsKeyedService`). Check: `dotnet build`. Check 8 in §9 verifies the startup failure.
4. **FilingCitation** (§6.6). Check: build.
5. **FilingFilters** (§6.5). Check: build. Checks 3, 4a and 4b in §9 verify the filters against pgvector.
6. **Search path** (§6.4): verify string search values are embedded by the store's query generator, or take the fallback. Check: build; check 2 in §9 proves the search path end to end.
7. **AddSecFilingsRetrieval** (§6.3). Check: build.
8. **AgentServer** (§6.7). Check: `dotnet run --project src/SECFilingsAgenticRag.AgentServer` fails fast without a DB (expected). Under Aspire it starts.
9. **AppHost wiring** (§6.8). Check: `aspire start`, then `aspire wait ingestion-init --status down --timeout 3600` (completes), then `aspire wait api`. `aspire describe` shows `api` *Running* on `http://localhost:5180`; it starts as soon as Postgres is up. Finish with `aspire stop`.
10. **Manual acceptance** (§9). Write the notes file, with the `/api/search` smoke command in the `## Commands & operating notes` section of your phase notes.

## 8. Tests

No automated tests in this phase. All unit and integration tests are deferred to [Phase 09](phase-09-tests.md), which specifies this phase's `RetrievalPipeline.Tests` and the `/api/search` endpoint tests in `AgentServer.Tests`. This phase is verified by `dotnet build` and the run-time checks in §9.

## 9. Acceptance criteria

Start everything:

```powershell
aspire start              # background; `aspire run` would block the terminal
aspire wait ingestion-init --status down --timeout 3600        # documents downloaded + uploaded; the job has exited
aspire wait indexing
./scripts/Invoke-Indexing.ps1   # YOU request indexing (minutes the first time; seconds when everything is skipped)
aspire wait api
aspire describe           # optional: shows resource states and endpoints
```

Then, in the same terminal:

```powershell
# 1) Health
Invoke-RestMethod http://localhost:5180/health          # -> "Healthy"

# 2) Unfiltered semantic search
$body = @{ query = "Apple supply chain risk"; top = 5 } | ConvertTo-Json
$r = Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body
$r.strategy                                             # -> Vector
$r.hits | Select-Object ticker, section, @{n='score';e={[math]::Round($_.score,3)}}
```

**Expected:** 5 hits; the majority (≥3) are `AAPL`, and the top results come from `Item 1A. Risk Factors` (or a closely related section if heading detection labelled them differently). Scores are in (0, 1] and descending.

```powershell
# 3) Ticker filter
$body = @{ query = "Apple supply chain risk"; top = 5; tickers = @("MSFT") } | ConvertTo-Json -Depth 5
(Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body).hits.ticker | Sort-Object -Unique
# -> MSFT   (only)

# 4) Validation
Invoke-WebRequest -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body '{"query":""}' -SkipHttpErrorCheck |
  Select-Object StatusCode                               # -> 400 (ProblemDetails body)
```

4a. **Indexing-time flags are honored.** Mark one AAPL chunk as flagged directly in the DB, then search for its exact text. `$pg` is the Postgres container found by name; the table is the resolved `FilingChunkSchema.CollectionName`.
   ```powershell
   $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
   $id = (docker exec $pg psql -U postgres -d secfilings -t -A -c "SELECT key FROM filing_chunks_nomic_embed_text_768 WHERE ticker='AAPL' LIMIT 1;").Trim()
   $txt = (docker exec $pg psql -U postgres -d secfilings -t -A -c "SELECT LEFT(content, 200) FROM filing_chunks_nomic_embed_text_768 WHERE key='$id';").Trim()
   docker exec $pg psql -U postgres -d secfilings -c "UPDATE filing_chunks_nomic_embed_text_768 SET containsinstructionliketext=true WHERE key='$id';"
   $body = @{ query = $txt; top = 5 } | ConvertTo-Json
   (Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body).hits.key -contains $id   # -> False
   docker exec $pg psql -U postgres -d secfilings -c "UPDATE filing_chunks_nomic_embed_text_768 SET containsinstructionliketext=false WHERE key='$id';"   # restore
   ```
   Column names are the writer's lower-case storage names (verify with `\d+`; see the Phase 02 notes). Also confirm that the returned `text` equals the stored `content`, i.e. the cleaned chunk with no table pipes.

4b. **Score semantics and the multi-ticker filter.** The PgVector connector's score direction and its translation of `Contains` on a captured array are assumptions; until Phase 09 this check is their only guard. Reuse `$id` and `$txt` from 4a (after the restore):
   ```powershell
   $body = @{ query = $txt; top = 1 } | ConvertTo-Json
   (Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body).hits | Select-Object key, score
   # -> key = $id, score >= 0.99 (CosineSimilarity: higher is better). A score near 0 means distance semantics: take the §6.4 fallback.
   $body = @{ query = "supply chain and competition risks"; top = 10; tickers = @("AAPL","MSFT") } | ConvertTo-Json -Depth 5
   (Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body).hits.ticker | Sort-Object -Unique
   # -> AAPL and MSFT only. A NotSupportedException in the API log means the connector rejects Contains: take the §6.5 fallback.
   ```
   Record both outcomes in the notes.

5. **Latency:** warm requests take about **150–600 ms** on CPU (query embedding dominates). The first request after startup may take several seconds while Ollama loads `nomic-embed-text`.
6. `dotnet build SECFilingsAgenticRag.slnx`: 0 errors, 0 warnings.
7. **Configuration override by environment variable** (proves models aren't hard-coded). Run the API outside Aspire with a bogus embedding model and confirm the request fails with a model-not-found error from Ollama (a 500 ProblemDetails mentioning the model), not a silently-used default:
   ```powershell
   # with the AppHost running (so Postgres is up), get the DB connection string: `aspire describe api` (environment section, ConnectionStrings__secfilings).
   # If the CLI doesn't print environment values, read it from the dashboard: api → Environment (optional human step).
   $env:ConnectionStrings__secfilings = "<paste>"
   $env:ConnectionStrings__ollama     = "Endpoint=http://localhost:11434"
   $env:AI__Embedding__Model          = "does-not-exist"
   $env:ASPNETCORE_URLS               = "http://localhost:5181"
   dotnet run --project src/SECFilingsAgenticRag.AgentServer
   # second terminal:
   Invoke-WebRequest -Method Post -Uri http://localhost:5181/api/search -ContentType 'application/json' -Body '{"query":"risk"}' -SkipHttpErrorCheck | Select-Object StatusCode
   # -> 500 and the API log shows Ollama "model 'does-not-exist' not found"
   ```
   Then remove the variable (`Remove-Item Env:AI__Embedding__Model`), restart, and confirm 200. Also confirm that **deleting** `AI:Embedding:Model` from `appsettings.json` (temporarily) makes the API fail at **startup** with an `OptionsValidationException`. Restore it afterwards.
8. Changing `Retrieval:Strategy` to `Bogus` (e.g. `$env:Retrieval__Strategy="Bogus"` before `dotnet run --project src/SECFilingsAgenticRag.AgentServer`) makes the API **fail at startup** with a message listing valid strategies.

## 10. Gotchas

- **Query prefix.** Only Common's keyed `query` generator adds `search_query: `. Adding it again, or calling the unkeyed provider generator directly, silently degrades results.
- **Distance vs similarity.** pgvector's `<=>` operator is cosine *distance*; the schema's `DistanceFunction.CosineSimilarity` makes the connector return similarity. Verify what `VectorSearchResult.Score` contains (§6.1).
- **Captured lists in filters.** Use arrays, avoid `Expression.Invoke`, and fall back to per-value searches if `Contains` isn't translated (§6.5).
- **Collection naming.** Always resolve the collection from DI (`AddSecFilingsVectorStore`). Never hard-code `"filing_chunks_nomic_embed_text_768"`; it changes if the embedding model changes.
- **Empty collection / table missing.** AgentServer creates the collection at startup (`EnsureCollectionExistsAsync`), so search before indexing returns zero hits. If the writer later rejects that table (see Phase 02 check 3b), switch to a `CollectionExistsAsync()` check and return empty.
- **Ollama cold start.** The first embedding call can take 5–20 s. Common's HttpClient has a long timeout, so don't add a short one here.
- **Built-in first.** If you're about to write a retriever interface, a request/result record for in-process calls, or a strategy registry, stop: `IVectorSearchable<FilingChunkRecord>`, `VectorSearchOptions<T>`, `VectorSearchResult<T>` and `IServiceProviderIsKeyedService` already cover them.
- **ServiceDefaults resilience.** The `IHttpClientFactory` defaults (10 s attempt / 30 s total) don't apply to Common's dedicated Ollama client. Keep it that way.
- **Fixed port.** Use `launchSettings.json` `applicationUrl` and don't add a duplicate `http` endpoint (§6.8).
- **Empty results before indexing.** The API starts without waiting for indexing. Until you run `./scripts/Invoke-Indexing.ps1` (or `aspire resource indexing reindex`), `/api/search` returns no chunks, and that is expected. A 409 from the script means indexing is already running; wait and retry.
- **No model names in code.** Don't write `"nomic-embed-text"`, `"qwen3:4b"` or `768` in any `.cs` file. They belong in `appsettings.json` and AppHost parameters. Grep before finishing (no output expected; comments count too):
  `Get-ChildItem src,evals -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Where-Object FullName -notmatch '\\(obj|bin)\\' | Select-String -Pattern 'qwen|nomic|llama|gpt-'`.
- **Npgsql + DateTime.** Records read back carry `DateTimeKind.Utc`; don't convert them to local time in mappings.

## 11. Definition of done and handoff

### 11.0 Microsoft Learn evidence log (required)

Your `docs/notes/phase-NN-notes.md` must contain this section. It is the project's audit trail of how official documentation shaped the code, and the user cites it in interviews.

```markdown
## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/... | e.g. exact HeaderChunker options | e.g. MaxTokensPerChunk=512; OverlapTokens ignored → 0 (see §…) |
```

- Record **every** Microsoft Learn page or code sample you used, whether found with `microsoft_docs_search`, `microsoft_code_sample_search` or `microsoft_docs_fetch`. Include the page you verified each preview API against.
- Record where the docs and reality **disagreed** (API renamed, sample outdated) and what you did about it.
- Map design choices to the relevant phase of Microsoft's RAG guide (preparation, chunking, enrichment, embeddings, retrieval, LLM evaluation, agentic RAG) where one applies. See `docs/02-microsoft-guidance-alignment.md`.
- If a fact came from non-Microsoft docs (SEC EDGAR, pgvector), list it too and mark it *external*.

- `dotnet build SECFilingsAgenticRag.slnx` has no errors and no new warnings.
- All of §9 verified manually. Paste the outputs (trimmed) into the notes.
- Write **`docs/notes/phase-03-notes.md`** containing:
  - the package versions added;
  - whether `Score` was distance or similarity;
  - whether `Contains` filters worked or the fallback is active;
  - which fixed-port approach worked;
  - the actual table and column names observed;
  - any deviations from this spec.
- Add to the `## Commands & operating notes` section of your phase notes: how to run the API, and the `/api/search` smoke-test command.
- Suggested commit message (don't commit):
  ```
  feat(retrieval): add SECFilingsAgenticRag.RetrievalPipeline (vector search, filters, citations) and AgentServer POST /api/search
  ```
