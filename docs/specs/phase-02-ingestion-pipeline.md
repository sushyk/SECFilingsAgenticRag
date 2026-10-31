# Phase 02: Indexing pipeline (class library and Azure Function)

> Depends on: Phases 00, 01 | Produces: Common wire contract `SECFilingsAgenticRag.Common.Ingestion` (+ `IndexingJson`), `SECFilingsAgenticRag.IngestionPipeline` (host-agnostic library), `SECFilingsAgenticRag.IngestionPipeline.Functions` (HTTP-triggered Azure Function), AppHost `indexing` resource + `reindex`/`reindex-force` commands, `scripts/Invoke-Indexing.ps1`, tests | Est. size: L

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
- **Authority.** This spec plus [`docs/01-architecture-and-design.md`](../01-architecture-and-design.md) are authoritative. If code already on disk conflicts with a contract here, **the code on disk wins**: keep it and record the deviation in `docs/notes/phase-02-notes.md`.
- **Read first:** `CLAUDE.md`, `docs/notes/phase-00-notes.md` and `docs/notes/phase-01-notes.md`.
- **Verify unfamiliar or preview APIs** before coding against them.
  - Use the Microsoft Learn MCP tools if available: `microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`.
  - Otherwise use the package XML docs or compiler errors.
  - **Must verify:**
    - `Microsoft.Extensions.DataIngestion*` (preview): `IngestionDocumentReader.ReadAsync` overloads, `IngestionDocument`, `HeaderChunker`, `IngestionChunkerOptions`, `IngestionChunk<T>` (constructor, `Content`, `Context`, `Metadata`), `IngestionChunkProcessor<T>`, **`IngestionPipeline<T>`** (constructor, `ChunkProcessors`, `ProcessAsync` overloads, `IngestionResult` members, one `WriteAsync` per document?), **`VectorStoreWriter<T>`** / `VectorStoreWriterOptions`.
    - `MarkdownReader` (package `Microsoft.Extensions.DataIngestion.Markdig`).
    - `Microsoft.ML.Tokenizers`.
    - Aspire 13: `AddAzureFunctionsProject`, `WithHostStorage`, `WithHttpCommand(path, displayName, endpointName, commandName, commandOptions)`, `HttpCommandOptions` (including `ResultMode`).
    - Azure Functions isolated worker on .NET 10.
- **Central package management (CPM).**
  - Versions go in `Directory.Packages.props`.
  - Pick the latest stable version, and a preview only where no stable version exists.
  - Find versions with `dotnet package search <id> --exact-match --prerelease`.
  - `Microsoft.Extensions.DataIngestion.*` and `Microsoft.ML.Tokenizers.Data.Cl100kBase` are **preview-only**, so use their latest previews.
  - **After every scaffold** (`dotnet new classlib`, `func init`): strip each `Version="..."`, move it to `Directory.Packages.props`, and delete `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` if `Directory.Build.props` sets them. Otherwise you get **NU1008**.
  - New `Aspire.*` packages (`Aspire.Hosting.Azure.Functions`, `Aspire.Npgsql`, `Aspire.Azure.Storage.Blobs`) use the **same** version as the existing Aspire packages and the AppHost SDK attribute.
- **Platform.** Windows 11 with **PowerShell 7.3+ (`pwsh`)**, which is required for the native-argument quoting in the `docker exec … psql` checks.
- **Agent-executable checks.** Use `aspire start` (background), `aspire wait <res>`, `aspire describe [<res>]`, `aspire logs <res>`, `aspire resource <res> <command>` and `aspire stop`. The dashboard is an optional human check only.
- Match the existing code style. Don't change Phase 00/01 public contracts unless unavoidable, and document it if you do.
- **Do NOT `git commit`.** The user commits.
- Ask nothing. Decide and document.
- **No model names in C#.** Models, dimensions and endpoints come from configuration (§6.8).

## 1. Goal and why

This is the **indexing phase of RAG**. It turns each raw 10-K into many small, searchable pieces: read the filing → clean the HTML → split it into chunks → **enrich (clean) each chunk** → embed the cleaned text → store the cleaned text, its vector and metadata in pgvector, using MEDI's `VectorStoreWriter<string>`. The order follows Microsoft's RAG data pipeline (chunk → enrich → embed → persist) from [Design and develop a RAG solution](https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide). We implement the guide's **"clean chunks"** half of the [chunk enrichment phase](https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-enrichment-phase). LLM-generated metadata (the "augment" half) is deferred for cost reasons; see Phase 14. After this phase, Postgres holds a few thousand chunks, each tagged with ticker, fiscal year, section and source URL.

The logic lives in a **host-agnostic class library**, `SECFilingsAgenticRag.IngestionPipeline`. An Azure Function exposes it over HTTP (`POST /api/index`), but the library knows nothing about Functions. Phase 09's integration test proves this by running the library from a plain generic host. Any host (CLI, web app, Function) can index.

Indexing is **idempotent**. Each filing gets an *index fingerprint*: its SHA-256 plus the configuration that shapes indexed content. If the stored chunks already carry that fingerprint, the filing is skipped, so restarting Aspire takes seconds, not minutes. **The user requests indexing.** The IngestionInit job from Phase 01 only stages documents and exits; it never calls the Function. You trigger the indexing phase with `scripts/Invoke-Indexing.ps1` or the `reindex` command.

The pipeline is **Microsoft.Extensions.DataIngestion's own `IngestionPipeline<string>`**: a reader, `HeaderChunker`, chunk processors and `VectorStoreWriter<string>`. We add only a little glue.

- **Why staging.** `ProcessAsync` accepts files only (verified on Learn), so changed blobs are downloaded to a staging folder first.
- **Where per-filing metadata comes from.** `IngestionDocument` has no metadata bag, so a final chunk processor attaches the per-filing metadata.
- **Extension points (Open/Closed).** The new seams are exactly MEDI's: a new format means a new keyed reader, and a new enrichment step means a new chunk processor.

## 2. Concepts in 60 seconds

- **Chunking**: LLM context and embeddings work best on passages of a few hundred tokens. Header-aware chunking keeps "Item 1A. Risk Factors" text together and records the heading as the chunk's *section*.
- **Overlap**: repeating a few tokens between neighbouring chunks keeps sentences from being cut off without context.
- **Tokens**: models count text in tokens (about 0.75 words each). We approximate token counts with a tiktoken tokenizer. That's close enough to size chunks, even though `nomic-embed-text` uses a different tokenizer internally.
- **Inline XBRL**: 10-K HTML embeds machine-readable tags and a hidden header block full of numbers. We strip the hidden parts so they don't pollute chunks.
- **Idempotency**: MEDI's `VectorStoreWriter<string>` with `IncrementalIngestion = true` upserts a document's new chunks, then deletes its old ones. Every chunk carries the index fingerprint and the chunk count, so "N chunks with this fingerprint, and N = chunk count" means "the whole document was written with the current pipeline".
- **Chunk cleaning (enrichment)**: remove noise that doesn't change meaning, such as Markdown table pipes, page numbers and repeated running footers, so vector matches are closer. We **store only the cleaned chunk**. It is the text that gets embedded, returned, cited and shown to the LLM. Microsoft's enrichment guidance presents keeping the original as an *optional* extra field, and we skip it. Cleaning also **flags instruction-like text** (possible prompt injection) at indexing time.

## 3. Environment and prerequisites

| Tool | Check |
|---|---|
| .NET SDK 10.0.4xx | `dotnet --list-sdks` |
| Docker Desktop running | `docker info --format '{{.ServerVersion}}'` |
| Azure Functions Core Tools **v4** (the Aspire Functions integration needs it) | `func --version` → `4.x` |
| Aspire CLI 13.x | `aspire --version` |
| Ollama running, with the embedding model pulled | `ollama pull nomic-embed-text`; `ollama list` |
| Filings on disk from Phase 01 | `Get-ChildItem data\filings -Recurse -Filter *.htm` → 5 files |

## 4. Repo state at start

```
SECFilingsAgent\
├─ SECFilingsAgenticRag.slnx, global.json, Directory.Build.props, Directory.Packages.props, CLAUDE.md
├─ data\filings\{TICKER}\10-K\{accession}.htm + .metadata.json   (git-ignored)
├─ src\
│  ├─ SECFilingsAgenticRag.AppHost\       (postgres/secfilings, storage/filings, ollama, parameters, init job)
│  ├─ SECFilingsAgenticRag.ServiceDefaults\
│  ├─ SECFilingsAgenticRag.Common\
│  ├─ SECFilingsAgenticRag.IngestionPipeline\      (EMPTY classlib, created in Phase 00)
│  └─ SECFilingsAgenticRag.IngestionInit\        (DownloadFilingsStep, UploadFilingsStep, Program.cs, Edgar\)
```

### 4.1 Contracts from Phase 00 (`SECFilingsAgenticRag.Common`): copied for reference

