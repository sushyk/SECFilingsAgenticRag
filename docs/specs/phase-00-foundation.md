# Phase 00: Foundation and local infrastructure

> Depends on: nothing (first phase) | Produces: flattened solution, `SECFilingsAgenticRag.AppHost`, `SECFilingsAgenticRag.ServiceDefaults`, `SECFilingsAgenticRag.Common`, `docs/notes/phase-00-notes.md` (incl. Commands & operating notes) | Est. size: M

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
- **Authority.** This spec plus [`docs/01-architecture-and-design.md`](../01-architecture-and-design.md) are authoritative. If code already on disk conflicts with a contract here, **the code on disk wins**: keep it and record the deviation in `docs/notes/phase-00-notes.md`.
- **Verify unfamiliar or preview APIs** before coding against them.
  - Use the Microsoft Learn MCP tools if available: `microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`.
  - Otherwise use the package XML docs, IntelliSense or compiler errors.
  - `CommunityToolkit.VectorData.PgVector` is **stable** (1.0.x). Fast-moving packages: Aspire 13.x, `OllamaSharp`, `Microsoft.Extensions.AI.*`. (`Microsoft.Extensions.DataIngestion.*`, used from Phase 02, is preview-only.)
- **Central package management (CPM).**
  - Every package version lives in `Directory.Packages.props`; `.csproj` files have `<PackageReference Include="..." />` with no version.
  - Pick the latest **stable** version, and a preview only where no stable version exists.
  - Find versions with `dotnet package search <id> --exact-match --prerelease`.
  - **Every scaffold step** (`dotnet new classlib`, `console`, `aspire-*`, `func init`) writes versioned `PackageReference`s. Immediately strip each `Version="..."` attribute, add the matching `<PackageVersion>` to `Directory.Packages.props`, and delete any `<TargetFramework>`, `<Nullable>` or `<ImplicitUsings>` that `Directory.Build.props` already sets. Otherwise the build fails with **NU1008**.
- **Aspire version alignment.** All `Aspire.*` packages **and** the `Aspire.AppHost.Sdk/<version>` attribute in the AppHost's `<Project Sdk=...>` use **one** version: the latest stable 13.x (13.6.x at the time of writing). Templates may emit an older SDK version (e.g. 13.2.2), so fix it by hand, or run `dotnet new install Aspire.ProjectTemplates@<latest>` before scaffolding.
- **Platform.** Windows 11 with **PowerShell 7.3+ (`pwsh`)**. Commands that pass quoted arguments to native tools (`docker exec … psql -c "…"`) rely on 7.3+ argument passing. Every command in this spec is PowerShell.
- **Agent-executable checks.** Use the Aspire CLI rather than the dashboard:
  - `aspire start` launches the AppHost in the background (`aspire run` blocks the terminal);
  - `aspire wait <resource>`, `aspire describe [<resource>]` and `aspire logs <resource>` inspect it;
  - `aspire resource <resource> <command>` runs a command;
  - `aspire stop` shuts it down.

  Dashboard steps are optional human checks only.
- Match the existing code style (file-scoped namespaces, `sealed` by default, records for DTOs, nullable enabled).
- There are no earlier phases, so there are no earlier contracts to protect. Later phases depend on everything you create here, so keep the public names **exactly** as specified.
- **Do NOT `git commit`.** The user commits.
- Ask nothing. Make reasonable decisions and document them in the notes file.

## 1. Goal and why

This phase builds the skeleton every later phase plugs into. It doesn't do anything "AI" yet. It creates:
- the solution layout;
- the Aspire AppHost, which starts Postgres with pgvector plus Azurite and points at your native Ollama;
- a shared library, `SECFilingsAgenticRag.Common`, holding the contracts that both halves of RAG must agree on.

The most important idea is **one shared contract for the vector data**. The indexing side (Phase 02) writes chunks to Postgres and the query side (Phase 03) reads them. If the two disagree on the table schema, the embedding model, the vector dimension or the text prefixes, search silently returns garbage. So the record type `FilingChunkRecord`, its runtime schema, the collection name and the **two keyed embedding generators** (`EmbeddingKeys.Document` for indexing, `EmbeddingKeys.Query` for search) all live in Common, and both sides use them. The generators are plain Microsoft.Extensions.AI `IEmbeddingGenerator<string, Embedding<float>>` pipelines with one small middleware (normalization + task prefix + dimension check); there is no custom embedder interface.

Common also contains `AddSecFilingsAI()`. It turns configuration into an `IChatClient` and an `IEmbeddingGenerator`, the Microsoft.Extensions.AI abstractions. All other code depends only on those interfaces. Switching from local Ollama to a cloud model is therefore a configuration change, not a code change.

## 2. Concepts in 60 seconds

- **Embedding**: a list of numbers (here 768 floats) representing the meaning of a text. Similar meaning gives nearby vectors.
- **Vector database / pgvector**: Postgres plus an extension that stores vectors and finds the nearest ones quickly (HNSW index, cosine distance).
- **Task prefixes**: `nomic-embed-text` was trained to see `search_document: ` before documents and `search_query: ` before questions. Forgetting them noticeably hurts retrieval quality.
- **Microsoft.Extensions.AI**: provider-neutral interfaces (`IChatClient`, `IEmbeddingGenerator<TInput,TEmbedding>`) plus middleware (`UseLogging`, `UseOpenTelemetry`).
- **Aspire AppHost**: a C# program that describes and starts all the pieces (containers, projects, connection strings) and shows them in a dashboard.

## 3. Environment and prerequisites

| Tool | Required version | Check command |
|---|---|---|
| Windows 11 + PowerShell 7.3+ (`pwsh`) | ≥ 7.3 | `$PSVersionTable.PSVersion` |
| .NET SDK | 10.0.4xx (several SDKs are installed: 8.0.x, 10.0.1xx, 10.0.2xx, 10.0.4xx; `global.json` pins 10.0.4xx) | `dotnet --list-sdks` |
| Docker Desktop (Linux containers), **running** | recent | `docker info --format '{{.ServerVersion}}'` |
| Ollama (native Windows install) on `http://localhost:11434` | 0.x recent | `ollama --version`; `Invoke-RestMethod http://localhost:11434/api/tags` |
| Ollama models | – | `ollama pull nomic-embed-text` and `ollama pull qwen3:4b`, then `ollama list` |
| Azure Functions Core Tools v4 (needed from Phase 02) | 4.x | `func --version` |
| Aspire CLI + templates | 13.x | `aspire --version`; `dotnet new list aspire` |
| HTTPS dev cert | – | `dotnet dev-certs https --check --trust` (run `dotnet dev-certs https --trust` if needed) |

