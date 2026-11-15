# Phase 06: Retrieval quality and evaluations

> Depends on: Phases 00–05 (foundation, data, indexing, retrieval, chat API + standard RAG, agentic RAG) | Produces: `PostgresHybridSearch` (built-in `IVectorSearchable` + `IKeywordHybridSearchable`), `RerankingVectorSearch` + `MultiQueryVectorSearch` decorators (MEAI `GetResponseAsync<T>`), config-driven composition, `evals/SECFilingsAgenticRag.Evals` (retrieval metrics matrix + answer-quality report) | Est. size: L

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
- **Authority.**
  - This spec and `docs/01-architecture-and-design.md` are authoritative.
  - If the code on disk conflicts with a contract here, **the code on disk wins**. Adapt to it and record the deviation in `docs/notes/phase-06-notes.md`.
- **Read first.**
  - Every `docs/notes/phase-*-notes.md` from earlier phases (00–05). They record deviations, actual package versions and decisions.
  - `CLAUDE.md`.
- **Verify unfamiliar or preview APIs** before coding against them:
  - Use the Microsoft Learn MCP tools (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`) if available.
  - Otherwise use the package XML docs, IntelliSense or compiler errors.
  - The APIs most likely to have moved are `Microsoft.Extensions.AI.Evaluation.*`, `CommunityToolkit.VectorData.PgVector` and `Microsoft.Extensions.VectorData`.
- **Central package management.**
  - Add every new package version to `Directory.Packages.props`, with no `Version=` in csproj files.
  - Use the latest **stable** version, or the latest preview only if no stable exists.
  - Check with `dotnet package search <id> --exact-match --prerelease`.
- **Environment.** Windows + PowerShell. Use PowerShell syntax in every command you run or document.
- **Style.**
  - Match the existing code style: namespaces, file layout, `sealed` classes, primary constructors if already used, logging style.
  - Do **not** change earlier phases' public contracts unless unavoidable. If you must, document the change in the notes file.
- **Process.**
  - **Do not `git commit`.**
  - Ask nothing. Make reasonable decisions and document them in the notes file.
- **No hard-coded model names (project-wide rule, docs/02 §4.2).** No model name, embedding dimension or endpoint may appear as a literal in C#. This includes the reranker model, the query-rewriter model and the eval judge model.
  - Options properties for Provider, Model and Dimensions are `[Required]`, with **no code defaults**, and are bound with `ValidateDataAnnotations().ValidateOnStart()`.
  - Defaults live in each host's `appsettings.json`.
  - Environment variables override them after deployment, e.g. `AI__Chat__Model`, `Retrieval__Rerank__Model`, `Evals__Judge__Model`.
  - API keys come only from user-secrets or environment variables.

---

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

Until now the system uses one retrieval technique: pure vector (semantic) search. That's a good start, but it has known weaknesses:

- It can miss exact terms such as "Item 1A", "Rule 10b5-1", product names and tickers. Embeddings capture *meaning* well and *exact tokens* poorly.
- The top-5 results are ordered by a fairly crude similarity score.
- A vague user question produces one vague embedding.

This phase adds three well-known improvements, each as a **pluggable piece selected by configuration only** (the Open/Closed principle):

1. **Hybrid search.** Postgres full-text (keyword) search plus vector search, fused with Reciprocal Rank Fusion (RRF).
2. **Reranking.** Fetch about 20 candidates, then ask an LLM to re-order them by relevance and keep the best 5.
3. **Multi-query.** Ask an LLM to rewrite the question into several variants, search with each, and fuse the results.

Then you answer the question an interviewer will ask: *"Does it actually help? Prove it."* You build an **evaluation harness** that:

- runs a golden question set against every retrieval configuration and reports **Hit@k, MRR and Recall@k**;
- runs the named agents (`standard-rag` vs `agentic-rag`) in-process and scores the answers with **Microsoft.Extensions.AI.Evaluation** (Relevance, Groundedness, Completeness).

Everything is built on the **built-in** retrieval contract `IVectorSearchable<FilingChunkRecord>`. The hybrid search *implements* it (plus the built-in `IKeywordHybridSearchable<FilingChunkRecord>`), and reranking and multi-query *decorate* it. LLM calls use MEAI structured output (`GetResponseAsync<T>`) instead of hand-written JSON parsing. Judge caching uses the evaluation library's built-in response cache.

---

## 2. Concepts in 60 seconds

- **Keyword search (full-text).** Postgres turns text into a `tsvector`: normalized word stems such as `risk`, `suppli` and `chain`. A query becomes a `tsquery`. `ts_rank_cd` scores how well the words match. It's great for exact terms and bad for synonyms.
- **Vector search.** Cosine similarity between embeddings (pgvector's `<=>` is distance; the schema's `CosineSimilarity` makes MEVD report similarity). It's great for meaning and weak on rare exact tokens.
- **RRF (Reciprocal Rank Fusion).** A way to combine ranked lists without comparing their incompatible scores. Each document gets `Σ 1 / (k + rank)` over every list it appears in, with `k = 60` by convention. Documents ranked well in *both* lists rise to the top.
- **Reranking.** A second, more expensive pass over a small candidate set. A real cross-encoder model would be ideal, but Ollama doesn't serve those, so we use the chat LLM as a *listwise reranker*: "here are 20 passages; order them by relevance to the question".
- **Multi-query.** Generate paraphrases of the user question to widen recall, then fuse the results with RRF.
- **Hit@k.** Did *any* relevant chunk appear in the top k results?
- **MRR (Mean Reciprocal Rank).** The average of `1 / rank of the first relevant chunk`. It rewards putting a relevant chunk near the top.
- **Recall@k.** Of all the things the question needed, the fraction found in the top k.
- **LLM-as-judge.** A (preferably strong) model scores each answer from 1 to 5. It's useful but noisy, and small local judges are unreliable.

---

## 3. Environment and prerequisites

- Windows 11, PowerShell 7.3+, .NET 10 SDK, Docker Desktop running.
- Native Ollama at `http://localhost:11434` with the models pulled:
  ```powershell
  ollama pull qwen3:4b
  ollama pull nomic-embed-text
  ```
- The corpus is indexed: you have run `aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1` at least once, so the chunk table is populated (5 companies: AAPL, MSFT, NVDA, AMZN, TSLA; the latest 10-K each).
- Optional but **recommended** for answer-quality evals: an OpenAI-compatible cloud endpoint for the judge model. Examples:
  - GitHub Models: `https://models.github.ai/inference` with a GitHub token;
  - OpenAI;
  - the Azure OpenAI v1 endpoint.

  Store the key with `dotnet user-secrets`. Never commit it.

---

## 4. Repo state at start

```
SECFilingsAgent/
├─ SECFilingsAgenticRag.slnx, Directory.Build.props, Directory.Packages.props, CLAUDE.md
├─ data/filings/                 (git-ignored)
├─ docs/ (01-architecture-and-design, 02-microsoft-guidance-alignment, specs + notes)
├─ src/
│  ├─ SECFilingsAgenticRag.AppHost/        resources: postgres→secfilings, storage→filings, ollama (conn string), indexing, ingestion-init, api(:5180)
│  ├─ SECFilingsAgenticRag.ServiceDefaults/
│  ├─ SECFilingsAgenticRag.Common/           FilingMetadata, FilingChunkRecord, FilingChunkSchema, EmbeddingKeys, AddSecFilingsAI, AddSecFilingsVectorStore, PgVectorSetup
│  ├─ SECFilingsAgenticRag.IngestionPipeline/ + SECFilingsAgenticRag.IngestionPipeline.Functions/ + SECFilingsAgenticRag.IngestionInit/
│  ├─ SECFilingsAgenticRag.RetrievalPipeline/      RetrievalOptions, FilingFilters, FilingCitation, AddSecFilingsRetrieval (keyed IVectorSearchable "Vector" = the collection)
│  ├─ SECFilingsAgenticRag.AgentServer/          AgentNames (chat-only, standard-rag, agentic-rag), AddSecFilingsAgents (MAF hosting), FilingTools, Prompts; POST /api/search, AG-UI /agents/{name}, GET /api/filings
│  └─ SECFilingsAgenticRag.AgentClient.Cli/
```

### 4.1 Contracts you consume (copied from docs/02; the code on disk wins)

```csharp
namespace SECFilingsAgenticRag.Common.VectorStore;

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

public static class FilingChunkSchema
{
    public static VectorStoreCollectionDefinition CreateDefinition(int dimensions);
    public static string CollectionName(VectorStoreOptions store, EmbeddingOptions embedding); // "filing_chunks_nomic_embed_text_768"
}
```

```csharp
namespace SECFilingsAgenticRag.Common.AI;
public static class EmbeddingKeys { public const string Document = "document", Query = "query"; }
// AddSecFilingsAI(services, configuration): IChatClient (UseLogging/UseOpenTelemetry) + KEYED IEmbeddingGenerator
//   "document"/"query" (normalize → prefix → validate dims; Query optionally UseDistributedCache).
// ChatModelOptions (AI:Chat): Provider (Ollama|OpenAICompatible), Model, Endpoint?, ApiKey?, TimeoutSeconds.
// AddSecFilingsVectorStore(services, configuration, embeddingKey): PostgresVectorStore with that keyed generator + the collection.
```

```csharp
namespace SECFilingsAgenticRag.RetrievalPipeline;
// THE query-phase contract is built in: Microsoft.Extensions.VectorData.IVectorSearchable<FilingChunkRecord>
//   SearchAsync<TInput>(TInput searchValue /* the question text */, int top, VectorSearchOptions<FilingChunkRecord>? options, CancellationToken)
//     -> IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>>   (Score: higher is better — keep that true for every implementation)
// Built-in hybrid contract (implemented by us in this phase): IKeywordHybridSearchable<FilingChunkRecord>.HybridSearchAsync(searchValue, keywords, top, HybridSearchOptions<FilingChunkRecord>?, ct)
public sealed class RetrievalOptions
{
    public string Strategy { get; set; } = "Vector"; public int DefaultTop; public int MaxTop; public bool ExcludeInstructionLikeChunks;
    public RerankOptions Rerank { get; set; } = new(); public MultiQueryOptions MultiQuery { get; set; } = new(); public HybridOptions Hybrid { get; set; } = new();
    public int ClampTop(int? requested); public string Describe();   // e.g. "Hybrid+MultiQuery+Rerank"
}
public static class FilingFilters  { public static Expression<Func<FilingChunkRecord,bool>>? Build(IReadOnlyCollection<string>? tickers, IReadOnlyCollection<string>? formTypes, IReadOnlyCollection<int>? fiscalYears, bool excludeInstructionLike); }
public static class FilingCitation { public static string Title(FilingChunkRecord record); }
public static class RetrievalServiceCollectionExtensions
{   // Keyed IVectorSearchable<FilingChunkRecord> strategies ("Vector" = the collection) + the composed unkeyed IVectorSearchable;
    // validator uses the built-in IServiceProviderIsKeyedService.
    public static IServiceCollection AddSecFilingsRetrieval(this IServiceCollection services, IConfiguration configuration);
}
```

```csharp
namespace SECFilingsAgenticRag.AgentServer;
public static class AgentNames { public const string ChatOnly = "chat-only", StandardRag = "standard-rag", AgenticRag = "agentic-rag"; }
// AddSecFilingsAgents(IHostApplicationBuilder): MAF hosting builder.AddAIAgent(name, ...) for each name (+ AgentSessionStore).
// Resolve an agent in-process by name (verify the keyed-service shape AddAIAgent uses, e.g. GetRequiredKeyedService<AIAgent>(name)).
```

### 4.2 Configuration keys involved

```jsonc
"Retrieval": {
  "Strategy": "Vector",                         // Vector | Hybrid
  "DefaultTop": 5,
  "Rerank":     { "Enabled": false, "CandidatePool": 20, "Model": null },     // Model: optional override; null → uses AI:Chat:Model
  "MultiQuery": { "Enabled": false, "Variants": 3, "Model": null },         // Model: optional override; null → uses AI:Chat:Model
  "Hybrid":     { "CandidatePool": 40, "RrfK": 60 }
},
"Agent": { "RetrievalTop": 5, "MaxToolIterations": 6 },
"VectorStore": { "CollectionPrefix": "filing_chunks" },
// AI:* values live in each host's appsettings.json; under Aspire they are injected from the AppHost
// parameters chat-model / embedding-model / embedding-dimensions via WithEnvironment("AI__Chat__Model", ...).
"AI": { "Chat": { "Provider": "Ollama", "Model": "qwen3:4b", ... }, "Embedding": { "Provider": "Ollama", "Model": "nomic-embed-text", "Dimensions": 768, ... } },
"ConnectionStrings": { "secfilings": "...", "ollama": "Endpoint=http://localhost:11434" }
```

**Model overrides for the reranker and rewriter.**
- `Retrieval:Rerank:Model` and `Retrieval:MultiQuery:Model` are **optional strings**. They are the only model settings that are allowed to be absent: when null or empty, the component uses the registered `IChatClient` (the `AI:Chat` model).
- When set, build a dedicated `IChatClient` for that model with the same provider and endpoint as `AI:Chat`. Use the Common chat-client factory (see §6.6); never a literal.
- **Caching (built in):** both LLM-using decorators may wrap their client with MEAI `UseDistributedCache()` (an `IDistributedCache`, e.g. `AddDistributedMemoryCache()` in the host), so repeated identical prompts during eval runs don't re-pay.
- Example deployment override: `Retrieval__Rerank__Model=qwen3:8b`.

New in this phase (Evals project only). The values below are the defaults in **`evals/SECFilingsAgenticRag.Evals/appsettings.json`**, not in C#:

```jsonc
"Evals": {
  "DatasetPath": "datasets/golden.json",
  "ResultsPath": "../../evals/results",          // relative to the project dir; resolve to an absolute path
  "Judge": {                                      // same shape as AI:Chat; bound to a JudgeOptions class
    "Provider": "OpenAICompatible",               // [Required] Ollama | OpenAICompatible
    "Model": "gpt-4o-mini",                       // [Required] override: Evals__Judge__Model
    "Endpoint": "https://models.github.ai/inference", // override: Evals__Judge__Endpoint
    "ApiKey": null,                               // NEVER in appsettings: user-secrets or Evals__Judge__ApiKey
    "TimeoutSeconds": 300
  }
}
```

`JudgeOptions.Provider` and `JudgeOptions.Model` are `[Required]` with no code defaults, validated on start. The evals `appsettings.json` must also contain the `AI:Chat` and `AI:Embedding` sections, because the evals app is a host, with values identical to AgentServer's. Document `Evals__Judge__Provider`, `Evals__Judge__Model`, `Evals__Judge__Endpoint` and `Evals__Judge__ApiKey` as environment overrides in the `## Commands & operating notes` section of your phase notes.

---

## 5. Scope

**In**
- `PostgresHybridSearch : IVectorSearchable<FilingChunkRecord>, IKeywordHybridSearchable<FilingChunkRecord>`, with an idempotent schema migration for the full-text column and index. It's custom and justified: the PgVector connector doesn't implement `IKeywordHybridSearchable`.
- `RrfFusion` (a shared static helper).
- `RerankingVectorSearch` (decorator, `IVectorSearchable<FilingChunkRecord>`), using MEAI structured output `GetResponseAsync<Ranking>`.
- `MultiQueryVectorSearch` (decorator), using `GetResponseAsync<QueryVariants>`.
- Config-driven composition in `AddSecFilingsRetrieval`, with `RetrievalOptions.Describe()` naming.
- **Not built (built in instead):**
  - no reranker or rewriter interfaces;
  - no JSON-parsing helper: MEAI structured output does the parsing;
  - no custom cache: MEAI `UseDistributedCache` handles it, and the evaluation reporting library has its own response cache.
- `evals/SECFilingsAgenticRag.Evals` console app with three commands: `dataset-helper`, `retrieval` and `answers`.
- The golden dataset (built from actually indexed data).
- Markdown results report, plus an HTML report via `dotnet aieval report`.
- Optional: AppHost resources to run evals from the dashboard.

**Out**
- Cross-encoder ONNX rerankers (mention them in the notes as future work).
- Changing the indexing pipeline: hybrid search must work **without** editing `SECFilingsAgenticRag.IngestionPipeline`.
- UI changes, conversation persistence and telemetry (Phase 07).

---

## 6. Design

### 6.1 Files to add

```
src/SECFilingsAgenticRag.RetrievalPipeline/
  Fusion/RrfFusion.cs                       static: Σ 1/(k+rank)
  (options) HybridOptions / RerankOptions / MultiQueryOptions ALREADY EXIST from Phase 03, nested in
            RetrievalOptions (Retrieval:Hybrid / :Rerank / :MultiQuery). Extend them in place (e.g. add an
            optional Model property); do NOT create duplicate classes.
  Hybrid/FilingChunkTableInfo.cs            resolves table + column names from the VectorStoreCollectionDefinition
  Hybrid/HybridSchemaInitializer.cs         idempotent migration, run once (lazy, thread-safe)
  Hybrid/PostgresHybridSearch.cs            : IVectorSearchable<FilingChunkRecord>, IKeywordHybridSearchable<FilingChunkRecord>
  Rerank/RerankingVectorSearch.cs           decorator : IVectorSearchable<FilingChunkRecord>
  MultiQuery/MultiQueryVectorSearch.cs      decorator : IVectorSearchable<FilingChunkRecord>
  RetrievalServiceCollectionExtensions.cs   (modify: composition)
evals/SECFilingsAgenticRag.Evals/
  SECFilingsAgenticRag.Evals.csproj, Program.cs, appsettings.json
  Dataset/GoldenDataset.cs, Dataset/GoldenQuestion.cs
  Retrieval/RelevanceRule.cs, Retrieval/RetrievalMetrics.cs, Retrieval/RetrievalMatrixRunner.cs
  Answers/RecordingVectorSearch.cs, Answers/AnswerEvalRunner.cs
  Hosting/EvalServiceFactory.cs             builds a ServiceProvider per configuration variant
  Reporting/MarkdownReport.cs
  datasets/golden.json
evals/results/.gitkeep  +  evals/results/sample-retrieval.md (one committed sample)
```

Add `evals/results/*` to `.gitignore`, with exceptions `!evals/results/.gitkeep` and `!evals/results/sample-*.md`.

### 6.2 Hybrid search

**Where does the migration run?** In the **retrieval library**, lazily, the first time `PostgresHybridSearch` runs (guarded by a `SemaphoreSlim`, so it happens once per process).

Why:
- The full-text index is a *query-side* concern.
- A Postgres **generated column** fills itself for existing rows and for every future insert, so the indexing library needs **no change**. That's the Open/Closed demonstration.
- If the table doesn't exist yet (nothing indexed), the initializer logs a warning and returns. The hybrid retriever then falls back to vector-only for that call and retries the migration on the next call.

**Resolve real names first.** The table is written by MEDI's `VectorStoreWriter<string>` (Phase 02), so the columns are the writer's lower-case storage names (`key`, `content`, `documentid`, `ticker`, `formtype`, `fiscalyear`, `containsinstructionliketext`, `embedding`, …). `FilingChunkSchema` mirrors them through `StorageName`. `FilingChunkTableInfo` must read the actual names from `FilingChunkSchema.CreateDefinition(dims)` (each property's `StorageName` if set, else `Name`). Never hard-code them. Confirm against the live database:

```powershell
# Find the Aspire postgres container by NAME (leftover containers can share the image) and open psql.
# Local socket auth inside the container needs no password.
$pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
# The table name is FilingChunkSchema.CollectionName(...); the default is shown here:
docker exec $pg psql -U postgres -d secfilings -c '\d "filing_chunks_nomic_embed_text_768"'
```

**Migration SQL** (identifiers quoted; `{table}` and `{text}` come from `FilingChunkTableInfo`):

```sql
-- 1) generated tsvector column (idempotent)
ALTER TABLE "{table}"
  ADD COLUMN IF NOT EXISTS search_tsv tsvector
  GENERATED ALWAYS AS (to_tsvector('english', coalesce("{text}", ''))) STORED;

-- 2) GIN index (idempotent)
CREATE INDEX IF NOT EXISTS "ix_{table}_search_tsv" ON "{table}" USING GIN (search_tsv);
```

Check for table existence first with `SELECT to_regclass('public."{table}"') IS NOT NULL`. Index names are limited to 63 characters, so truncate safely if needed.

**Keyword query** (top N = `Hybrid:CandidatePool`, default 40):

```sql
SELECT "{key}", ts_rank_cd(search_tsv, q) AS rank
FROM "{table}", websearch_to_tsquery('english', @query) AS q
WHERE search_tsv @@ q
  AND (@tickers::text[]   IS NULL OR "{ticker}"     = ANY(@tickers))
  AND (@forms::text[]     IS NULL OR "{formType}"   = ANY(@forms))
  AND (@years::int[]      IS NULL OR "{fiscalYear}" = ANY(@years))
  AND (NOT @excludeFlagged OR "{flagged}" = false)   -- Retrieval:ExcludeInstructionLikeChunks (Phase 03); {flagged} = containsinstructionliketext via FilingChunkTableInfo
ORDER BY rank DESC
LIMIT @n;
```

**Chunk-enrichment interplay (Phase 02 "clean chunks").**
- **Only one text is stored**: `content`, the cleaned chunk (Phase 02 stores only the cleaned chunk). The keyword (`tsvector`) side is built over it (`{text}` = `content`). Postgres full-text search does its own normalization (stemming, stop words) on top.
- The vector side uses embeddings of the same cleaned `content`, embedded by the write-side store's generator.
- Both sides must honor `Retrieval:ExcludeInstructionLikeChunks`: add the flag condition to the keyword query (above) **and** to the vector query, with the same `@excludeFlagged` parameter.
- **Evaluating cleaning variants (optional):** Microsoft's enrichment guide says to *test* lowercasing and similar cleaning before adopting it.
  1. To compare, index a second copy with `AI:Embedding:Normalization:Lowercase=true` (or different `Indexing:Cleaning` options) into a separate collection, using a different `VectorStore:CollectionPrefix` (e.g. `filing_chunks_lc`).
  2. Point the eval matrix at both prefixes as extra configurations, e.g. `Vector@filing_chunks` vs `Vector@filing_chunks_lc`.
  3. Report Hit@k / MRR side by side.
  4. Record the outcome in the notes, so the decision to keep or drop lowercasing is evidence-based.

**Vector side: use the built-in search, not custom SQL.** Call the collection's own `SearchAsync(searchValue, CandidatePool, new VectorSearchOptions<FilingChunkRecord> { Filter = options.Filter })`. MEVD translates the filter and the store's keyed `query` generator embeds the text with the right prefix.

**How filters reach the keyword side.** Consumers pass the **built-in** `VectorSearchOptions<FilingChunkRecord>.Filter`, which is an expression. Our keyword SQL can't translate arbitrary expressions, so:
1. Select the keyword candidates **without** the ticker, form and year predicates. Keep only the flagged-chunk predicate, via `Retrieval:ExcludeInstructionLikeChunks`.
2. Over-fetch (`LIMIT 5 × CandidatePool`) and return full scalar rows: every column except the vector.
3. Map the rows to `FilingChunkRecord`.
4. Apply `options.Filter.Compile()` **in memory**.
5. Keep the first `CandidatePool`.

Document the limitation: keyword recall for a narrowly filtered query depends on the over-fetch window.

**Fusion and hydration.**
1. Fuse the keyword and vector lists by `Key` with `RrfFusion` (`k = Hybrid:RrfK`, default 60).
2. Take the top `top`.
3. Yield `VectorSearchResult<FilingChunkRecord>(record, rrfScore)`, so higher is better, consistent with the contract. Records come from the two candidate sets, so no extra round trip is needed.
4. `IVectorSearchable.SearchAsync(searchValue, …)` uses `websearch_to_tsquery(searchValue)`.
5. `IKeywordHybridSearchable.HybridSearchAsync(searchValue, keywords, …)` uses `websearch_to_tsquery(string.Join(' ', keywords))` for the keyword side.

RRF scores are about 0.01–0.03, not comparable to cosine; document that.

```csharp
namespace SECFilingsAgenticRag.RetrievalPipeline.Fusion;

public static class RrfFusion
{
    /// rankings: each list is ordered best-first. Returns ids ordered by fused score (desc), ties broken by
    /// best single rank, then by first-seen order (deterministic).
    public static IReadOnlyList<(Guid Id, double Score)> Fuse(IEnumerable<IReadOnlyList<Guid>> rankings, int k = 60);
}
// score(id) = Σ over lists containing id of 1.0 / (k + rank), rank is 1-based.
```

### 6.3 Reranking (decorator on the built-in interface)

```csharp
// Custom (no built-in fits): MEVD has no reranking decorator; implements the built-in IVectorSearchable.
public sealed class RerankingVectorSearch(IVectorSearchable<FilingChunkRecord> inner, IChatClient chat,
    IOptions<RetrievalOptions> options, ILogger<RerankingVectorSearch> logger) : IVectorSearchable<FilingChunkRecord>
{
    // SearchAsync<TInput>(searchValue, top, opts, ct):
    //  1. searchValue must be a string (the question); otherwise pass through to inner unchanged.
    //  2. candidates = inner.SearchAsync(searchValue, max(Rerank.CandidatePool, top), opts)
    //  3. var r = await chat.GetResponseAsync<Ranking>(prompt, new ChatOptions { Temperature = 0, MaxOutputTokens = 200 }, ct);
    //     public sealed record Ranking(int[] Ids);          // MEAI structured output: JSON schema + typed deserialization
    //  4. map ids back; drop unknown/duplicate ids; APPEND missing candidates in original order; take top
    //  5. yield VectorSearchResult(record, 1.0 - i / n)      // synthetic, higher is better
}
```

- The `IChatClient` is the registered one, or a dedicated client when `Retrieval:Rerank:Model` is set (§4.2). Never hard-code a model name.
- **Prompt:** give each candidate a **short integer id** (1..n), not the GUID; small models copy short numbers far more reliably. Truncate each passage to about 700 characters, with `Ticker FY Section` as a header line. System instruction: *"Rank the passages by how well they answer the question. Include only ids you were given, best first."*
- **Structured output is built in.**
  - `GetResponseAsync<T>` sets a JSON schema on `ChatResponseFormat` (`useJsonSchemaResponseFormat` defaults to `true`) and deserializes the reply. See https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.chatclientstructuredoutputextensions.getresponseasync. There's no hand-written JSON extraction.
  - **Small-model fallback:** if the model or provider rejects schema-constrained output, retry once with `useJsonSchemaResponseFormat: false`. MEAI then instructs the model with the schema in the prompt. Record which mode works for your Ollama model.
  - A top-level array schema may fail with some services (Learn remark), which is why `Ranking` wraps the ids in an object.
- **On any failure** (exception, timeout, `JsonException`, empty result): log a warning and return the original order trimmed to `top`. Retrieval must never fail because the reranker misbehaved.

### 6.4 Multi-query (decorator on the built-in interface)

```csharp
// Custom (no built-in fits): MEVD has no query-rewriting decorator; implements the built-in IVectorSearchable.
public sealed class MultiQueryVectorSearch(IVectorSearchable<FilingChunkRecord> inner, IChatClient chat,
    IOptions<RetrievalOptions> options, ILogger<MultiQueryVectorSearch> logger) : IVectorSearchable<FilingChunkRecord>
{
    // var v = await chat.GetResponseAsync<QueryVariants>(prompt, new ChatOptions { Temperature = 0 }, ct);
    // public sealed record QueryVariants(string[] Queries);
    // queries = [question] + v.Result.Queries (dedupe, case-insensitive, max MultiQuery.Variants)
    // run inner for each query (same options + top; sequential is fine), fuse by Key with RrfFusion, keep first-seen record per Key
}
```

- Model: `Retrieval:MultiQuery:Model` if set, else the registered `IChatClient`.
- On failure (including the structured-output fallback failing too), only the original query runs.
- The prompt asks for **search-style rewrites** that keep company names, tickers and years, and that expand financial jargon (e.g. "capex" → "capital expenditures").

### 6.5 Composition (`AddSecFilingsRetrieval`)

```
keyed IVectorSearchable<FilingChunkRecord> "Vector" = VectorStoreCollection<Guid, FilingChunkRecord>   (built-in, Phase 03)
keyed IVectorSearchable<FilingChunkRecord> "Hybrid" = PostgresHybridSearch                              (new)
unkeyed IVectorSearchable<FilingChunkRecord> = factory:
    s = keyed(Strategy)
    if MultiQuery.Enabled → s = new MultiQueryVectorSearch(s, chatClientFor(MultiQuery.Model), ...)
    if Rerank.Enabled     → s = new RerankingVectorSearch(s, chatClientFor(Rerank.Model), ...)
```

- Validation (Phase 03's validator, relaxed): `Strategy` must be a registered key (`IServiceProviderIsKeyedService`). Rerank and MultiQuery are now allowed.
- `RetrievalOptions.Describe()` → `Vector`, `Hybrid`, `Vector+Rerank`, `Hybrid+MultiQuery+Rerank`, … That string is what `/api/search` reports and what the eval matrix asserts.

### 6.6 Evals console (`evals/SECFilingsAgenticRag.Evals`)

- Console app with a generic host.
- References `SECFilingsAgenticRag.Common`, `SECFilingsAgenticRag.RetrievalPipeline` and `SECFilingsAgenticRag.AgentServer`.
- Packages:
  - `Npgsql` (+ `Pgvector`);
  - `Microsoft.Extensions.Hosting`;
  - `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.AI.Evaluation.Quality`, `Microsoft.Extensions.AI.Evaluation.Reporting`;
  - `Microsoft.Extensions.AI.OpenAI` (for the judge).
- It registers `NpgsqlDataSource` itself: `new NpgsqlDataSourceBuilder(cs).UseVector().Build()`. In this project the eval app **is** the host, so it owns the connection, consistent with docs/02 rule 3.

**Commands**

| Command | Purpose |
|---|---|
| `dataset-helper [--ticker AAPL]` | Prints, per indexed document, the list of distinct `Section` values with chunk counts, plus 2 sample chunk snippets per section. It exists to help **author `golden.json` from real data**. |
| `retrieval [--k 5,10] [--configs Vector,Hybrid,Vector+Rerank,Hybrid+Rerank]` | Runs the retrieval matrix and writes `results/<yyyyMMdd-HHmmss>-retrieval.md`. |
| `answers [--agent standard-rag,agentic-rag] [--limit 10]` | Runs answer-quality evals and writes `results/<ts>-answers.md` plus a disk store for `dotnet aieval report`. |

**Preferred: run the evals as AppHost resources.** Aspire then injects the connection strings and model parameters automatically, which keeps the run agent-executable (see the `evals-*` resources below):

```powershell
aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1
aspire resource evals-retrieval start          # explicit-start resource
aspire wait evals-retrieval                     # check `aspire wait --help` for the flag that waits for Finished
aspire logs evals-retrieval                     # prints the matrix table
```

**Connection strings when running outside Aspire** (fallback).
- Aspire generates the Postgres port and password. Get the `secfilings` connection string from `aspire describe secfilings`. If the CLI doesn't print it, copy it from the dashboard (resource `secfilings` → *Connection string*); that's an optional human step. Then:
  ```powershell
  dotnet user-secrets --project evals/SECFilingsAgenticRag.Evals set "ConnectionStrings:secfilings" "<value>"
  ```
- `ConnectionStrings:ollama` defaults to `Endpoint=http://localhost:11434` in `appsettings.json`.
- **Also** add the project to the AppHost so connection strings are injected automatically:
  ```csharp
  builder.AddProject<Projects.SECFilingsAgenticRag_Evals>("evals-retrieval")
      .WithArgs("retrieval")
      .WithReference(secfilingsDb).WithReference(ollama)
      .WithAIModels(chatModel, embeddingModel, embeddingDimensions)   // same parameters as api/indexing
      .WaitFor(secfilingsDb)          // run it after indexing; it starts only on demand
      .WithExplicitStart();
  ```
  Add a similar `evals-answers` resource. Start them with `aspire resource evals-retrieval start`; clicking ▶ in the dashboard is the optional human path. Verify that `WithExplicitStart` exists in the installed Aspire version. If it doesn't, skip the AppHost resources, use the fallback connection-string path above, and document it.
- The evals call `IVectorSearchable<FilingChunkRecord>` and the named `AIAgent`s **in-process**, so no wire JSON is involved. If you add a path over HTTP (`/api/search`, or AG-UI via `AGUIChatClient`), deserialize with the server's options.

**Golden dataset** (`datasets/golden.json`).

> **IMPORTANT:** build it from the actually indexed chunks.
> - Run `dataset-helper`. Query Postgres for real section names and phrases.
> - Write questions whose answers you can **see** in specific chunks.
> - Do not write questions from memory about these companies; the filings may differ from what you remember.
>
> Target about 25 questions: about 5 per company, plus 3–5 cross-company questions with multiple `expected` entries. Mix risk factors, business description, segments, competition, human capital, legal proceedings, and MD&A revenue drivers.

```json
{
  "version": 1,
  "questions": [
    {
      "id": "aapl-supply-chain-01",
      "question": "What supply chain risks does Apple describe?",
      "filter": { "tickers": ["AAPL"] },
      "expected": [
        { "documentId": "AAPL/10-K/0000320193-25-000079", "sectionPrefix": "Item 1A", "mustContainAny": ["single source", "outsourcing partners"] }
      ],
      "referenceAnswer": "Apple relies on outsourcing partners, many located outside the U.S., and some components come from single or limited sources...",
      "expectedToolCalls": [ { "name": "search_filings", "arguments": { "ticker": "AAPL" } } ]
    }
  ]
}
```

- `filter` is optional. If present, it maps to `FilingFilters.Build(tickers, formTypes, fiscalYears, excludeInstructionLike: true)`.
- `expectedToolCalls` is optional and used only for `agentic-rag` tool-use evals. Arguments are a **subset** match: list only what must be there, e.g. the ticker. Cross-company questions list one call per company.
- Use the **real** `documentId` values from the database. The ones above are illustrative.

**Relevance rule** (deterministic). A retrieved chunk *satisfies* expectation `e` when all of these hold:
- `chunk.DocumentId == e.documentId` (ordinal);
- `e.sectionPrefix` is null **or** `chunk.Section.StartsWith(e.sectionPrefix, OrdinalIgnoreCase)`;
- `e.mustContainAny` is empty **or** `chunk.Text` contains at least one phrase (`OrdinalIgnoreCase`).

A chunk is *relevant* to a question if it satisfies at least one expectation.

**Metrics** (per question, then averaged; retrieve `Top = max(k)` once with the shared `search.SearchFilingsAsync(...)` from Phase 03, and evaluate every k on that list). These are **custom** and justified: neither MEAI.Evaluation nor MAF evaluation ships Hit@k, MRR or Recall@k (they're listed in the docs/02 custom-types table).
- `Hit@k` = 1 if any relevant chunk is in the top k, else 0.
- `MRR@10` = 1 / (1-based rank of the first relevant chunk within the top 10), else 0.
- `Recall@k` = (number of expectations satisfied by ≥ 1 chunk in the top k) / (number of expectations).
- Latency: p50 and p95 of a `Stopwatch` around each search.

**Matrix runner: proof that the system is open for extension.**
- For each configuration variant, `EvalServiceFactory` builds a fresh `ServiceProvider` from the base configuration plus in-memory overrides:
  ```csharp
  var cfg = new ConfigurationBuilder()
      .AddConfiguration(baseConfiguration)
      .AddInMemoryCollection(new Dictionary<string, string?> {
          ["Retrieval:Strategy"] = "Hybrid",
          ["Retrieval:Rerank:Enabled"] = "true",
      })
      .Build();
  services.AddSingleton(dataSource);
  services.AddSecFilingsAI(cfg).AddSecFilingsVectorStore(cfg, EmbeddingKeys.Query).AddSecFilingsRetrieval(cfg);
  ```
- **No `if (strategy == ...)` code is allowed in the eval app.** It only changes configuration.
- Default variants:

  | Name | Overrides |
  |---|---|
  | `Vector` | `Strategy=Vector` |
  | `Hybrid` | `Strategy=Hybrid` |
  | `Vector+Rerank` | `Strategy=Vector`, `Rerank:Enabled=true` |
  | `Hybrid+Rerank` | `Strategy=Hybrid`, `Rerank:Enabled=true` |
  | `Hybrid+MultiQuery+Rerank` | opt-in through `--configs` |

- Assert that `RetrievalOptions.Describe()` equals the variant name, and fail loudly if it doesn't.

**Report** (`MarkdownReport`). Start with a header recording the date, models, corpus size (document and chunk counts) and question count. Then:

```
| Config | Hit@5 | Hit@10 | MRR@10 | Recall@5 | Recall@10 | p50 ms | p95 ms |
|---|---|---|---|---|---|---|---|
| Vector | 0.76 | 0.88 | 0.61 | 0.70 | 0.84 | 180 | 260 |
```

After the table, list the "Misses" per config: question ids with Hit@10 = 0. Print the same table to the console.

**Answer evals** (`answers` command):
- `--agent standard-rag,agentic-rag` selects the agents. Build the host with `AddSecFilingsAgents()` (MAF hosting) and resolve each agent by name.
- **Decorate the unkeyed `IVectorSearchable<FilingChunkRecord>`** with `RecordingVectorSearch`. It's an eval-only decorator, justified because no built-in captures what was retrieved. It stores every record returned during the current question, which gives the grounding context for the Groundedness evaluator whichever way retrieval happened (context provider or tool). Register it **after** `AddSecFilingsRetrieval`, wrapping the registered instance.
- For each question:
  1. `var session = await agent.CreateSessionAsync();`
  2. `var response = await agent.RunAsync(q.Question, session);`
  3. Take `response.Text` as the answer.
  4. Count the tool calls as `FunctionCallContent` items in `response.Messages`.
  5. Count citations as `[n]` / `[TAG]` markers in the text.
- Judge configuration:
  - `Evals:Judge` → build a **separate** `IChatClient` and wrap it as `new ChatConfiguration(judgeClient)`. The model comes from `Evals:Judge:Model`, never a literal.
  - If Common exposes a reusable factory (e.g. `ChatClientFactory.Create(ChatModelOptions, ...)`), use it.
  - Prefer registering extra clients with the built-in keyed registration (`services.AddKeyedChatClient(key, sp => …)`) where a DI container exists.
  - If not, **add** `public static class ChatClientFactory { public static IChatClient Create(ChatModelOptions options, string? ollamaEndpoint, HttpClient? httpClient = null); }` to `SECFilingsAgenticRag.Common.AI`, refactor `AddSecFilingsAI` to use it (no behavior change), and record this in the notes.
- Evaluators (verified on Microsoft Learn, namespace `Microsoft.Extensions.AI.Evaluation.Quality`; each returns a `NumericMetric` from 1 to 5):
  - `RelevanceEvaluator`: no extra context.
  - `GroundednessEvaluator`: needs `new GroundednessEvaluatorContext(groundingContext)`, where `groundingContext` = the recorded chunk texts joined.
  - `CompletenessEvaluator`: needs `new CompletenessEvaluatorContext(groundTruth)` = `q.referenceAnswer`.
  - Optional: `RetrievalEvaluator` with `RetrievalEvaluatorContext(retrievedChunks)`. Verify the constructor.
- Use the reporting package for caching and storage:
  ```csharp
  ReportingConfiguration reporting = DiskBasedReportingConfiguration.Create(
      storageRootPath: Path.Combine(resultsPath, "store"),
      evaluators: [new RelevanceEvaluator(), new GroundednessEvaluator(), new CompletenessEvaluator()],
      chatConfiguration: new ChatConfiguration(judgeClient),
      enableResponseCaching: true,
      executionName: $"answers-{timestamp}");
  await using ScenarioRun run = await reporting.CreateScenarioRunAsync($"{mode}.{q.Id}");
  EvaluationResult result = await run.EvaluateAsync(messages, response, additionalContext: [groundingCtx, completenessCtx]);
  ```
  Verify the exact parameter names and overloads with Microsoft Learn ("Tutorial: Evaluate response quality with caching and reporting").
- The judge caches its responses on disk through the reporting library's **built-in** response cache (`enableResponseCaching: true`), so re-running an unchanged eval is cheap. There's no custom cache. The **system under test's** answers are not cached; they come from the agents.
- Markdown report: a per-mode table of average Relevance / Groundedness / Completeness, average tool calls, percentage of answers with ≥ 1 citation, and average latency. Below that, per-question rows.
- HTML report: add a local tool manifest and document the commands:
  ```powershell
  dotnet new tool-manifest   # if .config/dotnet-tools.json doesn't exist
  dotnet tool install Microsoft.Extensions.AI.Evaluation.Console
  dotnet aieval report --path evals/results/store --output evals/results/report.html
  ```
  Verify the tool id and command flags.
- If `Evals:Judge:Provider` is `Ollama`, print a **warning**: *"Local 4B judges are unreliable; treat these scores as indicative only."*

**Tool-use evals (`agentic-rag`): MAF's built-in agent evaluation.** See https://learn.microsoft.com/agent-framework/agents/evaluation. In .NET it builds on Microsoft.Extensions.AI.Evaluation, and the core types are in `Microsoft.Agents.AI`.

```csharp
var withTools = golden.Questions.Where(q => q.ExpectedToolCalls is { Count: > 0 }).ToList();
AgentEvaluationResults toolResults = await agent.EvaluateAsync(
    withTools.Select(q => q.Question).ToArray(),
    new LocalEvaluator(
        EvalChecks.ToolCalledCheck(FilingTools.SearchToolName),   // the agent searched at all
        EvalChecks.ToolCallArgsMatch()),                          // name + subset-of-args match against expectedToolCalls
    expectedToolCalls: withTools.Select(q => q.ExpectedToolCalls!
        .Select(c => new ExpectedToolCall(c.Name, c.Arguments)).ToArray()).ToArray());
// report toolResults.Passed / toolResults.Total, and the per-item failures
```

- **Verify on Learn** (`ExpectedToolCall` and `EvalChecks` API pages):
  - the `ExpectedToolCall` constructor's argument parameter (the getting-started sample shows only `new ExpectedToolCall("get_weather")`);
  - the exact name of the args-match check: `EvalChecks.ToolCallArgsMatch` (the Python name is `tool_call_args_match`).

  Record what exists. **Fallback:** if there's no .NET args-match check, write one `FunctionEvaluator.Create("ticker_matches", (EvalItem item) => …)` that inspects the `FunctionCallContent` arguments. That's still on the built-in `LocalEvaluator`.
- Report a `Tool use` section: pass rate per check, and the failing question ids.

**Answer quality through the same entry point (verify, with fallback).**
- Learn states that MEAI quality evaluators "work without any adapter" in `agent.EvaluateAsync(queries, new CompositeEvaluator(new RelevanceEvaluator(), new GroundednessEvaluator(), new CompletenessEvaluator()), chatConfiguration: new ChatConfiguration(judgeClient), expectedOutput: referenceAnswers)`.
- **Use that path if** you can supply the per-item `GroundednessEvaluatorContext` (the recorded chunks) and keep the judge response cache.
- **Otherwise (expected)**, keep the `ReportingConfiguration` / `ScenarioRun` path above. It's the one that has the built-in disk cache and the `dotnet aieval report` store.
- Record the decision in the notes.

---

## 7. Implementation steps

1. **Read context.** Read the notes files, `CLAUDE.md`, `FilingChunkSchema`, `FilingFilters` and `AddSecFilingsRetrieval`.
   *Check:* you can state the real table and column names.
2. **`RrfFusion`.**
   *Check:* `dotnet build`.
3. **Verify MEAI structured output** (`GetResponseAsync<T>`) against your Ollama model: schema mode, then the `useJsonSchemaResponseFormat: false` fallback. Record which works.
   *Check:* in step 9 with `Retrieval:Rerank:Enabled=true`, `/api/search` reports `…+Rerank` and the AgentServer log shows no rerank-fallback warning. Record the mode that worked.
4. **`FilingChunkTableInfo` + `HybridSchemaInitializer`.**
   *Check:* start AgentServer twice (`aspire stop; aspire start; aspire wait api`, twice) with no migration error in `aspire logs api`, then `\d+` on the chunk table shows the generated full-text column and its index. This and step 5's check are the only guards on the migration and the keyword SQL until Phase 09.
5. **`PostgresHybridSearch`** (built-in vector side + keyword SQL + in-memory compiled filter), with the vector-only fallback when the migration couldn't run.
   *Check:* in step 9 with `Retrieval__Strategy=Hybrid`, pick a rare exact term from one chunk with psql (a product or subsidiary name) and search for it: that chunk is in the top 5, and `strategy` is `Hybrid`.
6. **`RerankingVectorSearch`**, including the fallback to the original order on a garbage or failed model response.
   *Check:* `dotnet build`.
7. **`MultiQueryVectorSearch`**, fusing and deduping by `Key`.
   *Check:* `dotnet build`.
8. **Composition in `AddSecFilingsRetrieval`**.
   *Check:* `dotnet build`; step 9 shows the composed `strategy` name for each config you set.
9. **AgentServer sanity.** Set `Retrieval__Strategy=Hybrid` on the `api` resource temporarily, via `WithEnvironment` or `src/SECFilingsAgenticRag.AgentServer/appsettings.Development.json`. Then `aspire stop; aspire start; aspire wait api`.
   *Check:*
   ```powershell
   (Invoke-RestMethod -Method Post http://localhost:5180/api/search -ContentType 'application/json' -Body '{"query":"Item 1A risk factors","top":3}').strategy
   ```
   It returns `Hybrid`. Revert to `Vector` as the default afterwards.
10. **Evals project skeleton** with the `dataset-helper` command.
    *Check:* it prints the sections for the 5 documents.
11. **Author `golden.json`** (about 25 questions) using the `dataset-helper` output and SQL.
    *Check:* the loader validates that every `documentId` exists in the DB, and the run fails with a clear message if one doesn't.
12. **`retrieval` command, metrics and report.**
    *Check:* the table prints and the file is written.
13. **`answers` command** (recording decorator, evaluators, disk reporting), plus the **tool-use eval** for `agentic-rag` (`agent.EvaluateAsync` + `LocalEvaluator`).
    *Check:* `--limit 3 --agent standard-rag` completes and writes a report. `--agent agentic-rag` adds a `Tool use` section with a pass rate.
14. **Optional:** AppHost `evals-*` resources with `WithExplicitStart`.
15. **Docs.** Write a "How to run evals" section in the notes file. Commit one `evals/results/sample-retrieval.md` from a real run.

---

## 8. Tests

No automated tests in this phase. All unit and integration tests are deferred to [Phase 09](phase-09-tests.md), which specifies this phase's `RetrievalPipeline.Tests` (RRF, rerank, multi-query, composition, hybrid schema and search) and `Evals.Tests` (metric arithmetic). The evals in §6 are a product feature of this phase, not tests. This phase is verified by `dotnet build`, the eval runs and the run-time checks in §9.

## 9. Acceptance criteria

Run these from the repo root. Start the stack headlessly first, with `aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1; aspire wait api`. The evals commands below use the outside-Aspire connection string (user-secrets). Equivalently, run them as AppHost resources: `aspire resource evals-retrieval start`, then `aspire logs evals-retrieval`.

```powershell
dotnet build SECFilingsAgenticRag.slnx                      # 0 errors, 0 new warnings

dotnet run --project evals/SECFilingsAgenticRag.Evals -- dataset-helper --ticker AAPL
# → lists sections for the AAPL 10-K with chunk counts

dotnet run --project evals/SECFilingsAgenticRag.Evals -- retrieval
# → prints a table with rows Vector, Hybrid, Vector+Rerank, Hybrid+Rerank and columns Hit@5 … p95 ms
# → writes evals/results/<timestamp>-retrieval.md

dotnet run --project evals/SECFilingsAgenticRag.Evals -- answers --agent standard-rag,agentic-rag --limit 5
# → per-mode averages for Relevance, Groundedness, Completeness; writes evals/results/<timestamp>-answers.md

dotnet aieval report --path evals/results/store --output evals/results/report.html
# → HTML report opens in a browser
```

- In AgentServer, setting `Retrieval:Strategy` to `Hybrid` (or `Retrieval:Rerank:Enabled=true`) and restarting (`aspire stop; aspire start; aspire wait api`) changes `strategy` in the `POST /api/search` response to `Hybrid` (or `…+Rerank`). Check it with the `Invoke-RestMethod` command from step 9. **No code change is needed.**
- `$env:Evals__Judge__Model = "<other model>"; dotnet run --project evals/SECFilingsAgenticRag.Evals -- answers --limit 1` uses the overridden judge model; the report header shows the judge model name. Removing `Evals:Judge:Model` from `appsettings.json` with no environment variable makes the app fail at startup with an options-validation error.
- `golden.json` has at least 20 questions, covers all 5 tickers, and every `documentId` exists in the database.
- Hybrid search works on a database that was indexed **before** this phase, with no re-index needed, because the generated column backfills.

---

## 10. Gotchas

- **Column names.** Never hard-code them; the writer currently uses `content`, `embedding` and so on. Read them from the collection definition (`StorageName ?? Name`) and quote them. Postgres folds unquoted identifiers to lower case, which matches the writer's names.
- **Chunk keys aren't stable.** `VectorStoreWriter` assigns random keys on every re-index, so `golden.json` must reference `documentId` + `sectionPrefix` / `mustContainAny`, never `Key`. In-run dedupe by `Key` is fine.
- **The generated column rewrites the table** on first `ALTER`. That's fine for about 2k rows. The GIN index makes the `@@` operator fast.
- `websearch_to_tsquery` tolerates user syntax (quotes, `-`, `or`); `to_tsquery` throws on raw user text. Use the websearch variant.
- **Null array parameters** need an explicit `NpgsqlDbType`, or Postgres can't infer the type for `@tickers::text[] IS NULL`.
- The **vector side must go through the collection's `SearchAsync` with a string**, so the store's keyed `query` generator adds the `search_query: ` prefix. A raw, unprefixed embedding silently degrades results.
- **RRF scores are tiny** (about 0.016 for rank 1 with k = 60). Don't threshold them like cosine similarity.
- **qwen3 "thinking" and structured output.** Reasoning text can break JSON deserialization. Schema-constrained output (`GetResponseAsync<T>`'s default) avoids most of it; the `useJsonSchemaResponseFormat: false` fallback and the catch-all "original order" fallback cover the rest. Don't write a JSON scraper.
- **Built-in first.** No reranker or rewriter interfaces, no JSON helper, no custom cache. If one seems necessary, write down which built-in failed and why.
- **Reranking on CPU is slow**: about 20 passages × 700 characters is a big prompt, so it can take 30–90 s locally. Keep `CandidatePool` at 20, and truncate the passages. The matrix run with `+Rerank` over 25 questions can take a long time locally. Print progress.
- **Default resilience timeouts.** If the evals host uses `IHttpClientFactory` with resilience defaults, LLM calls time out at 10–30 s. Build the Ollama/judge HttpClient with explicit long timeouts, as `AddSecFilingsAI` does. If you do attach a resilience pipeline to the judge client:
  - retries × attempt timeout ≤ total timeout (e.g. `Evals:Judge:TimeoutSeconds` = 300, one retry → total ≥ 600 s);
  - circuit-breaker `SamplingDuration` ≥ 2 × attempt timeout.
- **LLM judges are model-sensitive.** The Quality evaluators are tuned for GPT-4o-class models. A 4B local judge often returns unparseable or meaningless scores. Prefer the cloud judge and label local results as indicative.
- **Don't evaluate on what you trained your intuition on.** Write the dataset from real chunks, and keep `referenceAnswer` short and factual.
- **Model names in code.** The reranker, rewriter and judge models are all configuration. A review grep must find no model-name literals in `src/` or `evals/` C# files, comments included. No output is expected:
  `Get-ChildItem src,evals -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Where-Object FullName -notmatch '\\(obj|bin)\\' | Select-String -Pattern 'qwen|nomic|llama|gpt-'`
- **Aspire ports and passwords change.** When running evals outside Aspire, refresh the `secfilings` connection string (`aspire describe secfilings`, or the dashboard) if the connection is refused. Running the evals as AppHost resources avoids this entirely.

### How to read the results (for the learner)

- Compare **Hit@10 vs Hit@5**.
  - A big gap means the right chunk is *found* but ranked too low. Reranking should close it.
  - A low Hit@10 means *recall* is the problem. Hybrid or multi-query should help.
- **MRR** rises when relevant chunks move to rank 1–2. That matters most for answer quality, because the LLM pays most attention to the first passages.
- **Hybrid vs Vector.**
  - Hybrid usually wins on questions with exact terms (item numbers, product names, legal terms).
  - Vector can win on paraphrased, conceptual questions.
- **Rerank.** Expect better MRR and Hit@5 at a large latency cost (look at p95). That's the classic quality vs latency trade-off you should be able to discuss.
- **Answer evals.**
  - High Relevance with low Groundedness = the model answered plausibly **but not from the filings** (hallucination risk).
  - `agentic-rag` often wins on cross-company questions, because it searches per company.
  - With small models it may also skip searching; check the "avg tool calls" column.
- With about 25 questions, a difference of ±0.04 in Hit@k is **one question**. Don't over-interpret small deltas. Look at the misses list.

---

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

- `dotnet build SECFilingsAgenticRag.slnx` is clean (0 errors, 0 warnings).
- All acceptance criteria are met. One real `evals/results/sample-retrieval.md` is committed-ready (not committed).
- Write `docs/notes/phase-06-notes.md` covering:
  - the actual table and column names;
  - the package versions used;
  - where the migration runs and why;
  - any contract additions (e.g. `ChatClientFactory`, the `RecordingVectorSearch` pattern), each with its "why no built-in" line;
  - which structured-output mode works for each model (schema vs `useJsonSchemaResponseFormat: false`);
  - your eval results summary (paste the matrix);
  - known limitations.
- Add to the `## Commands & operating notes` section of your phase notes the eval commands and the judge configuration (user-secrets).
- Suggested commit message:
  ```
  feat(retrieval): hybrid search, LLM reranking, multi-query + evaluation harness

  - PostgresHybridSearch (IVectorSearchable + IKeywordHybridSearchable; Postgres FTS + built-in vector search, RRF) with idempotent generated tsvector migration
  - RerankingVectorSearch and MultiQueryVectorSearch decorators (MEAI structured output)
  - config-only composition: Retrieval:Strategy, Retrieval:Rerank, Retrieval:MultiQuery
  - SECFilingsAgenticRag.Evals: golden dataset, Hit@k/MRR/Recall@k matrix, M.E.AI.Evaluation answer quality, MAF agent.EvaluateAsync tool-use checks
  ```