```csharp
namespace SECFilingsAgenticRag.Common.Filings;
public sealed record FilingMetadata(string Ticker, string CompanyName, string Cik, string FormType, string AccessionNumber,
    DateOnly FilingDate, DateOnly ReportDate, int FiscalYear, string PrimaryDocument, string SourceUrl)
{ public string DocumentId => $"{Ticker}/{FormType}/{AccessionNumber}"; }

public static class FilingBlobConventions
{
    public static string BlobName(FilingMetadata m, string extension);          // "AAPL/10-K/0000320193-25-000079.htm"
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        Accession = "accession", FilingDate = "filingdate", ReportDate = "reportdate", FiscalYear = "fiscalyear",
        PrimaryDocument = "primarydocument", SourceUrl = "sourceurl", ContentSha256 = "contentsha256";
    public static IDictionary<string, string> ToBlobMetadata(FilingMetadata m, string contentSha256);
    public static FilingMetadata FromBlobMetadata(IDictionary<string, string> metadata);   // case-insensitive keys
}

namespace SECFilingsAgenticRag.Common.VectorStore;
/// Mirrors the table that MEDI VectorStoreWriter<string> writes (storage names in quotes). This phase writes it.
public sealed class FilingChunkRecord
{
    public Guid Key { get; set; }                 // "key": random Guid generated by VectorStoreWriter (NOT stable across re-index)
    public string Text { get; set; } = "";        // "content": the CLEANED chunk (stored, embedded, returned, cited); no prefix
    public string? Context { get; set; }          // "context": MEDI chunk context
    public string DocumentId { get; set; } = "";  // "documentid" (indexed) = IngestionDocument.Identifier = FilingMetadata.DocumentId
    // metadata columns = chunk.Metadata keys (FilingChunkMetadataKeys), lower-case storage names, NON-NULL on every chunk:
    public string Ticker { get; set; } = "";      // "ticker"
    public string CompanyName { get; set; } = ""; // "companyname"
    public string Cik { get; set; } = "";         // "cik"
    public string FormType { get; set; } = "";    // "formtype"
    public int FiscalYear { get; set; }           // "fiscalyear"
    public DateTime FilingDate { get; set; }      // "filingdate": DateTimeKind.Utc
    public string Section { get; set; } = "";     // "section"
    public int ChunkIndex { get; set; }           // "chunkindex"
    public int ChunkCount { get; set; }           // "chunkcount"
    public string IndexFingerprint { get; set; } = ""; // "indexfingerprint" (§6.2a)
    public bool ContainsInstructionLikeText { get; set; } // "containsinstructionliketext"; set by ChunkCleaningProcessor (§6.3)
    public string SourceUrl { get; set; } = "";   // "sourceurl"
    public string EmbeddingModel { get; set; } = ""; // "embeddingmodel"
    public ReadOnlyMemory<float>? Embedding { get; set; } // "embedding"
}
public static class FilingChunkMetadataKeys
{
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        FiscalYear = "fiscalyear", FilingDate = "filingdate", Section = "section", ChunkIndex = "chunkindex",
        ChunkCount = "chunkcount", IndexFingerprint = "indexfingerprint",
        ContainsInstructionLikeText = "containsinstructionliketext", SourceUrl = "sourceurl", EmbeddingModel = "embeddingmodel";
}
public sealed class VectorStoreOptions { public string CollectionPrefix { get; set; } = "filing_chunks"; }   // section "VectorStore"
public static class FilingChunkSchema
{
    public static VectorStoreCollectionDefinition CreateDefinition(int dimensions);   // compatible with the writer's table
    /// THE table-name accessor: the connector uses the collection name verbatim as the (quoted, lower-case) table name.
    /// Default config => "filing_chunks_nomic_embed_text_768". Always ≤ 63 chars (long names get a hash suffix).
    public static string CollectionName(VectorStoreOptions store, EmbeddingOptions embedding);
}
public static class PgVectorSetup { public static Task EnsureExtensionAsync(NpgsqlDataSource dataSource, CancellationToken ct = default); } // CREATE EXTENSION vector + ReloadTypes; idempotent
public static class VectorStoreServiceCollectionExtensions
{   // DI glue. Requires NpgsqlDataSource (UseVector()) from host. Registers a PostgresVectorStore whose EmbeddingGenerator is the
    // keyed generator `embeddingKey`, plus VectorStoreCollection<Guid, FilingChunkRecord>.
    public static IServiceCollection AddSecFilingsVectorStore(this IServiceCollection services, IConfiguration configuration, string embeddingKey);
}

namespace SECFilingsAgenticRag.Common.AI;
public static class EmbeddingKeys { public const string Document = "document", Query = "query"; }
public static class AIServiceCollectionExtensions
{   // Registers IChatClient (UseLogging/UseOpenTelemetry) and KEYED IEmbeddingGenerator<string, Embedding<float>>
    // services EmbeddingKeys.Document / EmbeddingKeys.Query: MEAI builder pipelines with one Use(...) middleware that
    // normalizes (AI:Embedding:Normalization) THEN adds the key's prefix, batches by BatchSize, and validates dimensions.
    public static IServiceCollection AddSecFilingsAI(this IServiceCollection services, IConfiguration configuration);
}
// Options: AI:Chat {Provider, Model (required, no code default), Endpoint, ApiKey, TimeoutSeconds},
//          AI:Embedding {Provider, Model, Dimensions [Range(1,2000)] (required, no code default), DocumentPrefix, QueryPrefix, BatchSize, TimeoutSeconds},
//          VectorStore {CollectionPrefix = "filing_chunks"}
// AddSecFilingsAI throws from the Add… call for an unknown provider / OpenAICompatible without ApiKey/Endpoint /
// missing ConnectionStrings:ollama; a missing Provider/Model surfaces as OptionsValidationException.
```

### 4.2 Contracts from Phase 01 (`SECFilingsAgenticRag.IngestionInit`; FYI only, this phase does not change it)

```csharp
public sealed class IngestionInitOptions { public string DataPath { get; set; } = null!; public EdgarOptions Edgar { get; set; } = new(); } // section "IngestionInit"
public sealed record UploadSummary(int Scanned, int Uploaded, int Unchanged, int Invalid);
// Program.cs calls DownloadFilingsStep.RunAsync (EDGAR → data/filings; skipped when files already exist), then UploadFilingsStep.RunAsync.
// Blobs are uploaded with ContentType "text/html" and FilingBlobConventions metadata (incl. "contentsha256").
```

### 4.3 AppHost (current, simplified)

```csharp
var postgres = builder.AddPostgres("postgres").WithImage("pgvector/pgvector").WithImageTag("pg17").WithDataVolume();
var db = postgres.AddDatabase("secfilings");
var storage = builder.AddAzureStorage("storage").RunAsEmulator(e => e.WithDataVolume().WithBlobPort(10000));
var filings = storage.AddBlobContainer("filings");
var ollama = builder.AddConnectionString("ollama");                 // "Endpoint=http://localhost:11434" (AppHost appsettings)
var chatModel = builder.AddParameter("chat-model");
var embeddingModel = builder.AddParameter("embedding-model");
var embeddingDimensions = builder.AddParameter("embedding-dimensions");
var ingestionInit = builder.AddProject<Projects.SECFilingsAgenticRag_IngestionInit>("ingestion-init").WithReference(filings).WaitFor(filings)
    .WithEnvironment("IngestionInit__DataPath", dataPath);
```

Phase 00 helper (`src/SECFilingsAgenticRag.AppHost/AIModelResourceExtensions.cs`), with a pinned signature:

```csharp
public static IResourceBuilder<T> WithAIModels<T>(this IResourceBuilder<T> builder,
    IResourceBuilder<ParameterResource> chatModel, IResourceBuilder<ParameterResource> embeddingModel,
    IResourceBuilder<ParameterResource> embeddingDimensions) where T : IResourceWithEnvironment;
// => WithEnvironment("AI__Chat__Model", chatModel), ("AI__Embedding__Model", embeddingModel), ("AI__Embedding__Dimensions", embeddingDimensions)
```

## 5. Scope

**In**
- `SECFilingsAgenticRag.Common`: the indexing **wire contract** in §6.0 (records + `IndexingJson`), an additive change.
- `SECFilingsAgenticRag.IngestionPipeline`: every type in §6.1.
- `SECFilingsAgenticRag.IngestionPipeline.Functions`: `POST /api/index`.
- AppHost: the `indexing` resource and two dashboard/CLI commands (`reindex`, `reindex-force`).
- `scripts/Invoke-Indexing.ps1`: the user's way to request indexing over HTTP (§6.10).

**Out**
- Retrieval/search (Phase 03). Full-text/`tsvector` column (Phase 06). PDF reader (Phase 10). Chunk enrichers (hook only). Durable or queue-based indexing (Phase 13). Telemetry spans (Phase 07).

