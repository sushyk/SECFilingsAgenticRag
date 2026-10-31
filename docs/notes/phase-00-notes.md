# Phase 00 notes: Foundation and local infrastructure

## What was built
- Flattened layout: `SECFilingsAgent.slnx` at the root, `global.json` (SDK 10.0.400, `latestFeature`; resolves to 10.0.403), `Directory.Build.props`, `Directory.Packages.props` (CPM). `.gitignore` already had `data/filings/`, `.env`, `local.settings.json`.
- `src/SECFilings.ServiceDefaults` (template, CPM only), `src/SECFilings.AppHost` (Postgres `pgvector/pgvector:pg17`, Azurite + `filings` container, `ollama` connection string, `chat-model` parameter), `src/SECFilings.Core` (all §6.3 contracts), `src/SECFilings.Ingestion` (empty classlib, Phase 02), `tests/SECFilings.Core.Tests` (xUnit v2.9.3, 32 tests.
- `WithLifetime(ContainerLifetime.Persistent)` IS used on Postgres (faster restarts; the container survives AppHost stops, remove it with `docker rm -f`).

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | Move `SECFilings.Ingestion` | Disk had `SECFilings.Indexing` (placeholder csproj); I created `src/SECFilings.Ingestion` fresh and removed the old tree | docs/02 and later specs name the project `SECFilings.Ingestion`; the old project was an empty placeholder |
| 2 | Embedding middleware as an `EmbeddingGeneratorBuilder.Use(generateFunc)` lambda | `private sealed class PrefixingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>` | Spec allows the subclass "if it grows"; it needs the bound `EmbeddingOptions` from DI and serves both keys. Registered through `.Use((inner, sp) => ...)` |
| 3 | `AddSecFilingsAI(services, configuration)` only | Added an **internal** overload taking `embeddingProviderOverride` (test seam, `InternalsVisibleTo SECFilings.Core.Tests`) | Lets tests run the real middleware over a fake inner generator |
| 4 | AC6 grep `qwen\|nomic\|llama\|gpt-` expects no output | Matches the substring `llama` in "Ollama"/"ollama" (17 hits, all provider / connection-string names). Use `qwen\|nomic\|llama\d\|llama:\|gpt-` instead: no output | The regex is not case-sensitive in `Select-String`, and the provider is legitimately named Ollama. No real model name is in `src/` |
| 5 | Aspire CLI: `aspire start/wait/describe` | The installed CLI is **13.2.2**; the AppHost is 13.6.4. `aspire start` fails: the 13.6.4 AppHost rejects the CLI's socket directory `~/.aspire/cli/runtime/sockets` (needs an owner-only ACL). Used the spec's fallback `dotnet run --project src\SECFilings.AppHost` and checked via `docker ps` / `docker exec` | Fix is `aspire update` (CLI to 13.6.4), which I did not run since it changes the user's toolchain |
| 6 | `Aspire.Hosting.AppHost` PackageVersion | Omitted | It is an implicit SDK reference; a PackageVersion causes NU1009 under CPM |
| 7 | AC7 AppHost-level (`aspire describe chat-model`) | Not run (needs working CLI). Verified at unit level only (`EnvironmentVariable_OverridesJson`). Verify in Phase 02 via the Function's `AI__Chat__Model` | See #5 |
| 8 | `OllamaApiClient`-based embeddings for OpenAICompatible | `EmbeddingOptions` has no `Endpoint`/`ApiKey` (per spec); OpenAICompatible embeddings read `AI:Embedding:ApiKey` / `AI:Embedding:Endpoint` straight from `IConfiguration` | Keeps the pinned options shape |

## Observed Postgres schema (Testcontainers `pgvector/pgvector:pg17`)
- Table (= `FilingChunkRecord.CollectionName`): `filing_chunks`, schema `public`.
- Columns: `key:uuid, content:text, context:text, documentid:text, ticker:text, companyname:text, cik:text, formtype:text, fiscalyear:integer, filingdate:timestamp with time zone, section:text, chunkindex:integer, chunkcount:integer, indexfingerprint:text, containsinstructionliketext:boolean, sourceurl:text, embeddingmodel:text, embedding:vector`.
- Auto-embedding: `collection.SearchAsync("text", top)` embeds with the store's `EmbeddingGenerator` (verified: the fake keyed Query generator received `search_query: what are the risks?`). No fallback needed. Phase 03 still has to verify score direction for `CosineSimilarity`.
- Not yet checked: Phase 02's `WriterSchemaContractTests` (MEDI writer compatibility).

## Package versions (`Directory.Packages.props`)
Aspire 13.6.4 (all `Aspire.*` and `Aspire.AppHost.Sdk/13.6.4`), `Microsoft.Extensions.AI`/`.OpenAI` 10.11.0, `OllamaSharp` 5.5.0, `Microsoft.Extensions.VectorData.Abstractions` 10.11.0, `CommunityToolkit.VectorData.PgVector` 1.0.2, `Npgsql` 10.0.3, `Pgvector` 0.3.2, `Microsoft.Extensions.*` 10.0.14, `Http.Resilience`/`ServiceDiscovery` 10.11.0, OpenTelemetry 1.20.1, `Testcontainers.PostgreSql` 4.16.0, `Microsoft.NET.Test.Sdk` 18.11.0, `xunit` 2.9.3 (**xUnit v2**, the template's), `xunit.runner.visualstudio` 3.1.4, `coverlet.collector` 10.1.0.
Suppressed diagnostics: none. One new warning: `ASPIRE010` (AppHost `AspireUseCliBundle=false`; harmless, listed).

## Test results
- `Category!=RequiresOllama&Category!=RequiresDocker`: 38 pass. `RequiresDocker`: 2 pass. `RequiresOllama`: 1 pass (768 floats). Full `Category!=RequiresOllama`: 40 pass.
- Aspire (via `dotnet run`): `postgres` (`pgvector/pgvector:pg17`) and `storage` (Azurite 3.35) containers start. `pg_available_extensions` shows `vector | 0.8.7`. Azurite answers on host port 10000 (HTTP 403 to an unsigned request). `WithBlobPort(10000)` works; note the listener on 10000 is Aspire's own `dcp.exe` proxy, which forwards to a random container port, so `docker ps` shows `62343->10000`.

## Anything Phase 01 must know
- The embedding model, its 768 dimensions and its task prefixes are constants in `EmbeddingModelConstants`; the record is mapped with `[VectorStoreKey]`/`[VectorStoreData]`/`[VectorStoreVector]` attributes (Learn: "Define your data model"). Switching models means editing the constants and re-indexing.
- `AddSecFilingsVectorStore` registers `PostgresVectorStore` and `VectorStoreCollection<Guid, FilingChunkRecord>`; the host only has to register `NpgsqlDataSource` with `UseVector()`: `CREATE EXTENSION vector` runs inside the registration when the store is first resolved (`PgVectorSetup` was removed).
- The Aspire CLI needs `aspire update` before `aspire start` works with 13.6.4.
- `AppHost.cs` assigns unused resources to a discarded tuple; Phase 01/02 should reference them with `WithReference`.

## Commands & operating notes
**Project:** SEC filings RAG to agentic RAG demo. .NET 10, Aspire, Postgres + pgvector, Azurite, native Ollama. Read `README.md` first; contracts are in `docs/01-architecture-and-design.md`; phase specs and notes are in `docs/specs/`.

**Layout:** `src/SECFilings.{AppHost,ServiceDefaults,Core,Ingestion}` (Ingestion is empty until Phase 02), `tests/SECFilings.Core.Tests`. Not yet created: `Ingestion.Functions` (Phase 02), `IngestionInit` (01), `Retrieval` (03), `Agent` (05), `Api` (04), `ChatCli`, `evals/`, `scripts/` (later phases).

**Commands (PowerShell 7.3+)**
- `dotnet build SECFilingsAgent.slnx`
- `dotnet test SECFilingsAgent.slnx --filter "Category!=RequiresOllama"` (Docker must be running); unit only: `--filter "Category!=RequiresOllama&Category!=RequiresDocker"`
- Run the app: `aspire run` / `aspire start`, `aspire wait <res>`, `aspire logs <res>`, `aspire describe`, `aspire stop` (needs CLI 13.6.x: `aspire update`). Fallback: `dotnet run --project src\SECFilings.AppHost`.
- `ollama pull nomic-embed-text`, `ollama pull qwen3:4b`
- Override a parameter: `${env:Parameters__chat-model} = 'llama3.2'` before starting.
- Postgres check: `docker exec <container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d secfilings -c "..."'` (find it with `docker ps --filter ancestor=pgvector/pgvector:pg17`).
- Later: `dotnet user-secrets set "Parameters:sec-user-agent" "Your Name you@example.com" --project src/SECFilings.AppHost` (Phase 01); `./scripts/Invoke-Indexing.ps1` (Phase 02); `dotnet run --project src/SECFilings.ChatCli`.

**Conventions:** central package management (strip `Version=` after every scaffold, move to `Directory.Packages.props`; all Aspire versions and the AppHost SDK attribute in step); libraries never reference hosts; hosts register connection-bearing clients; one `AddSecFilingsXxx(IServiceCollection, IConfiguration)` per library; only the two keyed embedding generators embed text; built-in first; model names, dimensions and endpoints come from configuration only (host `appsettings.json` → env vars `AI__...` → Aspire parameters); no model names in C#; test traits `RequiresOllama` / `RequiresDocker`; Azurite blob port 10000 is fixed; no secrets in git; do not edit `CLAUDE.md`.

**Gotchas:** see spec §10, plus: Aspire CLI/AppHost version skew (deviation 5); the AC6 grep false positive on "Ollama" (deviation 4); `AddSingleton(sp => store.GetCollection(...))` registers the concrete `PostgresCollection` type, so register `VectorStoreCollection<Guid, FilingChunkRecord>` explicitly; the heredoc-in-bash tool call fails on apostrophes, so use the file tools for source with `'`.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.embeddinggeneratorbuilderservicecollectionextensions.addkeyedembeddinggenerator | Keyed registration helper and return type | `services.AddKeyedEmbeddingGenerator(key, factory)` returns `EmbeddingGeneratorBuilder` for `.Use(...).UseLogging().UseOpenTelemetry()` (RAG guide: embeddings phase) |
| 2 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.embeddinggeneratorbuilder-2.use | `Use` overloads | Spec's `generateFunc` overload exists; I used the `(inner, IServiceProvider)` overload with a `DelegatingEmbeddingGenerator` (deviation 2) |
| 3 | https://learn.microsoft.com/dotnet/ai/iembeddinggenerator#pipelines-of-functionality | Delegating generator as the middleware base | `PrefixingEmbeddingGenerator` |
| 4 | https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/postgres-connector | `UseVector()` requirement, `PostgresVectorStore(dataSource, ownsDataSource)`, `StorageName` override, HNSW is the only index created | `NpgsqlDataSource` built with `UseVector()` by the host; `StorageName` on every property; `IndexKind.Hnsw` |
| 5 | https://learn.microsoft.com/dotnet/aspire/compatibility/9.4/azure-storage-apis-renamed | `AddBlobContainer` on the storage resource | `storage.AddBlobContainer("filings")` |
| 6 | https://learn.microsoft.com/azure/storage/common/storage-install-azurite#command-line-options | Azurite default blob port 10000 | Fixed host blob port 10000 |
| 7 | Package XML docs `Aspire.Hosting.Azure.Storage` 13.6.4 (`WithBlobPort`) | Name and meaning (host port) | `WithBlobPort(10000)` works as spec'd |
| 8 | Learn's Postgres connector page and the `PostgresVectorStoreOptions.EmbeddingGenerator` API (checked by compiling and by the Docker test) | Auto-embedding with `SearchAsync(string)` | Works; no fallback needed. The Learn search did not surface a "Letting the vector store generate embeddings" page for this connector; the integration test is the evidence |
| 9 | docs/03 RAG guide mapping (not re-read) | Embedding step: same model and symmetric normalization for documents and queries | Normalization before prefix, same options on both keys |
| 10 | *external* Azurite port, pgvector 0.8 / HNSW 2000-dim limit, Npgsql `ReloadTypesAsync` | pgvector docs / Npgsql docs from prior knowledge, behavior confirmed by the Docker test | `PgVectorSetup` |

No Learn page contradicted the spec on an API fact.

## Suggested commit message
```
Phase 00: foundation — flattened solution, Aspire AppHost (pgvector, Azurite, Ollama conn string), SECFilings.Core contracts + tests
```

## Simplification after review

By request, to follow CLAUDE.md: Ollama only (no OpenAI-compatible provider), one fixed embedding model (`EmbeddingModelConstants`), no text normalization, batching, query cache or fingerprint columns, no `ChatModelOptions`/`EmbeddingOptions`/`OllamaConnectionString` (`ConnectionStrings:ollama` is a bare URL, `AI:Chat:Model` is read directly), `PgVectorSetup` folded into `AddSecFilingsVectorStore`, and `FilingMetadata`/`FilingBlobConventions` deferred to Phase 01. Docs 01/02 and the phase 01-02 specs still describe the removed pieces. 9 tests remain (2 need Docker, 1 needs Ollama).
