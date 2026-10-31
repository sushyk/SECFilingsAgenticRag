# Phase 11: Structured financial numbers tool (XBRL company facts)

> Depends on: Phases 00–08 (Phase 05 agentic agent, Phase 06 evals, Phase 08 MCP server: `agentic-rag` gets its tools over MCP) | Est. size: M

This phase spec is self-contained: the rules, conventions and shared contracts you need are below. Notes go to `docs/notes/phase-11-notes.md`.

## Rules for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-11-notes.md`, under a `## Commands & operating notes` heading.
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

- **Authority.** This spec and `docs/01-architecture-and-design.md` are authoritative. If the code on disk conflicts, **the code on disk wins**; record the deviation in `docs/notes/phase-11-notes.md`.
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
//   agentic-rag gets its tools over MCP from the `mcp` resource (McpToolsProvider → McpClient, Phase 08); it holds no tool code.
// SECFilingsAgenticRag.RetrievalPipeline.Tools (moved there in Phase 08) — FilingTools: SearchFilingsAsync / ListAvailableFilingsAsync
//   (model-facing text with cite tags), ListFilingsAsync → FilingSummary, AsTools() → "search_filings", "list_available_filings"; public CiteTag.
// SECFilingsAgenticRag.McpServer (resource `mcp`, port 5190) — serves those tools via McpServerTool.Create, plus resources and prompts.
public sealed record FilingSummary(string Ticker, string CompanyName, string FormType, int FiscalYear, string DocumentId, int ChunkCount);

// SECFilingsAgenticRag.AgentServer — HTTP surface:
//   POST /agents/{chat-only|standard-rag|agentic-rag}   AG-UI protocol (RunAgentInput with threadId → AG-UI SSE events), MapAGUI
//   POST /api/search  SearchRequest → SearchResponse { query, strategy, hits[], elapsed }      GET /api/filings → FilingSummary[]
```

**Aspire resources:** `postgres` → `secfilings`, `storage` → `filings`, `ollama` (connection string), `indexing` (Function, `POST /api/index`), `ingestion-init` (one-shot), `api` (port 5180), `mcp` (MCP server, port 5190, Phase 08). Common registration helpers: `AddSecFilingsAI`, `AddSecFilingsVectorStore(…, embeddingKey)`, `AddSecFilingsRetrieval`, `AddSecFilingsIndexing` (each on `IServiceCollection`), and `AddSecFilingsAgents` (on `IHostApplicationBuilder`). Hosts register `NpgsqlDataSource` (with `UseVector()`) and `BlobContainerClient` themselves.

---

## Phase specification


**Goal.** Give the agent a `get_financial_metric` tool that returns **exact reported numbers** (revenue, net income, …) from SEC XBRL data, so it picks between *text search* (narrative) and *structured data* (numbers).

**Why.**
- RAG over prose is poor at precise figures, because tables get chunked badly.
- Teaching the agent to route between tools is the essence of agentic design.

**Design.**
- Data source: `GET https://data.sec.gov/api/xbrl/companyfacts/CIK##########.json`, with the CIK zero-padded to 10 digits.
  - It's a **public SEC API**: a `User-Agent: <name> <email>` header is required, with at most 10 requests per second.
  - Read the User-Agent from configuration (`Edgar:UserAgent`, the same key as the downloader if one exists).
  - Set it with `HttpRequestHeaders.TryAddWithoutValidation("User-Agent", ...)`, because `"Name email@x"` fails `ProductInfoHeaderValue` parsing. For manual checks, use `Invoke-RestMethod ... -Headers @{ 'User-Agent' = '...' } -SkipHeaderValidation`.
- New code in `SECFilingsAgenticRag.McpServer/Financials/`. No new project: McpServer is the only consumer. Since Phase 08 the agent's tools are served by the MCP server, so `agentic-rag` discovers the new tool through `tools/list` with **no AgentServer code change** apart from the prompt line below. That is the payoff of Phase 08.
  - AppHost: give `mcp` the SEC User-Agent: `.WithEnvironment("Edgar__UserAgent", secUserAgent)`, the same secret parameter `ingestion-init` uses.
  ```csharp
  public sealed record FinancialFact(string Ticker, string Concept, string Label, int FiscalYear, decimal Value, string Unit,
      DateOnly PeriodEnd, string Form, string AccessionNumber, string SourceUrl);
  // Custom (no built-in fits): SEC companyfacts parsing. A concrete typed-HttpClient class, not an interface
  // (Phase 09's tests fake the HttpMessageHandler, so no abstraction is needed).
  public sealed class SecCompanyFactsClient(HttpClient http, IMemoryCache cache, IOptions<FinancialsOptions> options)
  {
      public Task<FinancialFact?> GetAnnualFactAsync(string cik, string ticker, string metric, int? fiscalYear, CancellationToken ct = default);
  }
  ```