## 6. Design

### 6.0 Indexing wire contract in `SECFilingsAgenticRag.Common` (namespace `SECFilingsAgenticRag.Common.Ingestion`)

The Function (server) and its clients exchange these types as JSON. The clients are `scripts/Invoke-Indexing.ps1`, the later phases (08, 10-14) and the Phase 09 end-to-end test. The types live in **Common**, so a .NET client never needs to reference `SECFilingsAgenticRag.IngestionPipeline`, which would drag in AngleSharp, MEDI and Tokenizers.

```csharp
namespace SECFilingsAgenticRag.Common.Ingestion;

public sealed record IndexingRequest(IReadOnlyList<string>? Tickers = null, bool Force = false);
public enum DocumentIndexingStatus { Indexed, Skipped, Failed }
public sealed record DocumentIndexingResult(string DocumentId, DocumentIndexingStatus Status, int ChunkCount, TimeSpan Duration, string? Error);
public sealed record IndexingReport(DateTimeOffset StartedAt, TimeSpan Duration, IReadOnlyList<DocumentIndexingResult> Documents)
{
    public int Indexed => Documents.Count(d => d.Status == DocumentIndexingStatus.Indexed);
    public int Skipped => Documents.Count(d => d.Status == DocumentIndexingStatus.Skipped);
    public int Failed  => Documents.Count(d => d.Status == DocumentIndexingStatus.Failed);
}

/// The ONE JsonSerializerOptions both sides use: camelCase, enums as strings, case-insensitive read.
public static class IndexingJson
{
    public static JsonSerializerOptions Options { get; }
    // new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }
    // Reading must ALSO accept PascalCase enum strings ("Indexed"); JsonStringEnumConverter reads case-insensitively.
}
```

The computed `Indexed`, `Skipped` and `Failed` properties also serialize. That's fine: clients ignore them on read and recompute them.

`IndexingJson.Options` must deserialize both `"status":"skipped"` and `"Skipped"`. Check 6 in §9 shows the camelCase string-enum output.

Per-document statistics (boilerplate lines removed, flagged chunks) are **structured logs**, not report fields. Phase 07 may also emit them as OpenTelemetry metrics.

### 6.1 `SECFilingsAgenticRag.IngestionPipeline`: MEDI-native, few classes

The library uses **Microsoft.Extensions.DataIngestion's own building blocks** directly:

| MEDI slot | Our type |
|---|---|
| reader | `IngestionDocumentReader` → `HtmlFilingReader` |
| chunker | `HeaderChunker` |
| chunk processors | `IngestionChunkProcessor<string>` → `ChunkCleaningProcessor`, `FilingMetadataProcessor` |
| writer | `VectorStoreWriter<string>` |
| orchestration | `IngestionPipeline<string>` |

The only orchestration we add is `IndexingService`. It lists the blobs, skips up-to-date filings, stages the changed blobs as files, and runs the pipeline.

**Verified on Microsoft Learn:**
- [`IngestionPipeline<T>.ProcessAsync`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion.ingestionpipeline-1.processasync) accepts **only files**: `IEnumerable<FileInfo>` or `DirectoryInfo` + pattern. Blobs are therefore downloaded to a staging folder first.
- [`IngestionDocument`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion.ingestiondocument) has only `Identifier` and `Sections`, with no metadata bag. Per-filing metadata is therefore attached by a chunk processor that looks the document up by its identifier.

Packages:
- `Microsoft.Extensions.DataIngestion` and `Microsoft.Extensions.DataIngestion.Markdig` (preview-only);
- `Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.Cl100kBase` (the data package is preview-only);
- `ReverseMarkdown`;
- `AngleSharp`;
- `Azure.Storage.Blobs`;
- `Microsoft.Extensions.Options.DataAnnotations`.

**No** Aspire, Functions or ASP.NET Core packages.

**Public surface (all of it):**

```csharp
namespace SECFilingsAgenticRag.IngestionPipeline;

/// Host-agnostic entry point. Uses IndexingRequest / IndexingReport from SECFilingsAgenticRag.Common.Ingestion (§6.0).
public interface IIndexingService
{
    Task<IndexingReport> IndexAsync(IndexingRequest request, CancellationToken ct = default);
}

/// MEDI reader for EDGAR 10-K HTML (Microsoft guide: chunking phase → media analysis).
/// Registered as a KEYED IngestionDocumentReader for "text/html". PDF later = one more reader + one keyed registration.
public sealed class HtmlFilingReader : IngestionDocumentReader { /* §6.2 */ }

/// MEDI chunk processor: Microsoft guide enrichment phase → "Clean your data". ONE class; its steps are private (§6.3).
public sealed class ChunkCleaningProcessor : IngestionChunkProcessor<string>
{
    public ChunkCleaningProcessor(IOptions<ChunkCleaningOptions> options, ILogger<ChunkCleaningProcessor> logger);
}

/// MEDI chunk processor, ALWAYS LAST. Built per indexing run with the run's filing metadata (§6.4).
public sealed class FilingMetadataProcessor : IngestionChunkProcessor<string>
{
    public FilingMetadataProcessor(IReadOnlyDictionary<string, StagedFiling> filingsByDocumentId, string embeddingModel);
    /// DocumentId -> number of chunks emitted in this run (0 / missing => IndexingService reports Failed("no chunks produced")).
    public IReadOnlyDictionary<string, int> ChunkCounts { get; }
}

/// One filing staged for this run: its metadata, its fingerprint and the staged file.
public sealed record StagedFiling(FilingMetadata Metadata, string IndexFingerprint, FileInfo File, string MediaType);

/// Fingerprint = source bytes + the configuration that shapes indexed content (§6.5a).
public static class IndexingFingerprint
{
    public static string Compute(string sourceSha256, IConfiguration configuration);
}

public sealed class IndexingOptions            // section "Indexing"
{
    public const string SectionName = "Indexing";
    [Range(64, 4096)] public int MaxTokensPerChunk { get; set; } = 512;
    [Range(0, 1024)]  public int OverlapTokens { get; set; } = 64;
    public string? StagingPath { get; set; }   // default: Path.Combine(Path.GetTempPath(), "secfilings-staging"); NOT part of the fingerprint
}

public sealed class ChunkCleaningOptions        // section "Indexing:Cleaning" (tuning values; code defaults allowed)
{
    public const string SectionName = "Indexing:Cleaning";
    public bool MarkdownArtifacts { get; set; } = true;
    public bool Boilerplate { get; set; } = true;
    [Range(2, 1000)] public int BoilerplateMinRepeatCount { get; set; } = 5;
    [Range(10, 400)] public int BoilerplateMaxLineLength { get; set; } = 80;
    public bool PromptInjection { get; set; } = true;
    public InstructionLikeTextMode PromptInjectionMode { get; set; } = InstructionLikeTextMode.Flag;
}
public enum InstructionLikeTextMode { Flag, Exclude }

public static class IndexingServiceCollectionExtensions
{
    /// Requires from host: BlobContainerClient ("filings"), NpgsqlDataSource (UseVector); plus AddSecFilingsAI + AddSecFilingsVectorStore(config, EmbeddingKeys.Document).
    public static IServiceCollection AddSecFilingsIndexing(this IServiceCollection services, IConfiguration configuration);
}
```

Everything else is `internal`: the HTML preparation and heading-promotion helpers, the blob listing and staging, and the up-to-date query. Add an `IValidateOptions<IndexingOptions>` that fails when `OverlapTokens >= MaxTokensPerChunk`.

### 6.2 `HtmlFilingReader` (document preparation, Microsoft guide: chunking phase → media analysis)

`HtmlFilingReader` overrides the reader's **file/stream** `ReadAsync` overload(s); verify which are abstract on Learn. It does the following:

1. **Stable identity (critical).** Create the `IngestionDocument` with `Identifier = DocumentId` (`"AAPL/10-K/0000320193-25-000079"`).
   - Derive it from the staged file's path **relative to the staging root**: `<TICKER>/<FORM>/<accession>.<ext>` with the extension removed.
   - **Ignore** whatever identifier the pipeline passes. It may be a temp path; verify what `IngestionPipeline` passes and record it.
   - `VectorStoreWriter` stores `chunk.Document.Identifier` as `documentid`, and `IncrementalIngestion` finds a document's old chunks by it. A path-based identifier would duplicate the whole corpus on every run.
   - The reader needs the staging root, so `IndexingService` passes it through an internal `StagingContext` (an `AsyncLocal` is fine). Alternatively, `IndexingService` creates the reader per run with the root.