The machine has 16 GB RAM and no NVIDIA GPU, so models run on the CPU. Embedding is fast enough; chat generation is slow (seconds to minutes).

## 4. Repo state at start

```
<repo-root>\
├─ .git\                     (initialized, no commits yet)
├─ .gitignore                (dotnet template + a "# --- SECFilingsAgent ---" block that ALREADY contains
│                             data/filings/, .env, local.settings.json, evals/results/*, !evals/results/sample.md)
├─ .claude\                  (empty or Claude Code settings; leave it)
├─ CLAUDE.md                 (maintained by the user by hand; do NOT edit)
├─ README.md                 (written during planning, for humans)
├─ docs\                     (01-architecture..., 02-microsoft-guidance..., specs\)
├─ tests\                    (empty; test projects arrive in Phase 09)
└─ src\
   └─ SECFilingsAgent\
      ├─ SECFilingsAgent.slnx
      ├─ .vs\                (IDE cache)
      └─ SECFilings.Ingestion\
         ├─ SECFilings.Ingestion.csproj   (net10.0 classlib, Nullable + ImplicitUsings enabled)
         ├─ Class1.cs                    (placeholder)
         ├─ bin\  obj\
```

No contracts exist yet. You are creating them.

## 5. Scope

**In**
- Flatten the layout. Add `global.json`, `Directory.Build.props` and `Directory.Packages.props`, and make sure the `.gitignore` entries are present.
- `src/SECFilingsAgenticRag.AppHost` with Postgres (pgvector image), Azurite plus the `filings` blob container, and the `ollama` connection string.
- `src/SECFilingsAgenticRag.ServiceDefaults` (Aspire template, unmodified apart from CPM).
- `src/SECFilingsAgenticRag.Common`: all contracts in §6.3, with implementations.
- Record commands, conventions and gotchas in `docs/notes/phase-00-notes.md`. Do **not** create or edit `CLAUDE.md`; the user maintains it.

**Out**
- Downloading filings, the init job, indexing, retrieval, the API and agents (Phases 01–05).
- Hybrid search columns (Phase 06). Telemetry tuning and resilience policies (Phase 07).

## 6. Design

### 6.1 Target layout after this phase

```
SECFilingsAgent\
├─ SECFilingsAgenticRag.slnx          (moved to root; lists every project below)
├─ global.json                   (pins SDK 10.0.4xx)
├─ Directory.Build.props
├─ Directory.Packages.props
├─ CLAUDE.md
├─ src\
│  ├─ SECFilingsAgenticRag.AppHost\        (AppHost.cs, appsettings.json, appsettings.Development.json)
│  ├─ SECFilingsAgenticRag.ServiceDefaults\
│  ├─ SECFilingsAgenticRag.Common\
│  └─ SECFilingsAgenticRag.IngestionPipeline\       (moved here; Class1.cs deleted; stays empty until Phase 02)
```

`src/SECFilingsAgent/` is deleted entirely, including `.vs`, `bin` and `obj`.

### 6.2 Build props

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <!-- Preview/experimental AI APIs raise diagnostics; suppress only the specific IDs you hit and list them in the notes. -->
  </PropertyGroup>
</Project>
```

`Directory.Packages.props` starts with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`, followed by an `ItemGroup` of `PackageVersion` entries. Templates write versions directly into the `.csproj` files: move them into `Directory.Packages.props` (see §0).

The `Aspire.AppHost.Sdk/<version>` attribute in the AppHost's `<Project Sdk=...>` is **not** covered by CPM. Set it by hand to the **same** version as every `Aspire.*` `PackageVersion`, the latest stable 13.x. Add a comment in `Directory.Packages.props` reminding future phases to keep them in step.

`global.json` (repo root):

```json
{ "sdk": { "version": "10.0.400", "rollForward": "latestFeature", "allowPrerelease": false } }
```

Use the lowest 10.0.4xx installed; `latestFeature` then picks the newest 10.0.4xx patch. Check with `dotnet --version`, which should print 10.0.4xx.

`.gitignore`: **ensure** these lines are present (idempotent; they normally already exist in the `# --- SECFilingsAgent ---` block). Don't duplicate or reorder existing entries, and keep `evals/results/*` / `!evals/results/sample.md`.

```
data/filings/
.env
local.settings.json
```

### 6.3 `SECFilingsAgenticRag.Common`: public contracts

Packages (all via CPM):
- `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OllamaSharp`;
- `Microsoft.Extensions.VectorData.Abstractions`, `CommunityToolkit.VectorData.PgVector` (stable 1.0.x), `Npgsql`, `Pgvector`;
- `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Options.DataAnnotations`, `Microsoft.Extensions.Configuration.Binder`;
- `Microsoft.Extensions.Http` only if needed.

**Do not reference any Aspire, ASP.NET Core or Azure Functions package from Common.**

**No model names in C#, not even in comments or XML docs.** Acceptance check 4 greps `src/` for `qwen|nomic|llama|gpt-`. The code sketches below therefore use placeholders like `<model>`. Concrete names appear only in `appsettings.json` files.

