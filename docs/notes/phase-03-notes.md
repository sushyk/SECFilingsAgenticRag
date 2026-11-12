# Phase 03 notes: Retrieval library (query phase) + Search API

## What was built
- `src/SECFilingsAgenticRag.RetrievalPipeline` (host-agnostic; references Common only):
  - `RetrievalOptions` (section `Retrieval`: `Strategy`, `DefaultTop`, `MaxTop`, `ExcludeInstructionLikeChunks`, `ClampTop`; constants `VectorStrategy`, `TopLimit = 50`).
  - `FilingFilters.Build` turns tickers, form types and fiscal years into one MEVD filter expression. Values are normalized and captured as arrays, the parts are joined with `AndAlso` by rebinding the parameter, and `!ContainsInstructionLikeText` is added by default.
  - `FilingCitation.Title`: `"Apple Inc. 10-K FY2025 — Item 1A. Risk Factors"`.
  - `FilingSearch.SearchFilingsAsync`: the one search function, an extension method on the built-in `IVectorSearchable<FilingChunkRecord>`. It validates the query (non-blank, at most 2,000 characters after trimming), clamps `top`, builds the filter, runs one `RetrievalPipeline.Process` span on the `SECFilingsAgenticRag.RetrievalPipeline` ActivitySource, and sorts by score descending.
  - `AddSecFilingsRetrieval`: options with DataAnnotations plus a strategy check through `IServiceProviderIsKeyedService`, `ValidateOnStart`. It registers keyed `"Vector"` (the collection itself) and the unkeyed `IVectorSearchable<FilingChunkRecord>` resolved from `Retrieval:Strategy`.
- `src/SECFilingsAgenticRag.AgentServer` (ASP.NET Core minimal API):
  - Endpoints: `POST /api/search`; `/health` and `/alive` from ServiceDefaults; `/openapi/v1.json` in Development.
  - Errors: ProblemDetails, `UseExceptionHandler`, `UseStatusCodePages`.
  - Request validation: the built-in `AddValidation()`.
  - Startup creates the collection (`EnsureCollectionExistsAsync`).
  - Port: fixed **5180** through `launchSettings.json`.
  - Wire DTOs: `SearchRequest`, `SearchHit`, `SearchResponse` in `Endpoints/SearchEndpoints.cs`.
- AppHost: resource `api` (`WithReference(db).WaitFor(db)`, `WithReference(ollama)`, `AI__Chat__Model` from the existing `chat-model` parameter, `WithExternalHttpEndpoints`). It waits for nothing else.