2. **Read and decode.** Read the file as UTF-8, falling back to Windows-1252 if decoding produces many U+FFFD.
3. **Internal helper `HtmlPreparation.Prepare(html)`.** Pure, with an AngleSharp DOM:
   1. Remove `ix:header`, plus any element whose `style` contains `display:none` / `display: none`.
   2. Remove `script`, `style` and `head`.
   3. Unwrap other `ix:*` elements, keeping their text.
   4. **Normalize whitespace inside headings and cells.** Replace NBSP with a space and collapse whitespace runs. Merge adjacent inline `span`/`b`/`font` elements in the same block, so "Item 1A." and "Risk Factors" split across spans become one line.
   5. **Flatten tables:**
      - drop cells that are empty or contain only `$ % ( )` or dashes, and drop rows left empty;
      - a one-column or layout-only table (one row, or one non-empty cell per row) becomes `<p>` paragraphs;
      - keep data tables, capped at 12 cells per row, with extras joined into the last cell.
4. **ReverseMarkdown**, HTML → Markdown.
5. **Internal helper `HeadingPromotion.Promote(markdown)`.** Pure and line-based. First normalize each line: trim, strip `**`/`__`, replace NBSP, collapse whitespace. Then:
   - a line matching `^PART\s+[IVX]+\b.{0,80}$` (case-insensitive) becomes `# PART I`;
   - a line matching `^\s*ITEM\s+\d{1,2}[A-C]?\s*[.:–—-]\s*\S.{0,120}$` (case-insensitive) becomes `## Item 1A. Risk Factors`, with the separator always normalized to `.`;
   - a bare `ITEM 1A.` line followed by a short (≤ 120 chars) non-table line is merged into one heading;
   - **never** promote lines starting with `|` (table rows, where the table of contents lives), or lines longer than 140 chars.
6. Delegate to MEDI `MarkdownReader` over a `MemoryStream` of the Markdown (media type `text/markdown`), passing **`DocumentId` as the identifier**.
7. **Optional cross-check (verify, warning only).** Before step 3 strips `<ix:header>`, the reader *may* read the iXBRL document-and-entity facts:
   - `dei:TradingSymbol`
   - `dei:EntityCentralIndexKey`
   - `dei:DocumentFiscalYearFocus`
   - `dei:DocumentType`

   If they disagree with the EDGAR-API `FilingMetadata` for that filing, log a **warning**. It is not a replacement: `FilingMetadata` stays the source of truth. Verify the element names in a real filing, and record what you find. Skip this step if it complicates the reader.

This is document preparation, *not* chunk cleaning (§6.3). It needs the whole DOM, and it keeps the structure the chunker relies on.

### 6.3 `ChunkCleaningProcessor` (Microsoft guide: enrichment phase → clean chunks)

This is the **"Clean your data"** half of Microsoft's [RAG chunk enrichment phase](https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-enrichment-phase). **Only the cleaned chunk is stored.** It's what gets embedded, returned, cited and shown to the LLM, so cleaning must stay conservative: it never drops words, numbers, tickers or financial figures.

Cleaning has two layers:

| Layer | Where | Applies to |
|---|---|---|
| **Symmetric normalization** (NFKC, NBSP/zero-width/smart-quote/dash normalization, whitespace collapse, optional lowercasing) | Common keyed embedding middleware (Phase 00, `EmbeddingKeys.Document`/`Query`), `AI:Embedding:Normalization` | documents **and** queries, applied only to the text sent to the embedding model; the stored content is never lowercased |
| **Document-side cleaning** | this processor, `Indexing:Cleaning` | documents only |

The processor buffers **one document's** chunks (MEDI calls it per document; verify that). This is because boilerplate detection needs line frequencies across the document. It then runs three **private** steps, each toggled by an option:

1. **Markdown artifacts** (`MarkdownArtifacts`):
   - remove table separator rows (`|---|`);
   - replace cell pipes with `; `;
   - strip bold/italic markers and heading `#` markers, keeping the heading text;
   - `[text](url)` → `text`;
   - collapse runs of the same punctuation.

   Every word and number stays.
2. **Boilerplate** (`Boilerplate`). Remove lines that match any of:
   - `^\s*(page\s+)?\d{1,3}\s*$`;
   - `^\s*F-\d{1,3}\s*$`;
   - table-of-contents back-links (`^\s*\[?table of contents\]?.*$`);
   - **running headers and footers**: a normalized line (trimmed, whitespace-collapsed, case-folded) of ≤ `BoilerplateMaxLineLength` chars that appears in ≥ `BoilerplateMinRepeatCount` chunks of this document.

   Lines that look like headings (`#`) or contain `Item \d` are never removed.
3. **Prompt-injection flagging** (`PromptInjection`). This is the guide's "guard against prompt injection attacks" step at *indexing* time. Use case-insensitive patterns with a 250 ms match timeout:
   - `ignore (all )?(previous|prior|above) instructions`
   - `disregard .{0,40}instructions`
   - `^\s*you are now (an?|the) `
   - `system prompt`
   - `<\|im_start\|>`
   - `### ?(system|instruction)`
   - `do not tell the user`

   On a match:
   - set the chunk's `containsinstructionliketext` to true;
   - in `Exclude` mode, drop the chunk;
   - the text itself is not modified;
   - **never log the matched text.**

Yield **new** `IngestionChunk<string>(cleanedContent, chunk.Document, chunk.Context)`, carrying the `containsinstructionliketext` flag in `Metadata` (default `false`). Verify the constructor and how the get-only `Metadata` dictionary is populated. Chunks that are empty or under 20 chars after cleaning are dropped. Log one structured line per document, e.g. `Cleaned AAPL/10-K/… boilerplateLines=812 flagged=0 excluded=0`.

**Considered but deliberately NOT applied** (the guide lists these as options "to test"; record the reasoning in the notes):
- stop-word removal: transformer embeddings use function words, and "not" carries meaning;
- spelling correction: risks corrupting tickers, product names and GAAP terms;
- number-to-words and abbreviation expansion: destroys figures and terms like "MD&A";
- localization: the corpus is English-only;
- lowercasing: available in Common normalization but **off** pending the Phase 06 evaluation.

### 6.4 `FilingMetadataProcessor` (always the last chunk processor)

`IndexingService` constructs it per run with `DocumentId → StagedFiling`. It looks each chunk up by `chunk.Document.Identifier`, which is the stable `DocumentId` from §6.2.

Then it yields new chunks whose `Metadata` holds **every** key in `FilingChunkMetadataKeys`, each with a **non-null scalar** value:

| Key(s) | Value |
|---|---|
| `ticker`, `companyname`, `cik`, `formtype`, `fiscalyear`, `sourceurl` | from `FilingMetadata` |
| `filingdate` | `FilingMetadata.FilingDate` as a UTC `DateTime` |
| `section` | the private section rules below |
| `chunkindex` | renumbered 0..n-1 within the document |
| `chunkcount` | n |
| `indexfingerprint` | from `StagedFiling` |
| `containsinstructionliketext` | preserved from cleaning; default `false` |
| `embeddingmodel` | the embedding model id |

It also records `ChunkCounts[documentId] = n`.