```csharp
namespace SECFilingsAgenticRag.Common.Filings;

/// Metadata for one filing. Written by the IngestionInit download step as <accession>.metadata.json,
/// copied into blob metadata by the IngestionInit job, and copied onto every chunk by the indexer.
public sealed record FilingMetadata(
    string Ticker,            // "AAPL" (upper-case)
    string CompanyName,       // "Apple Inc."
    string Cik,               // "0000320193" (10 digits, zero-padded)
    string FormType,          // "10-K"
    string AccessionNumber,   // "0000320193-25-000079"
    DateOnly FilingDate,      // date filed with the SEC
    DateOnly ReportDate,      // period of report (fiscal year end)
    int FiscalYear,           // ReportDate.Year
    string PrimaryDocument,   // "aapl-20250927.htm"
    string SourceUrl)         // https://www.sec.gov/Archives/edgar/data/320193/000032019325000079/aapl-20250927.htm
{
    /// Stable id for the filing document, e.g. "AAPL/10-K/0000320193-25-000079".
    public string DocumentId => $"{Ticker}/{FormType}/{AccessionNumber}";
}

/// Blob naming + blob-metadata key conventions shared by IngestionInit and Indexing.
public static class FilingBlobConventions
{
    public static string BlobName(FilingMetadata m, string extension) => $"{m.DocumentId}{extension}"; // "AAPL/10-K/0000320193-25-000079.htm"
    // Blob metadata keys (lower-case ASCII; values ASCII-safe; CompanyName is URL-encoded):
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        Accession = "accession", FilingDate = "filingdate", ReportDate = "reportdate", FiscalYear = "fiscalyear",
        PrimaryDocument = "primarydocument", SourceUrl = "sourceurl",
        ContentSha256 = "contentsha256";     // hex SHA-256 of the file bytes, computed by the IngestionInit job
    public static IDictionary<string, string> ToBlobMetadata(FilingMetadata m, string contentSha256);
    public static FilingMetadata FromBlobMetadata(IDictionary<string, string> metadata);
}
```

Implementation notes for `FilingBlobConventions`:
- Dates are formatted as `yyyy-MM-dd` (invariant culture). `FiscalYear` uses invariant integer format.
- `CompanyName` goes through `Uri.EscapeDataString` on write and `Uri.UnescapeDataString` on read.
- `SourceUrl` is already ASCII.
- `FromBlobMetadata` must be case-insensitive on keys (the Azure SDK may return different casing), and throws `FormatException` naming the missing key.
- `ToBlobMetadata` includes `contentsha256` (lower-case hex).

```csharp
namespace SECFilingsAgenticRag.Common.VectorStore;

/// One chunk of one filing, as stored in pgvector. WRITTEN in Phase 02 by MEDI's VectorStoreWriter<string>, READ by Retrieval.
/// The table schema is the WRITER's (private, preview): "key", "content", "context", "documentid", "embedding", plus one
/// lower-cased column per chunk.Metadata entry. This record MIRRORS that schema through StorageName (see CreateDefinition).
/// Only the CLEANED chunk is stored (Microsoft's enrichment guide makes keeping the original an optional extra field).
public sealed class FilingChunkRecord
{
    public Guid Key { get; set; }                 // "key": random Guid generated by the writer; NOT stable across re-index
    public string Text { get; set; } = "";        // "content": the CLEANED chunk: stored, embedded, returned, cited; no task prefix
    public string? Context { get; set; }          // "context": MEDI chunk context (may be null)
    public string DocumentId { get; set; } = "";  // "documentid": FilingMetadata.DocumentId (= IngestionDocument.Identifier)
    // ---- metadata columns: chunk.Metadata keys from FilingChunkMetadataKeys, lower-case storage names ----
    public string Ticker { get; set; } = "";      // "ticker"
    public string CompanyName { get; set; } = ""; // "companyname"
    public string Cik { get; set; } = "";         // "cik"
    public string FormType { get; set; } = "";    // "formtype"
    public int FiscalYear { get; set; }           // "fiscalyear"
    public DateTime FilingDate { get; set; }      // "filingdate": DateTimeKind.Utc (Npgsql requires UTC for timestamptz)
    public string Section { get; set; } = "";     // "section": e.g. "Item 1A. Risk Factors"
    public int ChunkIndex { get; set; }           // "chunkindex"
    public int ChunkCount { get; set; }           // "chunkcount": used by Phase 02's up-to-date check
    public string IndexFingerprint { get; set; } = ""; // "indexfingerprint": source SHA-256 + pipeline config (Phase 02)
    public bool ContainsInstructionLikeText { get; set; } // "containsinstructionliketext": prompt-injection flag (Phase 02); filterable
    public string SourceUrl { get; set; } = "";   // "sourceurl": EDGAR URL, used for citations
    public string EmbeddingModel { get; set; } = ""; // "embeddingmodel"
    public ReadOnlyMemory<float>? Embedding { get; set; } // "embedding"
}

/// The exact chunk.Metadata keys Phase 02 stamps on EVERY chunk (non-null values).
public static class FilingChunkMetadataKeys
{
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        FiscalYear = "fiscalyear", FilingDate = "filingdate", Section = "section", ChunkIndex = "chunkindex",
        ChunkCount = "chunkcount", IndexFingerprint = "indexfingerprint",
        ContainsInstructionLikeText = "containsinstructionliketext", SourceUrl = "sourceurl", EmbeddingModel = "embeddingmodel";
}

/// Namespace is PINNED: SECFilingsAgenticRag.Common.VectorStore (not Common.AI).
public sealed class VectorStoreOptions        // section "VectorStore"
{
    public const string SectionName = "VectorStore";
    [Required] public string CollectionPrefix { get; set; } = "filing_chunks";   // not deployment-specific; code default OK
}

/// Builds the schema at runtime so the vector dimension and collection name follow configuration.
public static class FilingChunkSchema
{
    public const int MaxIdentifierLength = 63;   // Postgres identifier limit (NAMEDATALEN - 1)
    public static VectorStoreCollectionDefinition CreateDefinition(int dimensions);
    // Key "key" (Guid) | Data properties with StorageName exactly as listed on FilingChunkRecord ("content", "context",
    //   "documentid", "ticker", ...; DocumentId, Ticker, FormType, FiscalYear, ContainsInstructionLikeText IsIndexed = true)
    //   | Vector "embedding": dimensions, DistanceFunction.CosineSimilarity (scores are similarities, higher is better; Phase 03 verifies, fallback documented there), IndexKind.Hnsw.
    // MUST be compatible with the table MEDI VectorStoreWriter<string> creates; Phase 02's psql schema check proves it.
    /// THE table-name accessor. The PgVector connector uses the collection name verbatim as the Postgres table
    /// name (quoted identifier, so case is preserved — we always produce lower-case). All raw SQL
    /// (psql checks, Phase 05 catalog, Phase 06 FilingChunkTableInfo) must call this, never hard-code the name.
    public static string CollectionName(VectorStoreOptions store, EmbeddingOptions embedding);
    // => "{prefix}_{model}_{dims}", e.g. "filing_chunks_<model>_<dims>" (lower-case; every non [a-z0-9] char -> '_';
    //    collapse repeats; trim '_'). If longer than 63 chars: truncate to 54 and append "_" + first 8 hex chars of
    //    SHA-256(full name) so distinct long names stay distinct and deterministic.
}

/// Custom (no built-in fits): MEVD/PgVector don't create the `vector` extension inside an existing database,
/// and Aspire's creation script runs at server level. One static call at host startup; no interface.
public static class PgVectorSetup
{
    /// CREATE EXTENSION IF NOT EXISTS vector; then connection.ReloadTypesAsync() so Npgsql learns the type. Idempotent.
    public static Task EnsureExtensionAsync(NpgsqlDataSource dataSource, CancellationToken ct = default);
}

public static class VectorStoreServiceCollectionExtensions
{
    /// DI glue only. Requires an NpgsqlDataSource (built with UseVector()) to be registered by the host.
    /// Registers: VectorStoreOptions (section "VectorStore"); a PostgresVectorStore whose EmbeddingGenerator is the
    /// KEYED IEmbeddingGenerator `embeddingKey` (EmbeddingKeys.Document on the indexing host, EmbeddingKeys.Query on AgentServer);
    /// and VectorStoreCollection<Guid, FilingChunkRecord> (singleton, CreateDefinition(Embedding:Dimensions) + CollectionName(...)).
    public static IServiceCollection AddSecFilingsVectorStore(this IServiceCollection services, IConfiguration configuration, string embeddingKey);
}
```