## How the Microsoft.Extensions.DataRetrieval proposal was used
The user asked to build on the draft proposal [dotnet/extensions#7507](https://github.com/dotnet/extensions/issues/7507) (draft PR [#7508](https://github.com/dotnet/extensions/pull/7508)). It is the read-side mirror of MEDI's `IngestionPipeline`. **Decision: adopt its architecture and vocabulary, not its types.**

**Why the types were not used.**
- The proposal isn't on NuGet: `dotnet package search Microsoft.Extensions.DataRetrieval --prerelease` returns nothing.
- Its `RetrievalPipeline.ProcessAsync(collection, query, topK, contentSelector)` takes no `VectorSearchOptions`. It can't express the ticker, form or year filters, nor the flagged-chunk exclusion.
- It reflects records into an untyped `IDictionary<string, object?>` and de-duplicates by content. Phases 4–6 consume typed `VectorSearchResult<FilingChunkRecord>`.
- Copying the draft into the repo would also break the built-in-first rule.

**What was adopted.**

| Proposal (`Microsoft.Extensions.DataRetrieval`) | This repo | Phase |
|---|---|---|
| `RetrievalPipeline.ProcessAsync` (query → search → results) | `FilingSearch.SearchFilingsAsync` on the built-in `IVectorSearchable<FilingChunkRecord>` | 3 |
| "Zero processors = raw vector search" | Unkeyed `IVectorSearchable` = keyed `"Vector"` = the `VectorStoreCollection` itself | 3 |
| `RetrievalPipelineOptions.ActivitySourceName` + span `RetrievalPipeline.Process`, tags `rag.topK`, `rag.results.count` | ActivitySource `SECFilingsAgenticRag.RetrievalPipeline`, same span name and tags, plus `rag.strategy` (no query text: it is sensitive) | 3 |
| `RetrievalQueryProcessor` (expansion, rewrite) | `MultiQueryVectorSearch` decorator behind the unkeyed `IVectorSearchable` | 6 |
| `RetrievalResultProcessor` / `IReranker` | `RerankingVectorSearch` decorator | 6 |
| RRF for multi-variant results | `RrfFusion` (hybrid and multi-query) | 6 |
| `IRetriever` | not needed: the built-in `IVectorSearchable<T>` is the DI and test seam | — |

Once the package ships with filter support, the migration is mechanical. The decorators become processors and `SearchFilingsAsync` becomes `ProcessAsync`. Consumers keep calling one function.

## Results (acceptance criteria, §9)
**How they were run.** Your own `aspire run` (bundled CLI 13.6.4, started 08:11) was running the AppHost, with `indexing` and 824 chunks indexed. The tool permission system denied stopping it. So every check ran against the AgentServer started **outside Aspire** with `dotnet run` (port 5180) or the built DLL (port 5181). The same Postgres container and native Ollama were used, with `ConnectionStrings__secfilings` / `ConnectionStrings__ollama` set by hand. That is the spec's own check-7 path. Only check 9 (the `api` resource under Aspire) is not verified (see deviation 13).

| # | Check | Result |
|---|---|---|
| 1 | `/health` | **Pass**: `Healthy` (includes Aspire.Npgsql's Postgres check) |
| 2 | Unfiltered search, `"Apple supply chain risk"`, top 5 | **Partial.** Strategy `Vector`; 5 hits; all from `Item 1A. Risk Factors`; scores 0.69, 0.69, 0.68, 0.67, 0.67 (in (0,1], descending). **Only 2 of 5 are AAPL** (spec expects at least 3); at top 10: AAPL 4, AMZN 2, NVDA 2, MSFT 1, TSLA 1. Cause: 10-K text says "the Company", never "Apple", and only `content` is embedded, so the company identity isn't in the vector. Remedy: the ticker filter (check 3) and Phase 5's agent passing `tickers`; Phase 6's hybrid search and evals; Phase 14 enrichment (prefix company name to embedded text). Not a retrieval-code defect |
| 3 | `tickers=["MSFT"]` | **Pass**: `MSFT` only. Normalization: `"aapl "` → only AAPL |
| 4 | `{"query":""}` | **Pass**: 400 `application/problem+json`, `errors.Query = ["The Query field is required."]`; `top=99` → 400 `"The field Top must be between 1 and 50."` |
| 4a | Flagged chunk excluded | **Pass**: AAPL chunk `13bdf084-…` set `containsinstructionliketext=true` → searching its own text returns hits without it (`False`); restored (0 flagged rows). Returned `text` equals stored `content`; no table pipes |
| 4b | Score semantics | **Pass (semantics), spec threshold not reachable**: top-1 for the chunk's first 200 chars is the same key, score **0.918**; first 1,990 chars → **0.979**; unrelated query ("chocolate cake recipe…") → 0.484. `Score` is cosine **similarity** (higher is better); a distance would be near 0. It stays below 0.99 because queries carry `search_query: ` and documents `search_document: ` (asymmetric nomic prefixes), and the query is truncated by the 2,000-char limit (the chunk is 3,022 chars) |
| 4b | `tickers=["AAPL","MSFT"]`, top 10 | **Pass**: `AAPL,MSFT`. `Contains` on a captured array translates (no fallback, no `PerValueFilterVectorSearch`) |
| 5 | Latency | **Pass**: first request 2.9 s (Ollama load), warm 43–83 ms end to end (faster than the spec's 150–600 ms) |
| 6 | `dotnet build SECFilingsAgenticRag.slnx` | **Pass**: 0 warnings, 0 errors (built with `--artifacts-path` to a scratch folder, because the running Functions host locks its `bin`) |
| 7 | Config override / no hard-coded model | **Adapted** (deviation 16). Without `AI:Chat:Model` (scratch content root without the `AI` section) → startup fails: `InvalidOperationException: AI:Chat:Model is not configured.` With Ollama unreachable → `POST /api/search` returns **500 `application/problem+json`** |
| 8 | `Retrieval__Strategy=Bogus` | **Pass**: startup fails, `OptionsValidationException: Retrieval:Strategy must be one of: Vector.` (`Retrieval__MaxTop=500` → `The field MaxTop must be between 1 and 50.`) |
| 9 | `api` under Aspire on 5180 | **Not verified** (deviation 13) |
| — | Writer compatibility (spec §10, Phase 02 notes "For Phase 03") | **Pass**. In a scratch database (`secfilings_p3check`, dropped afterwards), the table was created by `EnsureCollectionExistsAsync` from `FilingChunkRecord`, then MEDI `VectorStoreWriter<string>` (same options and 12 metadata keys as Phase 02) wrote one document twice: 2 rows (incremental replace, no duplicates), `vector(768)`, `FilingDate` read back as `Kind=Utc`. So AgentServer can create the table before the first index run |
| — | Model-name grep | Only `Common/AI/EmbeddingModelConstants.cs` (Phase 00 constant, pre-existing); nothing in this phase's code |
| — | OpenAPI | `/openapi/v1.json` lists `/api/search` with responses `200, 400` |

**Observed table** `sec_filing_chunks` (schema `public`):
- **Columns:** `key uuid`, `content`, `context`, `documentid`, `containsinstructionliketext boolean`, `ticker`, `companyname`, `cik`, `formtype`, `fiscalyear integer`, `filingdate timestamptz`, `sourceurl`, `section`, `chunkindex integer`, `chunkcount integer`, `indexfingerprint`, `embedding vector(768)`.
- **Indexes:** HNSW `vector_cosine_ops`; btree on `documentid`. AgentServer's `EnsureCollectionExistsAsync` added btree indexes on `ticker`, `formtype`, `fiscalyear` and `containsinstructionliketext` to the existing table (`CREATE INDEX IF NOT EXISTS`, from `IsIndexed`), which serve the filters.

**Search path:** `SearchAsync(string, …)` embeds the text through the store's keyed **query** generator (Phase 00 verified that the prefix is applied). No fallback is needed.

**Fixed port:** the `launchSettings.json` `applicationUrl` approach is in place and works for `dotnet run`. Not yet confirmed under Aspire (deviation 13).

## Package versions added
- `Microsoft.AspNetCore.OpenApi` **10.0.14**: new; latest stable, in line with the other `10.0.14` packages.
- `Aspire.Npgsql` 13.6.4 and `Microsoft.Extensions.Options.DataAnnotations` / `.ConfigurationExtensions` 10.0.14 were already in `Directory.Packages.props` from Phase 02.
- `AddValidation` ships in the ASP.NET Core shared framework, so it needs no package.
- No diagnostics are suppressed.

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `FilingFilters` | `VectorSearchOptions<T>.Filter` (**used**) | Translating HTTP and tool parameters into that expression is domain code |
| `FilingCitation` | — | Citation wording is domain-specific |
| `FilingSearch` (extension method) | `IVectorSearchable<T>` (**used**), DataRetrieval `RetrievalPipeline` (unpublished, no filter) | One place for query validation, filters, the flagged-chunk exclusion, clamping and the span |
| `RetrievalOptions` | Options pattern (**used**) | Our configuration |
| `SearchRequest`/`SearchHit`/`SearchResponse` | — | The HTTP contract must not expose vectors or bookkeeping columns |
| *(not added)* `RetrievalOptionsValidator`, `PerValueFilterVectorSearch` | `OptionsBuilder.Validate<TDep>` + `IServiceProviderIsKeyedService`; connector's `Contains` translation | Built-ins covered both |

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | `EmbeddingKeys.Query`, `FilingChunkSchema.CollectionName`, `AddSecFilingsVectorStore(config, key)`, `PgVectorSetup`, `WithAIModels` | `EmbeddingModelConstants.QueryEmbeddingKey`, table `sec_filing_chunks`, `AddSecFilingsVectorStore(key)` (it enables the extension itself), `.WithEnvironment("AI__Chat__Model", chatModel)` | Code on disk wins (Phase 00 simplification; Phase 02 deviation 1) |
| 2 | `AI:Embedding` section in AgentServer `appsettings.json` | Only `AI:Chat:Model` (required by `AddSecFilingsAI`) | The embedding model is a compile-time constant; there is no such configuration to bind |
| 3 | `RetrievalOptions.Rerank/MultiQuery/Hybrid` + `Describe()` + "not available until Phase 6" validation | Omitted. `SearchResponse.strategy` and the span report `Retrieval:Strategy` | CLAUDE.md "every line has purpose": no Phase 3 code reads them, and `Describe()` could only ever return `Strategy`. Phase 6 adds the sub-options, `Describe()` and the composition together with the decorators. Unbound keys in config are ignored, so the Phase 6 JSON shape is unaffected |
| 4 | Custom `RetrievalOptionsValidator : IValidateOptions` + static name list; case-insensitive strategy names | Built-in `OptionsBuilder.Validate<IServiceProviderIsKeyedService>(…, message)`; names match **exactly** (`Vector`) and the message lists them | No custom validator class; DI keys are case-sensitive, and normalizing would need a second registry. Wrong case fails fast with the valid names |
| 5 | Single value → `==`, multiple → `Contains` | Always `Contains` on a captured array | One code path; the connector translates `Contains` (Learn row "Contains … over array properties"; verified by checks 3 and 4b) |
| 6 | Manual validation, `Results<Ok<SearchResponse>, ValidationProblem>` | Built-in .NET 10 minimal-API validation: `AddValidation()` + `[Required, StringLength(2000)]` / `[Range(1, 50)]` on `SearchRequest`; handler returns `Ok<SearchResponse>`, `.ProducesValidationProblem()` documents the 400 | Built-in first (Learn: ASP.NET Core 10 validation). Same 400 ProblemDetails body. `FilingSearch` still throws `ArgumentException` for in-process callers (tools) |
| 7 | `SearchHit.Score` is `double` | `double?` | Mirrors `VectorSearchResult.Score`; no invented 0 |
| 8 | `IncludeVectors = false` | Not set | Learn: vectors are excluded by default |
| 9 | `ConfigureHttpJsonOptions` with camelCase + `JsonStringEnumConverter` | Not added | camelCase is already the ASP.NET Core web default; no enum is on the wire yet. **Phase 4 must add the converter when the first enum crosses the wire** (cross-cutting convention) |
| 10 | Information log inside `SearchFilingsAsync` | `SearchFilingsAsync` emits the span; `/api/search` logs one Information line (query truncated to 80 chars, filters, strategy, hit count, elapsed ms) | A static extension method has no logger; adding an `ILogger` parameter to every consumer's call would be noise. Phase 4/5 consumers get the span |
| 11 | Implement as specified | Architecture of the DataRetrieval proposal adopted (section above) | User request; types unpublished and lack filters |
| 12 | Template files | Deleted `appsettings.Development.json` (duplicate logging section), `AllowedHosts`, the https profile and `launchBrowser` | No purpose here |
| 13 | §7 step 9 / §9: `aspire start … aspire wait api`, check 9 | Not run: the user's AppHost was running, and stopping it (`aspire stop`) was denied by the tool permission policy. Everything else verified outside Aspire. **To verify:** restart the AppHost (below), then `aspire describe api` and `Invoke-RestMethod http://localhost:5180/health`. If 5180 is not honoured, use `.WithEndpoint("http", e => e.Port = 5180)` (spec §6.8) | Not my workload to stop |
| 14 | Check 2: at least 3 of 5 hits AAPL | 2 of 5 | See Results, check 2: corpus and embedding property, not code |
| 15 | Check 4b: identical text ≥ 0.99 | 0.918 (200 chars) / 0.979 (1,990 chars) | Asymmetric query/document prefixes; truncated query. Similarity semantics confirmed |
| 16 | Check 7: `AI__Embedding__Model=does-not-exist` → 500 | Not applicable (embedding model is a constant). Substituted: missing `AI:Chat:Model` → startup failure; unreachable Ollama → 500 ProblemDetails | Phase 00 simplification. The failure surfaces as `InvalidOperationException`, not `OptionsValidationException`, because Phase 00 reads `AI:Chat:Model` directly |
| 17 | Phase 02 notes: "search must return empty results instead of failing" before the first index | AgentServer creates the table at startup (spec §6.7); writer compatibility verified (Results) | Simpler than a per-request `CollectionExistsAsync` check |

## Anything Phase 04 must know
- Call `search.SearchFilingsAsync(retrievalOptions, query, tickers, formTypes, fiscalYears, top, ct)` on the **unkeyed** `IVectorSearchable<FilingChunkRecord>`. It throws `ArgumentException` for a blank query or one over 2,000 characters (map to an error string in tools).
- The citation is `FilingCitation.Title(hit.Record)` and the link is `hit.Record.SourceUrl`. Cite `documentid` + `section`, never `key`.
- `RetrievalOptions` has no `Describe()` yet (deviation 3). Report `retrievalOptions.Strategy`.
- Add `JsonStringEnumConverter` to `ConfigureHttpJsonOptions` with the first wire enum (deviation 9).
- Register additional tracing sources next to `FilingSearch.ActivitySourceName` in `Program.cs`. ServiceDefaults only adds the application's own source, so MEAI's `Experimental.Microsoft.Extensions.AI` spans are not exported yet (Phase 7).
- Company names are not in the embedded text. Questions about one company retrieve better with `tickers` set.

## Commands & operating notes
- **Run under Aspire** (CLI must match the AppHost: use the bundled one, or `aspire update`):
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  & $aspire stop --apphost src/SECFilingsAgenticRag.AppHost   # if one is already running
  dotnet build SECFilingsAgenticRag.slnx
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost
  & $aspire wait api
  & $aspire describe api          # expect Running on http://localhost:5180
  ```
  Don't build while an AppHost runs: the Functions DLLs are locked (Phase 02 gotcha). To check the build only, use `dotnet build SECFilingsAgenticRag.slnx --artifacts-path $env:TEMP\secfilings-artifacts`.
- **Run the API alone** (Postgres container up, native Ollama):
  ```powershell
  $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
  $port = (docker port $pg 5432) -replace '.*:', ''
  $pw = docker exec $pg sh -c 'printf %s "$POSTGRES_PASSWORD"'
  $env:ConnectionStrings__secfilings = "Host=127.0.0.1;Port=$port;Username=postgres;Password=$pw;Database=secfilings"
  $env:ConnectionStrings__ollama = "http://localhost:11434"
  dotnet run --project src/SECFilingsAgenticRag.AgentServer      # http://localhost:5180; set ASPNETCORE_URLS for another port
  ```
- **Smoke test:**
  ```powershell
  $body = @{ query = "Apple supply chain risk"; top = 5; tickers = @("AAPL") } | ConvertTo-Json -Depth 5
  $r = Invoke-RestMethod -Method Post -Uri http://localhost:5180/api/search -ContentType 'application/json' -Body $body
  $r.strategy; $r.hits | Select-Object ticker, citationTitle, @{n='score';e={[math]::Round($_.score,3)}}
  ```
  Filters: `tickers`, `formTypes` (both case-insensitive, trimmed), `fiscalYears`; `top` 1..50, clamped to `Retrieval:MaxTop`.
- **Configuration** (env vars override `appsettings.json`): `Retrieval__Strategy` (exact name, `Vector`), `Retrieval__DefaultTop`, `Retrieval__MaxTop` (1..50), `Retrieval__ExcludeInstructionLikeChunks`, `AI__Chat__Model`.
- **psql inside the container** needs the password: `docker exec -e PGPASSWORD=$pw $pg psql -U postgres -d secfilings -c "…"`. Without it psql prompts and fails ("fe_sendauth: no password supplied").
- **Gotchas:**
  - Queries over 2,000 characters are rejected (400). Whole chunks can be longer (up to about 3,100 characters).
  - Scores near 0.7 are normal for short queries against nomic embeddings. A near-identical passage scores about 0.98, not 1.0, because of the asymmetric prefixes.
  - The first request after startup takes about 3 s (Ollama loads `nomic-embed-text`).
  - Search before the first indexing run returns `hits: []`, not an error.
  - `/health` and `/alive` are mapped in Development only (ServiceDefaults).

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/postgres-connector | Supported filter clauses (`Contains` over arrays, `!`, `&&`), `CosineSimilarity` supported, HNSW only, `UseVector()`, `IsIndexed` → btree | `Contains` on captured arrays, `!ContainsInstructionLikeText`, no fallback decorator; the btree indexes created at startup serve the filters (RAG guide: information retrieval, filters) |
| 2 | https://learn.microsoft.com/dotnet/ai/vector-stores/vector-search | `SearchAsync(string, top, options)` embeds text with the store's generator; `Filter` is applied before the vector comparison; vectors are excluded by default | Query passed as text (no manual prefix or embedding); `IncludeVectors` not set (deviation 8) |
| 3 | https://learn.microsoft.com/dotnet/ai/vector-stores/how-to/use-vector-stores | `EnsureCollectionExistsAsync` creates the collection if missing | AgentServer startup; writer compatibility verified |
| 4 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.iserviceprovideriskeyedservice.iskeyedservice | `IsKeyedService(Type, object?)` | Strategy validation without a registry (deviation 4) |
| 5 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-information-retrieval | "Run the same cleaning operations on the query", "use the same embedding model"; filters; reranking adds latency | The query goes through the same keyed generator family as documents (only the task prefix differs); filters in P3; rerank stays a P6 option |
| 6 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide | Information retrieval phase: 5.1 index, 5.2 search options incl. filters, 5.3 evaluate | This phase = 5.1 (HNSW + btree) and 5.2 (vector + filters); 5.3 in Phase 6 |
| 7 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-agentic#design-retrieval-as-a-tool | "Wrap your existing search logic in a function"; `query`, `top_k`, `filter` parameters | One `SearchFilingsAsync` with exactly those parameters, reused by the Phase 5 tool |
| 8 | https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0#minimal-apis and https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis#validation-support-in-minimal-apis | `AddValidation()`, DataAnnotations on record parameters, automatic 400 | **Changed a decision**: built-in validation instead of the spec's manual checks (deviation 6) |
| 9 | https://learn.microsoft.com/aspnet/core/fundamentals/validation | Source generator only discovers types in the assembly calling `AddValidation` | `AddValidation()` and `SearchRequest` both live in AgentServer |
| 10 | https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses and https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.typedresults.validationproblem | `TypedResults` provide OpenAPI metadata; `ValidationProblem` → 400 `HttpValidationProblemDetails` | `Ok<SearchResponse>` + `.ProducesValidationProblem()` (OpenAPI shows 200 and 400) |
| 11 | Learn search "vector store collection UseLogging UseOpenTelemetry" | Whether MEVD ships logging or telemetry decorators for search, like MEAI does | None found, so `FilingSearch` emits its own span, mirroring the DataRetrieval proposal |
| 12 | https://learn.microsoft.com/dotnet/aspire/troubleshooting/allow-unsecure-transport | Whether an `http` `applicationUrl` is a problem | Applies to the AppHost's own launch profile, not project resources; the `http`-only profile is fine |
| 13 | *external* [dotnet/extensions#7507](https://github.com/dotnet/extensions/issues/7507) and draft PR [#7508](https://github.com/dotnet/extensions/pull/7508) (`RetrievalPipeline.cs`, `VectorStoreRetriever.cs`, abstractions) | Proposal API, design principles, implementation | Architecture adopted, types not (section above) |
| 14 | *external* Pgvector 0.3.2 README; compiler | Namespace of `UseVector()` (not stated in the README) | `using Npgsql;` (the `Pgvector.Npgsql` namespace does not contain it) |

Docs vs reality:
- Learn doesn't state the PgVector connector's score direction for `CosineSimilarity`. Measured: similarity, higher is better (check 4b).
- The spec's ≥ 0.99 for identical text doesn't hold with nomic's asymmetric prefixes (deviation 15).

## Suggested commit message
```
feat(retrieval): add SECFilingsAgenticRag.RetrievalPipeline (vector search, filters, citations) and AgentServer POST /api/search

- FilingSearch.SearchFilingsAsync on the built-in IVectorSearchable<FilingChunkRecord>, shaped after the
  proposed Microsoft.Extensions.DataRetrieval pipeline (query -> search -> results, one traced span)
- FilingFilters (ticker/form/year + flagged-chunk exclusion), FilingCitation, RetrievalOptions with
  keyed-strategy validation
- AgentServer: minimal API with built-in validation, ProblemDetails, OpenAPI, health, fixed port 5180
- AppHost: api resource
```