`chunkcount` requires the total, so the processor buffers the document's chunks. **Why so strict:** `VectorStoreWriter` derives the table schema from the **first chunk's** metadata and calls `value.GetType()`. A missing key or a null value breaks the write. Use only connector-supported types: string, int, long, bool, UTC `DateTime`, and `string[]` / `int[]`. Learn's Postgres connector page lists string and int *enumerables*. Arrays are **empty, never null**, on chunks that didn't get a value (e.g. Phase 14's `keywords`), so the first-chunk-derived schema always contains the column.

**Section rules** (private; record the observed `Context` format in the notes):
1. If `chunk.Context` is non-empty, take its last non-empty heading (split on newlines and `>` or `/`) and strip `#`.
2. Otherwise, use a leading `#` heading line in the chunk text.
3. Otherwise, carry the previous chunk's section forward.
4. Otherwise, use `"(no section)"`.

Trim to 200 chars.

### 6.5 `IndexingService.IndexAsync`

```
started = now; results = []
await PgVectorSetup.EnsureExtensionAsync(dataSource)                       // vector extension (Common); the writer creates the table
blobs = list blobs in "filings" (BlobContainerClient.GetBlobsAsync, traits: Metadata, prefix per upper-cased ticker)
        // missing container (404 / ContainerNotFound) => empty list + warning; blobs without "contentsha256" => skipped + warning
staged = {}
foreach blob:
    meta = FilingBlobConventions.FromBlobMetadata(blob.Metadata); id = meta.DocumentId
    fp   = IndexingFingerprint.Compute(blob.Metadata["contentsha256"], configuration)          // §6.5a
    if !request.Force && await IsUpToDateAsync(id, fp): results += Skipped(id); continue
    file = staging/<TICKER>/<FORM>/<accession>.<ext>; download blob → file                    // stable layout
    staged[id] = new StagedFiling(meta, fp, file, mediaType: blob ContentType ?? "text/html")
try:
    foreach group in staged.Values.GroupBy(f => f.MediaType):
        reader = sp.GetKeyedService<IngestionDocumentReader>(group.Key)     // unknown media type => each file in the group Failed(NotSupported)
        metadataProcessor = new FilingMetadataProcessor(staged, embeddingOptions.Model)
        using pipeline = new IngestionPipeline<string>(reader, chunker, writer, loggerFactory: loggerFactory)
        {
            ChunkProcessors = { ...DI IngestionChunkProcessor<string> IN REGISTRATION ORDER (Phase 14 enrichers),
                                cleaningProcessor, metadataProcessor }
        }
        await foreach r in pipeline.ProcessAsync(group.Select(f => f.File), ct):   // verify IngestionResult members (DocumentId, Succeeded, Exception)
            id = map r back to DocumentId (by staged file path; verify what r exposes)
            n  = metadataProcessor.ChunkCounts.GetValueOrDefault(id)
            results += !r.Succeeded ? Failed(id, r.Exception.Message)
                     : n == 0      ? Failed(id, "no chunks produced")       // never Indexed(0)
                     :               Indexed(id, n)
finally:
    delete the staging folder for this run (best effort)
return new IndexingReport(started, elapsed, results)
```

**The writer** is MEDI's `VectorStoreWriter<string>`, used **directly** (singleton, disposed with the container):

```csharp
// The Function host calls AddSecFilingsVectorStore(config, EmbeddingKeys.Document): the registered PostgresVectorStore's
// EmbeddingGenerator is the keyed "document" MEAI pipeline (normalize → document prefix → embed → validate dims).
var writer = new VectorStoreWriter<string>(vectorStore, embeddingOptions.Dimensions, new VectorStoreWriterOptions
{
    CollectionName       = FilingChunkSchema.CollectionName(vectorStoreOptions, embeddingOptions),
    DistanceFunction     = DistanceFunction.CosineSimilarity,   // must match FilingChunkSchema (Phase 00); scores = similarity
    IndexKind            = IndexKind.Hnsw,
    IncrementalIngestion = true      // upsert the document's new chunks, THEN delete its old ones
});
```

**Verify** the following, and record them:
- `PostgresVectorStoreOptions.EmbeddingGenerator` ("Letting the vector store generate embeddings" on Learn). If there's no store-level generator, set it on the collection, the record definition or the vector property.
- That the PgVector connector supports auto-embedding of `string` vector properties.

**Writer facts** (from the Learn API pages and the source they link, `src/Libraries/Microsoft.Extensions.DataIngestion/Writers/VectorStoreWriter.cs`):
- The schema is:
  - `key`: a random `Guid` per chunk;
  - `content`: `chunk.Content`;
  - `context`;
  - `documentid`: indexed;
  - `embedding`: from `chunk.Content` via the store's generator;
  - one lower-cased column per `chunk.Metadata` key, derived from the **first chunk**.
- The class is `sealed`.
- With `IncrementalIngestion`, it handles **only the first chunk's document per `WriteAsync` call**.

**Verify that `IngestionPipeline` calls the writer once per document.** If it batches several documents into one `WriteAsync`, run the pipeline **per file** instead (`ProcessAsync([file])`) and note it.

**Up-to-date check** (private `IsUpToDateAsync(id, fp)`):
1. If `!await collection.CollectionExistsAsync()` → not up to date (first run).
2. Run the built-in MEVD query `collection.GetAsync(r => r.DocumentId == id, top: 100_000)` on the typed read collection, `VectorStoreCollection<Guid, FilingChunkRecord>`.
3. The document is up to date when **≥ 1 chunk** is returned, **every** chunk has `IndexFingerprint == fp`, and **count == their ChunkCount**.

**Crash semantics** (document them in a code comment):
- An embedding or upsert failure leaves the previous version intact, because old keys are deleted only after all new chunks are written.
- A crash *mid-upsert* leaves old chunks plus some new ones. The check above reports "not up to date". On the next run, incremental ingestion collects **all** pre-existing keys (old plus partial) and deletes them after writing the full new set.

**Table creation:**
- The writer creates the table on first write. AgentServer (Phase 03) may create it earlier with `collection.EnsureCollectionExistsAsync()` from `FilingChunkSchema.CreateDefinition`, so search works before the first index run.
- This requires our definition to be **compatible** with the writer's. Check 3b in §9 verifies it with psql; Phase 09 adds a contract test.
- If the writer rejects a table our definition created, AgentServer must check `CollectionExistsAsync()` instead of creating it (return empty results until indexed); record which way it went.

**Other rules:**
- **Chunker.** `new HeaderChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk, OverlapTokens })` with `TiktokenTokenizer.CreateForEncoding("cl100k_base")`. That's an approximation of the embedding model's tokenizer; say so in a code comment.
  - `OverlapTokens` may be unsupported. If `HeaderChunker` rejects a non-zero value, an `IPostConfigureOptions<IndexingOptions>` probes it once at startup, sets `OverlapTokens = 0` and logs once. Then the configured value equals the effective value.
  - **Why `HeaderChunker` (built-in audit).** MEDI ships four chunkers: `HeaderChunker`, `SectionChunker`, `SemanticSimilarityChunker` and `DocumentTokenChunker`, all on `IngestionChunker<T>` (https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion.ingestionchunker-1).
    - `HeaderChunker` fits 10-Ks best, because the "Item N." headings *are* the semantic boundaries and they give the section citations.
    - `SemanticSimilarityChunker` needs an embedding call per sentence during chunking, which is too costly on CPU.
    - `DocumentTokenChunker` ignores structure.
    - `SectionChunker` splits on sections (pages), not headings.

    Record any measured comparison in the notes.
  - **Token counting** anywhere in this phase (chunk sizing, logs) uses the same `Microsoft.ML.Tokenizers` `Tokenizer` instance (`CountTokens`). No hand-written token estimators.
- **Why two custom chunk processors (built-in audit).** MEDI's built-in chunk processors are all LLM enrichers: `SummaryEnricher`, `KeywordEnricher`, `ClassificationEnricher` and `SentimentEnricher` (https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion.ingestionchunkprocessor-1). None of them cleans text or stamps known metadata, so:
  - `ChunkCleaningProcessor` and `FilingMetadataProcessor` derive from the built-in base class `IngestionChunkProcessor<string>`, each with a one-line "why no built-in" comment;
  - nothing else is custom here.
- **Failures.** Use `catch (Exception ex) when (!ct.IsCancellationRequested)` around staging and per-group processing. HttpClient/Polly timeouts are `TaskCanceledException`; they mean a per-filing or per-group failure, not a run abort. One bad filing never stops the others.
- **Order.** Process groups and files **sequentially**, because embedding is CPU-bound locally.
- **Logging.** Log each document: `Indexed AAPL/10-K/... chunks=312 in 00:01:12`.
- **Keys are random.** `FilingChunkRecord.Key` changes on every re-index, so evals and golden data reference `documentId` + section, never `ChunkId`.

### 6.5a Index fingerprint (automatic re-index when the pipeline changes)

A filing is "up to date" only if **both** its bytes and the configuration that shapes its indexed content are unchanged:

```
IndexingFingerprint.Compute(sourceSha256, configuration) =
    lower-hex SHA-256( UTF-8( sourceSha256 + "|" + canonicalJson ) )
canonicalJson = JSON object with keys sorted ordinally (recursively), values as invariant-culture strings, built from the
    configuration sections:  "Indexing" (EXCLUDING Indexing:StagingPath),
    "AI:Embedding:Normalization",  "Indexing:Enrichment" (Phase 14; absent → {})
```

- Changing chunking options, cleaning options, normalization or enrichment settings changes the fingerprint. The next **normal** run then re-indexes every affected filing, with no force needed.
- **Not included:** the embedding model and dimensions. Changing those selects a different collection (`FilingChunkSchema.CollectionName`), which starts empty.
- **Not included:** `Indexing:StagingPath`, which is a location, not content.
- Bump a code-level version value inside the relevant section (e.g. `Indexing:Cleaning:Version`, default 1) when cleaning **logic** changes in code.
- `sourceSha256` is the blob metadata `contentsha256` written by the IngestionInit job. The `indexfingerprint` column, stamped on every chunk, holds the fingerprint; never compare the two directly.

### 6.6 DI registration (`AddSecFilingsIndexing`)

```csharp
services.AddOptions<IndexingOptions>().BindConfiguration(IndexingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<ChunkCleaningOptions>().BindConfiguration(ChunkCleaningOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
services.AddKeyedSingleton<IngestionDocumentReader, HtmlFilingReader>("text/html");   // PDF later = one more keyed line (Open/Closed)
services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForEncoding("cl100k_base"));
services.AddSingleton<IngestionChunker<string>>(sp => new HeaderChunker(/* options from IndexingOptions */));
services.AddSingleton<ChunkCleaningProcessor>();          // NOT registered as IngestionChunkProcessor<string>: IndexingService appends it
services.AddSingleton(sp => /* VectorStoreWriter<string> as in §6.5 */);
services.AddSingleton<IIndexingService, IndexingService>();
// Extension hook: services.AddSingleton<IngestionChunkProcessor<string>, X>() — these run BEFORE cleaning, in registration order.
// Phase 14 (enrichers) appends its descriptor.
```

The final processor order is always: **[DI processors in registration order]** → `ChunkCleaningProcessor` → `FilingMetadataProcessor`.

### 6.7 `SECFilingsAgenticRag.IngestionPipeline.Functions` (Azure Functions isolated worker)

Create it with the Functions template:

```powershell
func init src\SECFilingsAgenticRag.IngestionPipeline.Functions --worker-runtime dotnet-isolated --target-framework net10.0
```

Or, if available: `dotnet new func -n SECFilingsAgenticRag.IngestionPipeline.Functions -o src\SECFilingsAgenticRag.IngestionPipeline.Functions`.

If .NET 10 isn't supported by the installed Functions worker or SDK, override `TargetFramework` in this csproj to the newest supported framework and note it. Fix the generated package references for CPM.

**Remove the Application Insights packages and calls** the template adds (`Microsoft.ApplicationInsights.WorkerService`, `Microsoft.Azure.Functions.Worker.ApplicationInsights`, and `AddApplicationInsightsTelemetryWorkerService()` / `ConfigureFunctionsApplicationInsights()`). Aspire ServiceDefaults provide OpenTelemetry, and a second telemetry pipeline clashes with it.

Packages:
- `Microsoft.Azure.Functions.Worker`, `Microsoft.Azure.Functions.Worker.Sdk`, `Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`;
- `Aspire.Npgsql` (provides `AddNpgsqlDataSource`), `Aspire.Azure.Storage.Blobs` (provides `AddAzureBlobContainerClient`), both on the shared Aspire version;
- `Pgvector` (for `UseVector`).

Reference `SECFilingsAgenticRag.IngestionPipeline`, `SECFilingsAgenticRag.Common` and `SECFilingsAgenticRag.ServiceDefaults`.

`Program.cs`:

```csharp
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false) // only if not already loaded — verify
                     .AddEnvironmentVariables();                                          // env vars must still win
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: b => b.UseVector());
builder.AddAzureBlobContainerClient("filings");
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(builder.Configuration, EmbeddingKeys.Document);   // write side: document-prefix generator
builder.Services.AddSecFilingsIndexing(builder.Configuration);
builder.Build().Run();
```

`IndexFunction.cs`:

```csharp
public sealed class IndexFunction(IIndexingService indexing, IHostApplicationLifetime lifetime, ILogger<IndexFunction> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);   // one indexing run at a time

    [Function("Index")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "index")] HttpRequest req, CancellationToken ct)
    {
        // body optional: { "tickers": ["AAPL"], "force": false }; empty body => all tickers, force=false
        //   read with JsonSerializer.DeserializeAsync<IndexingRequest>(req.Body, IndexingJson.Options, ct)
        // if (!await Gate.WaitAsync(0, ct)) return new ConflictObjectResult(new { error = "Indexing already running" });
        // try { var report = await indexing.IndexAsync(request, lifetime.ApplicationStopping);   // NOT ct (see below)
        //       return new JsonResult(report, IndexingJson.Options); } finally { Gate.Release(); }
    }
}
```

**Use the host shutdown token for `IndexAsync`, not the trigger's `ct`.** The trigger token may be tied to the HTTP request being aborted. If a caller gives up (the dashboard command's ~100 s client timeout), indexing must keep running and finish, and only a host shutdown should cancel it. Verify how `ct` behaves in the isolated worker with ASP.NET Core integration, and record it in the notes. Even if `ct` turns out not to be request-bound, keep `ApplicationStopping`, so the intent is explicit.

