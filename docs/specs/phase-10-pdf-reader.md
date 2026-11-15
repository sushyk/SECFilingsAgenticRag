# Phase 10: PDF filings reader (Open/Closed showcase)

> Depends on: Phases 00–07 | Est. size: S

This phase spec is self-contained: the rules, conventions and shared contracts you need are below. Notes go to `docs/notes/phase-10-notes.md`.

## Rules for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-10-notes.md`, under a `## Commands & operating notes` heading.
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

- **Authority.** This spec and `docs/01-architecture-and-design.md` are authoritative. If the code on disk conflicts, **the code on disk wins**; record the deviation in `docs/notes/phase-10-notes.md`.
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


**Goal.** Index PDF filings, for example annual reports from investor-relations sites, **without editing `IndexingService`**.

**Why.** It proves the Open/Closed design. A new format is one class plus one registration line, and the pipeline is untouched. That's a crisp interview story.

**Design.**
- Package: `PdfPig` (namespace `UglyToad.PdfPig`). Verify the package id and the latest version.
- `src/SECFilingsAgenticRag.IngestionPipeline/Readers/PdfFilingReader.cs : IngestionDocumentReader`.
  1. Open the stream with `PdfDocument.Open(stream)`.
  2. For each page, extract the text. Prefer `ContentOrderTextExtractor`, or the page's word-based layout, from `UglyToad.PdfPig.DocumentLayoutAnalysis`.
  3. Build Markdown: `## Page {n}` per page. Then apply the same "Item N." heading promotion the HTML reader uses (reuse the helper, e.g. `SecItemHeadingPromoter`; extract it to a shared internal class if needed), so sections read `Item 1A. Risk Factors` where detectable, falling back to `Page n`.
  4. Delegate the Markdown to the existing `MarkdownReader` to produce the `IngestionDocument`, exactly as `HtmlFilingReader` does.
- Registration, the **only** change in `AddSecFilingsIndexing`: `services.AddKeyedSingleton<IngestionDocumentReader, PdfFilingReader>("application/pdf");`
- Data side:
  - **No new tool.** Drop the PDF and a hand-written sidecar into `data/filings/<TICKER>/<FORM>/`, e.g. `data/filings/AAPL/10-K/PDF-1a2b3c4d.pdf` plus `PDF-1a2b3c4d.metadata.json`. The sidecar is `FilingMetadata` JSON (camelCase, `FilingMetadataJson` format). Use a synthetic accession like `PDF-<sha8>` when there isn't a real one:
    ```json
    { "ticker": "AAPL", "companyName": "Apple Inc.", "cik": "0000320193", "formType": "10-K",
      "accessionNumber": "PDF-1a2b3c4d", "filingDate": "2025-10-31", "reportDate": "2025-09-27", "fiscalYear": 2025,
      "primaryDocument": "aapl-annual-report-2025.pdf", "sourceUrl": "https://investor.apple.com/<report>.pdf" }
    ```
    The IngestionInit download step only fetches tickers that are *missing*, so it leaves hand-added files alone.
  - **IngestionInit** maps extension → media type with a dictionary (`.htm`/`.html` → `text/html`, `.pdf` → `application/pdf`). If the init job hard-codes `.htm`, generalizing this is the only allowed init job edit.

**Steps.**
1. Write the reader.
2. Register the reader.
3. Make the init job's extension → media-type mapping change, and add one hand-written PDF + sidecar.
4. Index: `aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire wait indexing; ./scripts/Invoke-Indexing.ps1; aspire wait api`. If the stack is already running, use `aspire resource indexing reindex` (the dashboard command from Phase 02).

**Acceptance.**
- `git diff --stat` shows **no changes** to `IndexingService.cs`.
- After adding one PDF, running the init job and `./scripts/Invoke-Indexing.ps1`, `/api/search` with the filter on that ticker returns chunks whose `DocumentId` is the PDF document, with page or Item sections:
  ```powershell
  $body = @{ query = "risk factors"; top = 5; tickers = @("AAPL") } | ConvertTo-Json -Depth 5
  (Invoke-RestMethod -Method Post http://localhost:5180/api/search -ContentType 'application/json' -Body $body).hits |
    Select-Object documentId, section
  ```
- `aspire logs ingestion-init` shows the `.pdf` uploaded with content type `application/pdf`.
- An unknown media type still fails that single document with `NotSupportedException` (reported as `Failed`) and doesn't stop the others.

**Gotchas.**
- Scanned PDFs have no text layer. OCR is out of scope: detect pages with no text and log a warning.
- Multi-column layouts can interleave text. The layout-analysis extractors help.
- PDF tables come out as flat text; accept this.
- Ligatures and hyphenation produce odd tokens, which matter for keyword search. Normalize `ﬁ`/`ﬂ` and join hyphenated line breaks.