Startup sequence in every host that touches the table (Phase 02 Function, Phase 03 AgentServer):
1. `await PgVectorSetup.EnsureExtensionAsync(dataSource)`.
2. Only the **AgentServer** also calls `await collection.EnsureCollectionExistsAsync()`, so search on an empty index returns nothing instead of failing. The indexing side doesn't need it: MEDI's `VectorStoreWriter` creates the collection itself (Phase 02 verifies the writer accepts a table that `FilingChunkSchema` created first).

With the store's `EmbeddingGenerator` set, the **built-in** auto-embedding applies: the writer stores `chunk.Content` in the vector property and the store embeds it with the document generator; on AgentServer, `collection.SearchAsync("text", top)` embeds the query with the query generator. Verify on Learn ("Letting the vector store generate embeddings"; `PostgresVectorStoreOptions.EmbeddingGenerator`) that the PgVector connector supports this for a `ReadOnlyMemory<float>` vector property searched with a `string`. Fallback, if it doesn't: resolve the keyed generator and pass the embedding (`ReadOnlyMemory<float>`) to `SearchAsync`. Record which path you used.

**Column names are the writer's lower-case storage names** (`content`, `documentid`, `ticker`, `fiscalyear`, …). Set them with `StorageName` in `CreateDefinition`, because Phase 02 writes the table with MEDI's `VectorStoreWriter<string>`, which uses these names. Later phases write SQL against `content`, `documentid`, `ticker` and so on, always with the table name from `CollectionName(...)`.

Phase 02's acceptance verifies both naming facts once the writer has created the table: the table name equals `CollectionName(...)`, and the columns carry the storage names above (`\d+ "<table>"` in psql). Nothing creates the table in this phase.

**Configuration rule: no hard-coded model names, dimensions or endpoints in C#.**
- `Provider`, `Model` and `Dimensions` are `[Required]` with **no code default**. A host that forgets them fails at startup (`ValidateOnStart`) instead of silently using a baked-in model.
- The defaults live in each **host's** `appsettings.json` (Functions, AgentServer, Evals, …). Common has no appsettings.
- Environment variables override them: `AI__Chat__Model`, `AI__Embedding__Model`, `AI__Embedding__Dimensions`, … (`__` = `:`).
- Under Aspire, the AppHost declares the models once as parameters and injects them as these environment variables into every service (§6.4).
- `ApiKey` only ever comes from user-secrets or an environment variable, never from `appsettings.json`.