- The route is `/api/index` (Functions adds the `api/` prefix).
- **Serialize with `IndexingJson.Options`** from Common (§6.0), via `new JsonResult(report, IndexingJson.Options)`, so enums are strings and clients can read the response with the same options.
- `AuthorizationLevel.Anonymous` is fine for the local demo. Add a code comment that production would use Function keys or Entra auth.

`host.json` must include `"functionTimeout": "01:00:00"`.

`appsettings.json`: `CopyToOutputDirectory=PreserveNewest`. These are the **defaults**; Aspire overrides them with environment variables:

```json
{
  "AI": {
    "Chat":      { "Provider": "Ollama", "Model": "qwen3:4b", "TimeoutSeconds": 600 },
    "Embedding": { "Provider": "Ollama", "Model": "nomic-embed-text", "Dimensions": 768,
                   "DocumentPrefix": "search_document: ", "QueryPrefix": "search_query: ",
                   "BatchSize": 16, "TimeoutSeconds": 300,
                   "Normalization": { "UnicodeNormalization": true, "CollapseWhitespace": true, "Lowercase": false } }
  },
  "VectorStore": { "CollectionPrefix": "filing_chunks" },
  "Indexing":    { "MaxTokensPerChunk": 512, "OverlapTokens": 64,
                   "Cleaning": { "MarkdownArtifacts": true, "Boilerplate": true, "BoilerplateMinRepeatCount": 5,
                                 "BoilerplateMaxLineLength": 80, "PromptInjection": true, "PromptInjectionMode": "Flag" } }
}
```

The Chat section is required because `AddSecFilingsAI` validates both sections. The Function never calls the chat model.

`local.settings.json` is git-ignored and only used for standalone `func start` outside Aspire. Include:
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`
- `AzureWebJobsStorage=UseDevelopmentStorage=true`
- `ConnectionStrings:secfilings`, `ConnectionStrings:filings` and `ConnectionStrings:ollama` placeholders

Also commit a **`local.settings.sample.json`** copy so a fresh clone knows the shape. `.gitignore` covers only `local.settings.json`, so the sample *is* committed; that's intended. The sample holds placeholders only, no secrets.

### 6.8 Configuration rule (applies to everything in this phase)

- No model names, dimensions or endpoints appear in C# under `src/`.
- Defaults come from the Function's `appsettings.json`.
- Environment variables override them: `AI__Chat__Model`, `AI__Embedding__Model`, `AI__Embedding__Dimensions`, …
- Under Aspire, the AppHost injects the three parameters (`chat-model`, `embedding-model`, `embedding-dimensions`, declared in the AppHost `appsettings.json` under `Parameters`) through the Phase 00 `WithAIModels` helper. The API (Phase 03) gets the same values, which guarantees an identical embedding model on both sides.

### 6.9 AppHost wiring

Add the package `Aspire.Hosting.Azure.Functions` to the AppHost, on the **same** version as the other `Aspire.*` packages and the SDK attribute. Then reference the Functions project from the AppHost.

```csharp
var indexing = builder.AddAzureFunctionsProject<Projects.SECFilingsAgenticRag_IngestionPipeline_Functions>("indexing")
    .WithHostStorage(storage)                 // Functions runtime storage = the same Azurite (avoid a 2nd emulator)
    .WithReference(db).WaitFor(db)
    .WithReference(filings).WaitFor(filings)
    .WithReference(ollama)
    .WithAIModels(chatModel, embeddingModel, embeddingDimensions)   // Phase 00 helper => AI__* env vars
    .WithHttpCommand("/api/index", "Re-index filings",
        commandName: "reindex",                                      // ALWAYS explicit: two commands share path+method
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Index new or changed filings (unchanged ones are skipped)",
            PrepareRequest = ctx => { ctx.Request.Content = JsonContent.Create(new { force = false }); return Task.CompletedTask; },
            // ResultMode = <value that surfaces the response body>,   // verify member + enum value in Aspire 13.6
        })
    .WithHttpCommand("/api/index", "Force re-index ALL filings",
        commandName: "reindex-force",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            ConfirmationMessage = "Re-embed every filing? This takes several minutes on CPU.",
            PrepareRequest = ctx => { ctx.Request.Content = JsonContent.Create(new { force = true }); return Task.CompletedTask; },
            // ResultMode = <same>,
        });