- Metric → concept candidates (configuration, `Financials:Metrics`), tried in order:

  | Metric | Concept candidates |
  |---|---|
  | `revenue` | `Revenues`, `RevenueFromContractWithCustomerExcludingAssessedTax`, `SalesRevenueNet` |
  | `net_income` | `NetIncomeLoss` |
  | `operating_income` | `OperatingIncomeLoss` |
  | `total_assets` | `Assets` |
  | `rd_expense` | `ResearchAndDevelopmentExpense` |
  | `eps_diluted` | `EarningsPerShareDiluted` (unit `USD/shares`) |

- Fact selection (`facts.us-gaap.<concept>.units.USD[]`):
  1. Keep `form == "10-K"` and `fp == "FY"`, and duration facts about 1 year long (`end - start` between 350 and 380 days).
  2. Prefer the fact whose **`frame` == `$"CY{year}"`**. Otherwise choose the one whose `end` year equals the requested fiscal year, from the **latest `filed`**.
  3. If `fiscalYear` is null, use the latest.

  Why: a 10-K reports prior years too, so `fy` alone is ambiguous.
- Map ticker → CIK from the indexed records (`FilingChunkRecord.Cik`, via the built-in `collection.GetAsync(r => r.Ticker == t, top: 1)`) or from `company_tickers.json`.
- Cache responses in `IMemoryCache` for 12 h. The company facts JSON can be several MB.
- Tool, added to the **`agentic-rag`** tool set:
  ```csharp
  [Description("Get an exact annual financial figure reported to the SEC (XBRL). Use for numbers; use search_filings for explanations.")]
  Task<string> GetFinancialMetric(
      [Description("Ticker, e.g. MSFT")] string ticker,
      [Description("One of: revenue, net_income, operating_income, total_assets, rd_expense, eps_diluted")] string metric,
      [Description("Fiscal year, e.g. 2025; omit for latest")] int? fiscalYear = null);
  // returns e.g. "MSFT revenue FY2025: 281,724,000,000 USD (period ending 2025-06-30, 10-K 0000950170-25-xxxxx) — source: <url>"
  ```
  Make tools registration-only **without a new interface**:
  - in **McpServer**, register each tool as the built-in type: `services.AddSingleton<AITool>(sp => AIFunctionFactory.Create(..., new AIFunctionFactoryOptions { Name = "get_financial_metric", MarshalResult = <as-is, like FilingTools> }))`;
  - change Phase 08's `Configure<McpServerOptions>` loop to wrap `[.. sp.GetServices<AITool>()]` with `McpServerTool.Create`;
  - register `FilingTools.AsTools()`'s two tools the same way.

  Document this as a contract addition. `agentic-rag`'s `McpToolsProvider` lists whatever the server serves, so nothing changes there. Restart `api`, or let it reconnect, to pick up the new tool list.
- Update `Prompts.AgenticRag`: *"For exact figures use get_financial_metric; for explanations, risks or strategy use search_filings; for comparisons call tools per company."*
- Citations: the tool output carries a deterministic cite tag, e.g. `[MSFT-FY2025-XBRL-revenue]`, plus the `SourceUrl`, using the same cite-tag convention as `search_filings` (Phase 05). The model cites it like any excerpt; no recorder or per-turn state.

**Acceptance.**
- *"What was Microsoft's revenue in fiscal 2025 and what drove it?"* produces tool calls to **both** `get_financial_metric` and `search_filings`, and the answer states the exact figure. Check it agent-executably against the `agentic-rag` agent:
  ```powershell
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --agent agentic-rag --ask "What was Microsoft's revenue in fiscal 2025 and what drove it?" --show-tool-args |
    Select-String 'get_financial_metric|search_filings|toolCalls='
  ```
- `tools/list` against `http://localhost:5190/mcp` shows `get_financial_metric`, and `aspire logs mcp` (not `api`) logs its call during the run above.
- Add a golden question with `expectedToolCalls` for both tools, and confirm it passes Phase 06's MAF `EvalChecks` tool-use eval.
- Ask the agentic agent "What was Apple's total revenue in fiscal 2025?" and check the answer cites the companyfacts tool result. Phase 09 adds the fixture-based unit tests.

**Gotchas.**
- Concepts vary by company and year (`Revenues` vs `RevenueFromContract…`), so always use the fallback list.
- Fiscal years don't equal calendar years (MSFT ends in June, AAPL in September). Report `PeriodEnd`.
- Values are raw USD. Format with thousands separators, and keep the unit.
- Rate limits and the User-Agent rule apply. Small models may pass `"Revenue"` or `"sales"`, so normalize metric names, case-insensitively, with synonyms.