```csharp
namespace SECFilingsAgenticRag.Common.AI;

// Bound from configuration (host appsettings.json, overridable by env vars such as AI__Chat__Model).
public sealed class ChatModelOptions          // section "AI:Chat"
{
    public const string SectionName = "AI:Chat";
    [Required] public string Provider { get; set; } = null!;          // Ollama | OpenAICompatible  (NO default)
    [Required] public string Model { get; set; } = null!;             // chat model id from config    (NO default)
    public string? Endpoint { get; set; }                            // OpenAICompatible only
    public string? ApiKey { get; set; }                              // OpenAICompatible only (user-secrets / env var)
    [Range(10, 7200)] public int TimeoutSeconds { get; set; } = 600; // operational tuning, a code default is OK
}

public sealed class EmbeddingOptions          // section "AI:Embedding"
{
    public const string SectionName = "AI:Embedding";
    [Required] public string Provider { get; set; } = null!;          // NO default
    [Required] public string Model { get; set; } = null!;             // embedding model id from config (NO default)
    [Range(1, 2000)] public int Dimensions { get; set; }             // 0 fails validation (NO default). 2000 = pgvector HNSW max
    public string DocumentPrefix { get; set; } = "";                 // model-specific -> set in appsettings
    public string QueryPrefix { get; set; } = "";                    // model-specific -> set in appsettings
    [Range(1, 512)] public int BatchSize { get; set; } = 16;
    [Range(10, 7200)] public int TimeoutSeconds { get; set; } = 300;
    public bool CacheQueries { get; set; }                           // MEAI UseDistributedCache on the Query generator (host registers IDistributedCache)
    public EmbeddingNormalizationOptions Normalization { get; set; } = new();   // section "AI:Embedding:Normalization"
}

/// SYMMETRIC text normalization: applied by the embedding middleware to documents AND queries (like the task prefixes),
/// so both sides of a vector comparison see text normalized the same way. Microsoft's RAG enrichment guide
/// ("Clean your data") lists unicode normalization, text normalization and lowercasing as cleaning options.
public sealed class EmbeddingNormalizationOptions
{
    public bool UnicodeNormalization { get; set; } = true;  // NFKC; NBSP/zero-width removed; smart quotes -> ' "; en/em dashes -> '-'
    public bool CollapseWhitespace { get; set; } = true;    // runs of whitespace -> one space; trim
    public bool Lowercase { get; set; }                     // default false: the guide says evaluate first (Phase 06 note)
    /// Deterministic, e.g. "norm:v1:nfkc=true:ws=true:lower=false" (invariant culture, lower-case bools).
    /// Phase 02 folds it into the index fingerprint so changing normalization re-indexes.
    public string Fingerprint { get; }
}

/// Keys of the two keyed IEmbeddingGenerator<string, Embedding<float>> services. These are the ONLY embedding entry points.
public static class EmbeddingKeys
{
    public const string Document = "document";   // indexing: normalize -> DocumentPrefix -> embed -> validate dims
    public const string Query = "query";         // search:   normalize -> QueryPrefix    -> embed -> validate dims
}

public static class AIServiceCollectionExtensions
{
    /// DI glue over MEAI builders. Registers:
    ///  - IChatClient = provider client .AsBuilder().UseLogging().UseOpenTelemetry()  (NO UseFunctionInvocation here:
    ///    agents add it with their own iteration cap, Phase 05);
    ///  - KEYED IEmbeddingGenerator<string, Embedding<float>> EmbeddingKeys.Document and EmbeddingKeys.Query, each =
    ///    provider generator .AsBuilder()
    ///        .Use(generateFunc)        // MEAI EmbeddingGeneratorBuilder.Use(...): the one small middleware below
    ///        [.UseDistributedCache()]  // Query key only, when AI:Embedding:CacheQueries = true (host registers IDistributedCache)
    ///        .UseLogging().UseOpenTelemetry()
    ///    (verify the keyed registration helper, e.g. services.AddKeyedEmbeddingGenerator(key, ...), on Learn).
    /// Reads the Ollama endpoint from ConnectionStrings:ollama ("Endpoint=..."). Dedicated HttpClient with a LONG timeout.
    public static IServiceCollection AddSecFilingsAI(this IServiceCollection services, IConfiguration configuration);
}
```