```

Verify the `WithHttpCommand(path, displayName, endpointName, commandName, commandOptions)` parameter names and the `HttpCommandOptions` members (`Method`, `PrepareRequest`, `ConfirmationMessage`, `Description`, `IconName`, `ResultMode`, and the result accessor such as `GetCommandResult`) for the installed Aspire version.
- **Always pass `commandName`** (`reindex`, `reindex-force`). Default names derived from path and method would collide.
- **Set `ResultMode`** (or the equivalent) so the returned `IndexingReport` is visible in the dashboard / CLI output.
- The Functions HTTP endpoint is usually named `http`.

**Timeout caveat.** The command's internal HttpClient probably uses the default 100 s timeout. A quick "reindex" with everything skipped returns in time, but a **forced** full re-embed won't. The command then reports a timeout or failure while indexing continues in the Function. For long runs, observe completion through `aspire logs indexing`, not the command result, and document what you see. The Function's 409 response protects against concurrent runs.

### 6.10 Invoking the indexing phase: `scripts/Invoke-Indexing.ps1`

**Nothing starts indexing automatically.** The IngestionInit job (Phase 01) only downloads and uploads documents, then exits. The user requests the indexing phase by calling the Function, in one of two ways:

1. **Dashboard or CLI command:** `aspire resource indexing reindex` / `reindex-force` (§6.9). Fine for quick runs. The command's HTTP call times out after about 100 s, but indexing continues, so watch `aspire logs indexing` for long runs.
2. **Direct HTTP**, through a committed helper script. It's the agent-friendly, scriptable path, and it waits for the report:

```powershell
# scripts/Invoke-Indexing.ps1  (PowerShell 7.3+)
param(
    [switch]   $Force,
    [string[]] $Tickers,
    [int]      $TimeoutSec = 3600,
    [string]   $BaseUrl          # optional override; otherwise resolved from `aspire describe indexing`
)
# 1. Resolve the Function's http endpoint: parse `aspire describe indexing` (verify its output/flags, e.g. a --format json
#    option; record what works in the notes). If resolution fails and -BaseUrl is absent → write a clear error, exit 2.
# 2. Body = IndexingJson-compatible camelCase JSON: @{ tickers = $Tickers; force = [bool]$Force } | ConvertTo-Json
# 3. Invoke-RestMethod -Method Post "$BaseUrl/api/index" -ContentType 'application/json' -Body $body -TimeoutSec $TimeoutSec
#    409 → "Indexing already running" (exit 3).
# 4. Print the report: a table of documentId / status / chunkCount / duration / error, then
#    "Indexing: indexed=N skipped=N failed=N in hh:mm:ss".
# 5. exit 1 if any document has status "failed" (case-insensitive), else 0.
```

The Function keeps its safety rules:
- a **409** guard for overlapping runs;
- the **shutdown-token** rule (`IndexAsync` uses `ApplicationStopping`, not the request token);
- `host.json` `functionTimeout = 01:00:00`.

**Until the user indexes, the index is empty.** `/api/search` and chat return empty or "no filings" results. That's expected.

## 7. Implementation steps

1. **Verify the APIs** with the Microsoft Learn MCP, and write the confirmed signatures in your notes:
   - `IngestionPipeline<T>` (constructor, `ChunkProcessors`, `ProcessAsync` overloads, `IngestionResult` members; and whether it writes one document per `WriteAsync`);
   - `IngestionDocumentReader` overloads, `IngestionChunk<T>` (constructor, `Metadata`, `Context`) and `HeaderChunker` options;
   - `VectorStoreWriter<T>` and `VectorStoreWriterOptions`, plus `PostgresVectorStoreOptions.EmbeddingGenerator`;
   - `WithHttpCommand`, `AddAzureFunctionsProject` and `WithHostStorage`.

   Check: none (research).
2. **Common wire contract** (§6.0). Check: `dotnet build`.
3. **`HtmlFilingReader` and its internal helpers** (`HtmlPreparation`, `HeadingPromotion`). Check: `dotnet build`. Checks 3 and 3a in §9 verify the reader on the real filings.
4. **`ChunkCleaningProcessor`** (§6.3) and **`FilingMetadataProcessor`** (§6.4). Check: `dotnet build`.
5. **`IndexingFingerprint`** (§6.5a), **`IndexingService`** (§6.5) and **`AddSecFilingsIndexing`** (§6.6). Check: `dotnet build`.
6. **The Functions project** (§6.7): the CPM fix, remove Application Insights, `appsettings.json` with `CopyToOutputDirectory`. Check: `dotnet build`.
7. **AppHost wiring** (§6.9) and **`scripts/Invoke-Indexing.ps1`** (§6.10). Check:
   ```powershell
   aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing
   ./scripts/Invoke-Indexing.ps1
   ```
   Expect `Indexing: indexed=5 ...`. This takes minutes on CPU.
8. **Restart.** Run `aspire stop; aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1`. Expect `indexed=0 skipped=5`.
9. **Commands.** Run `aspire resource indexing reindex` (expect skipped only), then `aspire resource indexing reindex-force`, and watch `aspire logs indexing`. If the CLI can't invoke custom commands in this version, use the dashboard buttons and note it.
10. Write `docs/notes/phase-02-notes.md`, including the `## Commands & operating notes` section. Include the index-fingerprint rule: pipeline config changes re-index automatically, and force is only for rebuilding unchanged filings.

## 8. Tests

No automated tests in this phase. All unit and integration tests are deferred to [Phase 09](phase-09-tests.md), which specifies this phase's `IngestionPipeline.Tests` (reader, cleaning, metadata, fingerprint, the host-agnostic pipeline run, stable identity, the writer schema contract and the up-to-date check) and the `IndexingJson` tests in `Common.Tests`. This phase is verified by `dotnet build` and the run-time checks in §9.

`scripts/Invoke-Indexing.ps1` check: run it against the running AppHost (§9). With `-BaseUrl http://localhost:1` it must exit 2 with a clear message, and with a 409 response it must exit 3.

## 9. Acceptance criteria

1. **Build.**
   ```powershell
   dotnet build SECFilingsAgenticRag.slnx
   ```
   0 errors, 0 warnings.
2. **First indexing run.**
   ```powershell
   aspire start
   aspire wait ingestion-init --status down --timeout 3600   # documents downloaded/uploaded; the job has exited
   aspire wait indexing
   ./scripts/Invoke-Indexing.ps1                             # the USER requests indexing (minutes on CPU)
   aspire logs indexing | Select-String 'Indexed '
   ```
   Expect:
   - `Indexing: indexed=5 skipped=0 failed=0`, and one `Indexed <docId> chunks=…` line per filing;
   - **roughly 2–10 minutes** on a CPU-only laptop.

   Optional human check: the dashboard.
3. **Chunks in Postgres.** The table name is `FilingChunkSchema.CollectionName(...)`. For the default config that's `filing_chunks_nomic_embed_text_768`; if you changed the config, substitute the actual name recorded in the phase-00 notes. Requires PowerShell 7.3+.
   ```powershell
   $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
   docker exec $pg psql -U postgres -d secfilings -c "SELECT extname, extversion FROM pg_extension WHERE extname='vector';"
   docker exec $pg psql -U postgres -d secfilings -c 'SELECT ticker, fiscalyear, COUNT(*) AS chunks FROM filing_chunks_nomic_embed_text_768 GROUP BY ticker, fiscalyear ORDER BY 1;'
   docker exec $pg psql -U postgres -d secfilings -c 'SELECT section, COUNT(*) FROM filing_chunks_nomic_embed_text_768 WHERE ticker=''AAPL'' GROUP BY 1 ORDER BY 2 DESC LIMIT 15;'
   docker exec $pg psql -U postgres -d secfilings -c 'SELECT LEFT(content, 200) FROM filing_chunks_nomic_embed_text_768 WHERE section ILIKE ''Item 1A%'' LIMIT 2;'
   ```
   Expect:
   - the vector extension is listed;
   - 5 tickers, each with roughly 150–800 chunks;
   - AAPL sections include `Item 1A. Risk Factors` and `Item 7. ...`;
   - the text is readable prose, with no XBRL noise and no `search_document:` prefix.
3a. **Chunk cleaning worked** (Microsoft guide: enrichment → clean chunks). The stored `content` (the only text column; it's what was embedded) has no table pipes or running footers. Verify the actual column names first with `\d+ "filing_chunks_nomic_embed_text_768"`, and record them in the notes.
   ```powershell
   docker exec $pg psql -U postgres -d secfilings -t -c 'SELECT COUNT(*) FILTER (WHERE content LIKE ''%|%'') AS content_with_pipes, COUNT(*) FILTER (WHERE containsinstructionliketext) AS flagged, COUNT(DISTINCT indexfingerprint) AS fingerprints FROM filing_chunks_nomic_embed_text_768;'
   aspire logs indexing | Select-String 'Cleaned '      # one structured log line per document: boilerplateLines=… flagged=… excluded=…
   ```
   Expect `content_with_pipes = 0` and one fingerprint per filing. `flagged` is normally 0 for real 10-Ks; Phase 09's fixture-based pipeline test proves the flagger finds a planted sentence. Phase 03's acceptance shows `/api/search` excludes flagged chunks.
3b. **The writer's table matches `FilingChunkSchema`, and document identity is stable.** `VectorStoreWriter<string>` creates the table from a private schema, and Phase 03 reads it through `FilingChunkSchema.CreateDefinition`. This check is the only guard on that contract until Phase 09.
   ```powershell
   docker exec $pg psql -U postgres -d secfilings -c '\d+ filing_chunks_nomic_embed_text_768'
   docker exec $pg psql -U postgres -d secfilings -c 'SELECT DISTINCT documentid, chunkcount FROM filing_chunks_nomic_embed_text_768 ORDER BY 1;'
   ```
   Expect:
   - exactly the columns `FilingChunkSchema.CreateDefinition` declares, under the same storage names (`key`, `content`, `context`, `documentid`, every `FilingChunkMetadataKeys` value, and `embedding` of type `vector(768)`), with nothing missing and nothing extra. Any difference is a deviation: fix the schema and record it;
   - 5 rows whose `documentid` has the form `AAPL/10-K/<accession>`, never a temp or staging path.

   After check 5's `reindex-force`, re-run the second query: the same 5 `documentid` values and the same `chunkcount` (replaced, not duplicated).
4. **Idempotent restart.**
   ```powershell
   aspire stop; aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing
   ./scripts/Invoke-Indexing.ps1
   ```
   Expect `indexed=0 skipped=5` within seconds.
5. **Re-index commands** (AppHost still running).
   ```powershell
   aspire resource indexing reindex           # expect a report (via ResultMode) or logs showing Skipped for all 5
   aspire resource indexing reindex-force     # may report a timeout after ~100 s; indexing continues
   aspire logs indexing | Select-String 'Indexed '   # repeat until all 5 re-indexed lines appear
   ```
   Re-run the step-3 count query: the chunk counts are unchanged (no duplicates). If the CLI can't invoke custom commands in this version, use the dashboard buttons and note it.
6. **Manual API call.** Find the Function's HTTP endpoint with `aspire describe indexing` (the endpoints section), then:
   ```powershell
   $url = '<indexing http endpoint from describe>'
   Invoke-RestMethod -Method Post -Uri "$url/api/index" -ContentType 'application/json' -Body '{"tickers":["AAPL"],"force":false}'
   ```
   Expect a JSON `IndexingReport` with one document, `status: "skipped"` (camelCase string enum).
7. **Configuration override: the AppHost parameter path only** (skip standalone `func start`).
   ```powershell
   aspire stop
   ${env:Parameters__embedding-dimensions} = '1024'
   aspire start; aspire wait indexing
   Invoke-RestMethod -Method Post -Uri "$url/api/index" -ContentType 'application/json' -Body '{"tickers":["AAPL"],"force":true}'
   aspire logs indexing | Select-String 'dimensions'
   ```
   Expect the embedding middleware's dimension-mismatch error ("returned 768 dimensions but AI:Embedding:Dimensions is 1024"). That proves the injected env var overrode the Function's `appsettings.json`. The endpoint port may change after a restart, so re-read it with `aspire describe indexing`. Clean up:
   ```powershell
   aspire stop
   Remove-Item 'env:Parameters__embedding-dimensions'
   aspire start; aspire wait postgres
   $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
   docker exec $pg psql -U postgres -d secfilings -c 'DROP TABLE IF EXISTS filing_chunks_nomic_embed_text_1024;'
   aspire stop
   ```
   Record the observed behavior in the notes.
8. **No model names in C#, comments included:**
   ```powershell
   Get-ChildItem src -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
     Where-Object FullName -notmatch '\\(obj|bin)\\' |
     Select-String -Pattern 'qwen|nomic|llama|gpt-'
   ```
   Expect **no output**.

## 10. Gotchas

- **Timeouts everywhere.**
  - An indexing run can take 10 minutes or more. `Invoke-Indexing.ps1` uses `-TimeoutSec 3600`, and the dashboard command times out after about 100 s while indexing continues.
  - `functionTimeout` is `01:00:00`.
  - The circuit breaker's `SamplingDuration` must be ≥ 2× the attempt timeout.
- **Use `AddAzureFunctionsProject`, not `AddProject`.** `Program.cs` must use `FunctionsApplication.CreateBuilder`.
- **One storage emulator only.** `WithHostStorage(storage)` reuses the Aspire Azurite.
- **10-K HTML quirks.**
  - Headings are bold spans, not `<h*>`.
  - The table of contents repeats every "Item" line inside a table.
  - The hidden `ix:header` holds hundreds of numbers.
- **`ProcessAsync` takes files only** (Learn). Blobs are staged to `Indexing:StagingPath` with the stable layout `<TICKER>/<FORM>/<accession>.<ext>`. The staging folder is deleted after each run.
- **Stable document identity.** `documentid` must be `DocumentId`, never a temp path, or `IncrementalIngestion` can't find old chunks and every run duplicates the corpus. `HtmlFilingReader` sets it from the path relative to the staging root. Check 3b in §9 verifies it.
- **One document per `WriteAsync`.** `IncrementalIngestion` only replaces the *first* chunk's document per call. If the pipeline batches across documents, process one file per `ProcessAsync` call.
- **`VectorStoreWriter` metadata rules.**
  - The schema comes from the **first chunk's** metadata, via `value.GetType()`.
  - Every chunk needs the same keys with **non-null**, connector-supported values: string, int, long, bool, UTC `DateTime`, `string[]`/`int[]`. Use an empty array, never null.
  - `FilingMetadataProcessor` (always last) guarantees this. Don't add metadata anywhere else unless every chunk gets the key.
- **Random keys.** `ChunkId` isn't stable across re-index.
- **Column names are the writer's lower-case storage names** (`content`, `documentid`, `ticker`, …). Check 3b in §9 verifies them with `\d+`; record them in the notes.
- **Tokenizer mismatch.** cl100k only approximates `nomic-embed-text`'s tokenizer.
- **The embedding prefix is applied only in Common** (the keyed `document`/`query` MEAI middleware), after normalization. Never store it.
- **Npgsql UTC.** `filingdate` must be `DateTimeKind.Utc`.
- **pgvector type loading.** Call `PgVectorSetup.EnsureExtensionAsync` before the first collection operation in every process.
- **Collection name follows config.** A new embedding model or dimension means a new, empty table.
- **Small concurrency guard.** The Function returns 409 for overlapping requests.
- **MEDI is preview.** If a name here doesn't exist, use the closest equivalent, keep the semantics, and note it.
- **Zero chunks means Failed**, never `Indexed(0)`.
- **.NET clients must not reference `SECFilingsAgenticRag.IngestionPipeline`.** They use the Common wire contract plus `IndexingJson.Options`.
- **Overlapping requests.** A 409 means "someone else is indexing". The script exits 3; wait and retry.
- **Nothing indexes automatically.** After `aspire start` the index is empty until the user runs `Invoke-Indexing.ps1` or the `reindex` command.
- **Dashboard/CLI command timeout (about 100 s).** Watch `aspire logs indexing` for long runs.
- **Pipeline config changes re-index automatically** (§6.5a). Expect minutes on CPU. `indexfingerprint` is not the blob's `contentsha256`.
- **10-K tables.** Without flattening, ReverseMarkdown emits giant, mostly-empty pipe rows.
- **Two different "cleaning" jobs; don't merge them.**
  - The reader's HTML preparation is *document preparation* before chunking (guide: chunking phase → media analysis).
  - `ChunkCleaningProcessor` is *chunk enrichment* after chunking (guide: enrichment phase → clean chunks). Its output **is** the stored, embedded, cited text, so keep it conservative.
- **Symmetry.** Anything that must also apply to queries belongs in Common normalization, never in the document-only cleaning processor.
- **The prompt-injection flag is a heuristic.** Phase 03 filters flagged chunks; Phase 07 adds query-time guardrails. Never log matched text.
- **Function template telemetry.** Remove the Application Insights packages that `func init` adds.

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

- `dotnet build` is clean (0 errors, 0 warnings).
- `aspire start` indexes all 5 filings; the restart skips them; the `reindex` / `reindex-force` commands work.
- `docs/notes/phase-02-notes.md` covers:
  - the confirmed MEDI API names, and the `IngestionChunk.Context` format observed (which section-rule branch in `FilingMetadataProcessor` fires in practice); the identifier `IngestionPipeline` passes to the reader; whether the pipeline writes one document per `WriteAsync`;
  - how many sections per filing were recognized (`Item 1A`, `Item 7`, …) for the 5 real filings, and any heading pattern that still fails;
  - whether `OverlapTokens` was honored;
  - the actual chunk counts per ticker;
  - the indexing duration on this machine;
  - the Functions target framework used;
  - `WithHttpCommand` details and timeout behavior;
  - deviations;
  - for Phase 03: the collection/table name, column names, and the fact that `PgVectorSetup.EnsureExtensionAsync` must be called before searching.
- the `## Commands & operating notes` section of your phase notes covers: indexing commands, how to force re-index, the psql snippets.
- Suggested commit message:
  ```
  Phase 02: indexing pipeline — host-agnostic SECFilingsAgenticRag.IngestionPipeline (HTML→Markdown, header chunking, pgvector writer, idempotent), HTTP Azure Function, Aspire re-index commands, Invoke-Indexing script
  ```