**The embedding middleware** is an `EmbeddingGeneratorBuilder.Use(Func<IEnumerable<string>, EmbeddingGenerationOptions?, IEmbeddingGenerator<…>, CancellationToken, Task<GeneratedEmbeddings<…>>>)` lambda (see https://learn.microsoft.com/dotnet/ai/iembeddinggenerator#pipelines-of-functionality). If it grows, a `DelegatingEmbeddingGenerator<string, Embedding<float>>` subclass is the built-in alternative. It does three things:
1. For each input: normalize, **then** prepend the prefix for this key. Normalization order: Unicode NFKC plus NBSP / zero-width / smart-quote / dash replacement → optional `ToLowerInvariant` → collapse whitespace, with options from `AI:Embedding:Normalization`. Normalizing after prefixing would mangle the prefix.
2. Split into groups of `BatchSize`, call `inner.GenerateAsync(...)` per group, and preserve order. Empty input → empty result without calling the inner generator.
3. If any vector length ≠ `Dimensions`, throw `InvalidOperationException("Embedding model '{Model}' returned {n} dimensions but AI:Embedding:Dimensions is {Dimensions}. Fix config and re-index.")`.

The normalization routine is a private static method next to the registration code not a public type.

`AddSecFilingsAI` details:
- **Options.** Bind `ChatModelOptions` and `EmbeddingOptions` with `.ValidateDataAnnotations().ValidateOnStart()`.
- **Eager provider check (pinned behavior).** Inside the `AddSecFilingsAI` call itself, before registering anything, read `configuration["AI:Chat:Provider"]` and `configuration["AI:Embedding:Provider"]` directly from `IConfiguration`. Then:
  1. **Provider missing or empty:** register nothing provider-specific for that client, and let options validation report it. You get `OptionsValidationException` mentioning `Provider` on first `.Value` access, or at host start via `ValidateOnStart`. The "Required" validation message wins over every other error.
  2. **Provider present but unknown:** throw `InvalidOperationException("Unknown AI:Chat:Provider '<x>'. Valid values: Ollama, OpenAICompatible.")` **from the `Add…` call** (same for Embedding). Compare case-insensitively.
  3. **`OpenAICompatible` with an empty `ApiKey` or `Endpoint`:** throw `InvalidOperationException` **from the `Add…` call**, naming the missing key. Empty strings count as unset.
  4. **`Ollama`:** `Endpoint` and `ApiKey` are ignored, and empty values are fine. A missing `ConnectionStrings:ollama` throws **from the `Add…` call**, see below.

  Model and dimensions are *not* checked eagerly; they go through `[Required]` / `[Range]` validation.
- **Ollama endpoint.** Read `configuration.GetConnectionString("ollama")`. Parse it with `System.Data.Common.DbConnectionStringBuilder` and take the `Endpoint` key. If the value is a bare URL with no `Endpoint=`, accept it as-is. If it's missing while a provider is `Ollama`, throw `InvalidOperationException("ConnectionStrings:ollama is not configured …")`. There's no hard-coded endpoint fallback (configuration rule). Put the parsing in a small public static helper, `OllamaConnectionString.Parse(string?) → Uri`, so it can be unit-tested.
- **Ollama provider.**
  - Create `new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) }`, then `new OllamaApiClient(httpClient, model)`.
  - Chat and embeddings each get their own client and their own model.
  - `OllamaApiClient` implements both `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>`; verify against the current OllamaSharp version.
  - **Do not** obtain this `HttpClient` from `IHttpClientFactory`. Aspire ServiceDefaults attach a standard resilience handler (10 s attempt / 30 s total timeout) to every factory client, and that would kill slow local LLM calls.
- **OpenAICompatible provider.**
  - `new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromSeconds(timeout) })`.
  - Then `.GetChatClient(model).AsIChatClient()`, or for embeddings `.GetEmbeddingClient(model).AsIEmbeddingGenerator()`.
  - The missing `ApiKey` / `Endpoint` check happens eagerly (rule 3 above).
- **Unknown provider.** Eager throw (rule 2 above).
- **Registration** (MEAI builders only; no wrappers of our own):
  - `services.AddChatClient(sp => inner).UseLogging().UseOpenTelemetry();`
  - `services.AddKeyedEmbeddingGenerator(EmbeddingKeys.Document, sp => innerEmbedding).Use(documentMiddleware).UseLogging().UseOpenTelemetry();`
  - `services.AddKeyedEmbeddingGenerator(EmbeddingKeys.Query, sp => innerEmbedding).Use(queryMiddleware)[.UseDistributedCache()].UseLogging().UseOpenTelemetry();`
  - Verify the builder and keyed-registration method names in `Microsoft.Extensions.AI` on Learn.
- **Internal structure.** Keep it Open/Closed with one private factory method per provider, selected through a `switch` on the provider name (a `Dictionary<string, Func<...>>` is fine too).

### 6.4 `SECFilingsAgenticRag.AppHost`

Create it with `dotnet new aspire-apphost -o src/SECFilingsAgenticRag.AppHost -n SECFilingsAgenticRag.AppHost`. Then apply the CPM rules and the Aspire version-alignment rule from §0.

Packages, all on the same Aspire version:
- `Aspire.Hosting.AppHost`, if the template references it;
- `Aspire.Hosting.PostgreSQL`;
- `Aspire.Hosting.Azure.Storage`.

Phase 01 adds no AppHost packages. Phase 02 adds `Aspire.Hosting.Azure.Functions`.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Postgres with the pgvector extension available (image ships the extension binaries).
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume()                 // data survives restarts
    .WithLifetime(ContainerLifetime.Persistent);   // optional: faster restarts; document your choice
var db = postgres.AddDatabase("secfilings");

// Azurite (Azure Storage emulator) + the container for raw filings.
// Fixed host blob port 10000 so CLI checks can use the well-known devstoreaccount1 connection string.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume().WithBlobPort(10000));   // verify WithBlobPort name in Aspire 13
var filings = storage.AddBlobContainer("filings");

// Native Ollama, NOT a container. Value comes from AppHost configuration ConnectionStrings:ollama.
var ollama = builder.AddConnectionString("ollama");

// Model configuration: declared ONCE here, injected into every service as environment variables
// so the indexing Function and the API are guaranteed to use the identical embedding model.
var chatModel           = builder.AddParameter("chat-model");            // Parameters:chat-model
var embeddingModel      = builder.AddParameter("embedding-model");       // Parameters:embedding-model
var embeddingDimensions = builder.AddParameter("embedding-dimensions");  // Parameters:embedding-dimensions

// Later phases add: init job (01), indexing Function (02), api (03+). Each AI-using service calls
//   .WithAIModels(chatModel, embeddingModel, embeddingDimensions)

builder.Build().Run();
```

**`WithAIModels`: pinned signature.** Implement it in `src/SECFilingsAgenticRag.AppHost/AIModelResourceExtensions.cs`; every later phase calls exactly this:

```csharp
public static class AIModelResourceExtensions
{
    public static IResourceBuilder<T> WithAIModels<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<ParameterResource> chatModel,
        IResourceBuilder<ParameterResource> embeddingModel,
        IResourceBuilder<ParameterResource> embeddingDimensions)
        where T : IResourceWithEnvironment
        => builder
            .WithEnvironment("AI__Chat__Model", chatModel)
            .WithEnvironment("AI__Embedding__Model", embeddingModel)
            .WithEnvironment("AI__Embedding__Dimensions", embeddingDimensions);
}
```

AppHost `appsettings.json` must contain:

```json
{
  "ConnectionStrings": {
    "ollama": "Endpoint=http://localhost:11434"
  },
  "Parameters": {
    "chat-model": "qwen3:4b",
    "embedding-model": "nomic-embed-text",
    "embedding-dimensions": "768"
  }
}
```

Verify the `AddParameter`, `WithBlobPort` and `WithEnvironment(string, IResourceBuilder<ParameterResource>)` overloads for Aspire 13 with the Microsoft Learn MCP. If `WithBlobPort` is named differently, use the equivalent API that pins the emulator's blob host port, and note it.

**Port 10000 conflict.** If another Azurite (VS, or a standalone `azurite` process) already uses port 10000, `storage` fails to start. Stop the other emulator, and note this in your phase notes.

The well-known connection string for CLI checks against this emulator (Azurite's public dev account, not a secret):

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;
```

To override a parameter, set an environment variable before `aspire start` (or `aspire run`), for example:

```powershell
${env:Parameters__chat-model} = 'llama3.2'
```

The braces are needed because of the hyphen. Values that are not secrets may also be edited in the AppHost `appsettings.json`.

Nothing references `db`, `filings` or `ollama` in this phase. Keep the variables anyway: assign them with `_ =` or leave them, and suppress the unused warnings. Phase 01 and 02 add the references.

pgvector extension creation: the pgvector image *ships* the extension, but each database must still run `CREATE EXTENSION`. This project does that from code, in `PgVectorSetup.EnsureExtensionAsync`, because Aspire's database creation script runs at server level, not inside the new database. If you find a clean, verified Aspire API that runs SQL inside `secfilings`, you may *additionally* use it, and note it.

### 6.5 `SECFilingsAgenticRag.ServiceDefaults`

Create it with `dotnet new aspire-servicedefaults -o src/SECFilingsAgenticRag.ServiceDefaults -n SECFilingsAgenticRag.ServiceDefaults`. Leave the generated code as-is, apart from moving versions to CPM and aligning the Aspire version. Its packages are `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.ServiceDiscovery` and the `OpenTelemetry.*` set; keep whatever the template lists. Later phases call `builder.AddServiceDefaults()` from hosts.

## 7. Implementation steps

1. **Flatten the layout.**
   ```powershell
   cd <repo-root>
   Move-Item src\SECFilingsAgent\SECFilings.Ingestion src\SECFilingsAgenticRag.IngestionPipeline
   Rename-Item src\SECFilingsAgenticRag.IngestionPipeline\SECFilings.Ingestion.csproj SECFilingsAgenticRag.IngestionPipeline.csproj
   Remove-Item src\SECFilingsAgenticRag.IngestionPipeline\Class1.cs
   Remove-Item -Recurse -Force src\SECFilingsAgenticRag.IngestionPipeline\bin, src\SECFilingsAgenticRag.IngestionPipeline\obj -ErrorAction SilentlyContinue
   Move-Item src\SECFilingsAgent\SECFilingsAgent.slnx .\SECFilingsAgenticRag.slnx
   Remove-Item -Recurse -Force src\SECFilingsAgent   # fails if Visual Studio has the old .slnx open (locks .vs) — close VS first
   ```
   Edit `SECFilingsAgenticRag.slnx` so the project path is `src/SECFilingsAgenticRag.IngestionPipeline/SECFilingsAgenticRag.IngestionPipeline.csproj`. Remove `TargetFramework`, `Nullable` and `ImplicitUsings` from that csproj; they now come from `Directory.Build.props`.
   Check: `dotnet build SECFilingsAgenticRag.slnx` succeeds.
2. **Add `global.json` and the props files**, and ensure the `.gitignore` entries exist (§6.2). Check: `dotnet --version` prints 10.0.4xx, and `dotnet build` still succeeds.
3. **Create ServiceDefaults and AppHost** from the templates (§6.4, §6.5). Add them with `dotnet sln SECFilingsAgenticRag.slnx add <csproj>`, or by editing the `.slnx` by hand. Apply the CPM rule: strip versions, move them into `Directory.Packages.props`, and align every Aspire version, including the SDK attribute.
   Check: `dotnet build` succeeds with no NU1008.
4. **Write `AppHost.cs`, `AIModelResourceExtensions.cs` and the AppHost `appsettings.json`** (§6.4). Check:
   ```powershell
   aspire start
   aspire wait postgres
   aspire wait storage
   aspire describe
   ```
   `aspire describe` lists `postgres`, `secfilings`, `storage`, `filings`, `ollama` and the three parameters. Finish with `aspire stop`. Fallback if `aspire start` is unavailable: run `dotnet run --project src\SECFilingsAgenticRag.AppHost` in a separate terminal.
5. **Create Common** with `dotnet new classlib -o src\SECFilingsAgenticRag.Common -n SECFilingsAgenticRag.Common`, then apply the CPM rule, delete `Class1.cs` and add the packages. Implement the files in this order:
   - `Filings/FilingMetadata.cs`
   - `Filings/FilingBlobConventions.cs`
   - `VectorStore/FilingChunkRecord.cs`
   - `VectorStore/VectorStoreOptions.cs`
   - `VectorStore/FilingChunkMetadataKeys.cs`
   - `VectorStore/FilingChunkSchema.cs`
   - `AI/*Options.cs`
   - `AI/EmbeddingKeys.cs`
   - `AI/OllamaConnectionString.cs`
   - `AI/AIServiceCollectionExtensions.cs`
   - `VectorStore/PgVectorSetup.cs`
   - `VectorStore/VectorStoreServiceCollectionExtensions.cs`

   Before writing the definition and registration code, verify the current names of `VectorStoreCollectionDefinition`, `VectorStoreKeyProperty`, `VectorStoreDataProperty`, `VectorStoreVectorProperty`, `DistanceFunction`, `IndexKind`, `PostgresVectorStore` and `GetCollection<TKey,TRecord>(name, definition)` with the Microsoft Learn MCP (search "Microsoft.Extensions.VectorData record definition" and "Postgres vector store connector").
   Check: `dotnet build` succeeds.
6. **Write `docs/notes/phase-00-notes.md`**, including the "Commands & operating notes" section described in §7.1. Do **not** touch `CLAUDE.md`.

### 7.1 "Commands & operating notes" section of `phase-00-notes.md`

This section is the hand-off that later agents read, and the material the user will distil into `CLAUDE.md` by hand. Verify every command you list. It must cover:

- **One-paragraph project summary:** an SEC filings RAG to agentic RAG demo; .NET 10; Aspire; Postgres + pgvector; Azurite; native Ollama.
- **Pointer to `docs/`:** read `README.md` first; contracts live in `docs/01-architecture-and-design.md`; phase specs live in `docs/specs/`, phase notes in `docs/notes/`.
- **Layout:** the tree from docs/02 §3. Mark folders that don't exist yet as "(created in Phase NN)".
- **Commands:**
  - `dotnet build SECFilingsAgenticRag.slnx`
  - `aspire run` (interactive) or `aspire start` / `aspire wait <res>` / `aspire logs <res>` / `aspire describe` / `aspire stop` (background, agent-friendly)
  - `ollama pull nomic-embed-text`, `ollama pull qwen3:4b`
  - Later phases (mark them "(from Phase 01)" etc.):
    - `dotnet user-secrets set "Parameters:sec-user-agent" "Your Name you@example.com" --project src/SECFilingsAgenticRag.AppHost` (from Phase 01; the `ingestion-init` job downloads filings on `aspire start`)
    - `./scripts/Invoke-Indexing.ps1` (from Phase 02; you request indexing)
    - `dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli`
- **Conventions:**
  - central package management;
  - libraries never reference hosts;
  - hosts register connection-bearing clients;
  - one `AddSecFilingsXxx(IServiceCollection, IConfiguration)` per library;
  - only the two keyed MEAI embedding generators (`EmbeddingKeys.Document` / `EmbeddingKeys.Query`) embed text;
  - built-in first: no custom interface, wrapper or framework where an SDK type exists (see docs/02 "Custom types" table);
  - **model names, dimensions and endpoints come from configuration only**: host `appsettings.json` → env vars (`AI__Chat__Model`, …) → Aspire parameters (`Parameters:chat-model` etc. in the AppHost `appsettings.json`);
  - CPM: every scaffold → strip `Version=` and move it to `Directory.Packages.props`; all Aspire versions, including the AppHost SDK attribute, in step;
  - no model names in C#, comments included;
  - Azurite blob port 10000 is fixed (it conflicts with any other running Azurite);
  - no secrets in git (use `dotnet user-secrets`);
  - PowerShell commands.
- **Gotchas:** the short list from §10.

## 8. Tests

No automated tests in this phase. All unit and integration tests are deferred to [Phase 09](phase-09-tests.md), which specifies this phase's `Common.Tests`. This phase is verified by `dotnet build` and the run-time checks in §9.

## 9. Acceptance criteria

1. **Clean build.**
   ```powershell
   cd <repo-root>
   dotnet build SECFilingsAgenticRag.slnx
   ```
   Expect 0 errors, and `src\SECFilingsAgent\` no longer exists.
2. **The configured embedding model returns the configured dimensions.** Nothing in this phase calls the model through our code yet, so check Ollama directly with the AppHost's `embedding-model` and `embedding-dimensions` defaults:
   ```powershell
   $r = Invoke-RestMethod -Method Post http://localhost:11434/api/embed -ContentType application/json -Body '{"model":"nomic-embed-text","input":"What are Apple''s risk factors?"}'
   $r.embeddings[0].Count
   ```
   Expect `768`. A mismatch means `Parameters:embedding-dimensions` is wrong. From Phase 02 on, the keyed generators' dimension check (§6.3) enforces this at run time.
3. **Aspire (agent-executable).**
   ```powershell
   aspire start
   aspire wait postgres; aspire wait storage
   aspire describe          # postgres, secfilings, storage, filings, ollama, chat-model, embedding-model, embedding-dimensions
   $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1   # Aspire container names start with the resource name
   docker exec $pg psql -U postgres -d secfilings -c "SELECT name, default_version FROM pg_available_extensions WHERE name = 'vector';"
   ```
   Expect one row (`vector | 0.8.x`). The extension is *available*; it gets *created* by the initializer in Phase 02. If several containers match, pick the one whose image is `pgvector/pgvector:pg17`; don't pick a leftover container from another run. Optional human check: open the dashboard URL printed by `aspire start`.
4. **Model names are configuration-only, comments included.**
   ```powershell
   Get-ChildItem src -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
     Where-Object FullName -notmatch '\\(obj|bin)\\' |
     Select-String -Pattern 'qwen|nomic|llama|gpt-'
   ```
   Expect **no output**. Model names appear only in `appsettings.json` files.
5. **The environment-variable override takes effect.**
   ```powershell
   aspire stop
   ${env:Parameters__chat-model} = 'llama3.2'
   aspire start; aspire wait postgres
   aspire describe chat-model | Select-String 'llama3.2'
   aspire stop; Remove-Item 'env:Parameters__chat-model'
   ```
   Expect a match. If `describe` doesn't print parameter values in this CLI version, verify instead in Phase 02, where the Function's environment shows `AI__Chat__Model`. Record which method you used.
6. `docs/notes/phase-00-notes.md` exists, with a verified "Commands & operating notes" section. `CLAUDE.md` is unchanged.

## 10. Gotchas

- **Timeouts.** `HttpClient` defaults to a 100 s timeout. ServiceDefaults add a 10 s/30 s resilience handler to every `IHttpClientFactory` client. Common's Ollama client is therefore a hand-made `HttpClient` with `TimeoutSeconds`.
- **Embeddings stay consistent.** Only the two keyed MEAI generators embed text (document vs query prefix). The collection name contains the model and dimension, so switching models creates a new, empty table rather than mixing vectors.
- **Prefixes.** The stored `Text` never contains `search_document: `. Only the embedding input does.
- **`timestamptz` needs UTC.** Set `FilingDate = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)`.
- **Npgsql type loading.** If the `vector` extension is created after the data source has loaded types, call `ReloadTypesAsync()` on the connection. Otherwise you get "type vector does not exist" or mapping errors.
- **Data volume and generated password.** Aspire generates the Postgres password and persists it in the AppHost's user secrets. If you delete the user secrets but keep the volume, Postgres rejects the new password. Fix it with `docker volume rm <volume>`; find the name with `docker volume ls`.
- **Aspire SDK version.** `Aspire.AppHost.Sdk/<version>` lives in the `<Project Sdk=...>` attribute, not in CPM. Keep it equal to the `Aspire.*` package versions; skew between, say, 13.2.2 and 13.6.x causes confusing build and runtime errors.
- **Postgres identifiers max 63 chars.** Longer names are silently truncated by Postgres, which `CollectionName` prevents. pgvector HNSW supports at most 2000 dimensions, hence `[Range(1, 2000)]`.
- **`dotnet new` + CPM gives NU1008.** Always strip `Version=` after scaffolding.
- **PowerShell `**` is not recursive in `-Path`.** Use `Get-ChildItem -Recurse`.
- **`OllamaApiClient` constructor and interface names change between OllamaSharp versions.** Verify against the installed version.
- **Don't name the Ollama connection anything other than `ollama`.** Later phases rely on `ConnectionStrings:ollama`.
- **No model names in C#.** `Model`/`Provider`/`Dimensions` are required config. A host that forgets its `AI` section fails fast with `OptionsValidationException`, which is the intended behavior.
- **The parameter name `embedding-dimensions` is a string.** Aspire parameters are strings; the env var `AI__Embedding__Dimensions=768` binds to `int` through the configuration binder.
- **`ValidateOnStart` only fires when a host starts.** Resolving `IOptions<T>.Value` from a plain `ServiceProvider` triggers validation instead.

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

- `dotnet build SECFilingsAgenticRag.slnx` is clean (no errors; warnings acceptable but listed if new).
- `docs/notes/phase-00-notes.md` written, covering:
  - what was built;
  - every deviation from this spec;
  - chosen package versions (copy the `Directory.Packages.props` lines), including the Aspire SDK version;
  - whether `WithBlobPort` worked and how the check-5 override was verified;
  - any suppressed diagnostic IDs;
  - whether `WithLifetime(Persistent)` was used;
  - anything Phase 01 must know.
- Notes written, including "Commands & operating notes". `CLAUDE.md` untouched. `README.md` is touched only if a documented command changed.
- Suggested commit message:
  ```
  Phase 00: foundation — flattened solution, Aspire AppHost (pgvector, Azurite, Ollama conn string), SECFilingsAgenticRag.Common contracts
  ```
