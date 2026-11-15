# Phase 05 notes: Agentic RAG (the model decides when and what to search)

## What was built
- `src/SECFilingsAgenticRag.AgentServer` (no new project, no new package):
  - `Tools/FilingTools.cs`:
    - `FilingTools`, a sealed class with the bodies of two tools, `search_filings` (`SearchFilingsAsync`) and `list_available_filings` (`ListAvailableFilingsAsync`). It also has the typed `ListFilingsAsync`, which `GET /api/filings` uses, and `AsTools()`, which wraps both methods with the built-in `AIFunctionFactory.Create`. It is a DI singleton (all its dependencies are singletons).
    - `FilingSummary`, the record shared by the tool and `GET /api/filings`.
  - `AgentNames.AgenticRag = "agentic-rag"`.
  - `AgentOptions.MaxToolIterations` (1..20, default 6) and `"MaxToolIterations": 6` in `appsettings.json`.
  - `Prompts.AgenticRag`. `Prompts.Escape` / `Attribute` are now shared with the tool, which fences excerpts with them.
  - `AgentHostingExtensions`: `AddMemoryCache()`, `AddSingleton<FilingTools>()`, and `AddAIAgent(AgentNames.AgenticRag, CreateAgenticRag).WithInMemorySessionStore(withIsolation: false)`.
  - `Program.cs`: `MapAGUIServer(AgentNames.AgenticRag, "/agents/agentic-rag")` and `GET /api/filings`.
- No change to Common, RetrievalPipeline, AppHost or AgentClient.Cli (deviations 1 and 11).

## Function-invocation path
**Builder + `UseProvidedChatClientAsIs = true`** (the spec's primary path, not the `GetService` fallback):

```
FunctionInvokingChatClient (MaximumIterationsPerRequest = Agent:MaxToolIterations, MaximumConsecutiveErrorsPerRequest = 2,
                            FunctionInvoker = JsonException → actionable error text)
  → OpenTelemetryChatClient → LoggingChatClient → OllamaApiClient      (the last three come from Common's AddSecFilingsAI)
```

- `UseProvidedChatClientAsIs` keeps `ChatClientAgent` from adding its default function-invocation layer (Learn rows 1-2), so ours is the only one. Common's chat client has none (Phase 00).
- `IncludeDetailedErrors` stays at its default `false` (Learn row 5), and `ToolMode` stays at its default (Auto when tools are present). Neither is set explicitly.
- Observed: when the model asks for several calls in one response, the loop runs them all before the next model call. The client shows all `🔎` lines first, then all `↳` lines.

## AG-UI and `FunctionResultContent`
**Yes, both content types reach the client.** Every run printed `🔎 search_filings({...})` (from `FunctionCallContent`) and `↳ "Found 5 excerpts…"` (from `FunctionResultContent`, i.e. the AG-UI `TOOL_CALL_RESULT` event), and `toolCalls=` counted the calls. No client change and no tool-event sink were needed (Learn row 4).

## Table and column names (real)
- Table: `public."sec_filing_chunks"`, from `FilingChunkRecord.CollectionName` (deviation 2).
- Columns, all quoted: `"ticker"`, `"companyname"`, `"formtype"`, `"fiscalyear"` from `FilingChunkMetadataKeys`, and `"documentid"`, which is the MEDI writer's own column and also the `StorageName` on `FilingChunkRecord.DocumentId`.

```sql
SELECT "ticker", "companyname", "formtype", "fiscalyear", "documentid", COUNT(*)::int
FROM "sec_filing_chunks" GROUP BY 1, 2, 3, 4, 5 ORDER BY 1, 4 DESC, 3
```

The result is cached for 60 s in the built-in `IMemoryCache` (`AbsoluteExpirationRelativeToNow`).

## Tool behaviour
- **Cite tag:** `{Ticker}-FY{FiscalYear}-Item{N}` comes from `^Item\s+(\d+[A-Z]?)\.` (`Item 1A. Risk Factors` → `AAPL-FY2025-Item1A`). Sections that aren't an Item use `Chunk{ChunkIndex:000}`. Those are the indexed `PART I`, `PART II, III`, `(no section)` and a long exhibit header (from `SELECT DISTINCT section`, 65 values). The tag is deterministic and needs no per-turn state. Chunks of one Item share a tag on purpose, and a non-Item chunk can never share a tag with different text.
- **Argument validation** (model output is untrusted; Learn rows 6-7):

  | Bad argument | What the model gets back |
  |---|---|
  | blank `query` | `Error: query must not be empty.` |
  | unknown `ticker` | `Unknown ticker 'XYZ'. Indexed tickers: AAPL, AMZN, …` (no search runs) |
  | non-indexed `fiscalYear` | `Fiscal year 2022 is not indexed for AAPL. Indexed fiscal years: 2025. Omit fiscalYear…` (deviation 4) |
  | non-indexed `formType` | silently dropped (only indexed form types are passed, matched case-insensitively) |
  | argument JSON that doesn't bind (e.g. `"fiscalYear":"null"`) | actionable message from the `FunctionInvoker` (deviation 6) |

  `top` is clamped to 1..8, and then again by `Retrieval:MaxTop`. A string `"8"` binds fine: MEAI reads numbers from strings (measured).
- **Result format:** `Found N excerpts for "q" (filters: ticker=AAPL, formType=any, fiscalYear=any).`, then one `<excerpt cite=… title=… url=… score=…>` per hit (text ≤ 1,200 characters, `<` and `>` escaped), then a cite reminder.
- **Logging:** one Information line per call, for example `Tool search_filings query=revenue drivers ticker=NVDA formType=(null) fiscalYear=(null) results=5 elapsedMs=50.6`. Searches take 50-70 ms warm.
- **Schema** (printed from `AsTools()` with a scratch file-based app):
  - `search_filings` has `required: ["query"]`, and `ticker`/`formType`/`fiscalYear`/`top` as nullable with `default: null`. There is **no `cancellationToken`** (Learn row 3).
  - `list_available_filings` has an empty `properties` object.

## How the secret parameter was handled
**Not added** (deviation 1). Phase 00's simplification, made at the user's request, left Common **Ollama-only**: there is no `AI:Chat:Provider`, `Endpoint` or `ApiKey`, and no OpenAI-compatible client. The `chat-provider`, `chat-endpoint` and `chat-api-key` AppHost parameters would set environment variables that nothing reads. So there is no secret and nothing to configure for a local start.

To add a cloud provider later:
1. Add `Microsoft.Extensions.AI.OpenAI` and a provider switch to `AddSecFilingsAI`.
2. Add the three AppHost parameters, giving `chat-api-key` the empty-default pattern that `sec-user-agent` already uses (`AddParameter(name, () => config[...] ?? "", secret: true)`), so local runs still start without user-secrets.

## Results (acceptance criteria, §9)
- **Stack:** bundled Aspire CLI 13.6.4 (`aspire start` / `wait` / `describe` / `stop`), Docker Desktop, native Ollama 0.40.16, CPU only. Ollama finds no GPU; see "Commands & operating notes".
- **Indexing:** `indexed=0 skipped=5 failed=0` (the persistent volume kept 824 chunks).
- **Three chat models were tried**, because the default `qwen3:4b` takes 10+ minutes per answer.
- The prompt was changed after the `qwen3:4b` runs (deviation 8), and argument validation after the first `llama3.2` round (deviations 4 and 6).

| # | Check | Result |
|---|---|---|
| 1 | `GET /api/filings` | **Pass**: 5 rows, all 10-K, chunk counts > 0: AAPL FY2025 121, AMZN FY2025 157, MSFT FY2026 175, NVDA FY2026 174, TSLA FY2025 197 |
| 2 | Comparison → multiple filtered searches | **Tool calls: partly pass. Full criterion: 0 of 11 attempts** (details below). The criterion needs both tickers searched, tags from both companies cited, and a Sources list |
| 3 | "Which companies do you have filings for?" | `llama3.2`: **Pass** both rounds: one `list_available_filings` call, then the 5 companies with their fiscal years; 15 s. `qwen2.5:1.5b`: fail (no call) |
| 4 | "Thanks!" | `qwen2.5:1.5b`: `toolCalls=0` (pass, 3 s). `llama3.2`: `toolCalls=5` both rounds (searched "revenue drivers" for every ticker; 132-148 s). Lenient criterion: noted, not failed |
| 5 | Same question, three agents | See "Three agents compared" below |
| 6 | Env-var override | **Pass**: `${env:Parameters__chat-model}` = `qwen2.5:1.5b`, then `llama3.2` → `aspire describe api` shows `"AI__Chat__Model": "llama3.2"`, and `ollama ps` lists `llama3.2:latest 2.6 GB 100% CPU` |
| 7 | Optional cloud run | **Not run**: no cloud provider exists (deviation 1) |
| 8 | `dotnet build SECFilingsAgenticRag.slnx` | **0 errors, 4 warnings, none new**: `NU1903` on `Aspire.Hosting` / `.AppHost` 13.6.4 (pre-existing, Phase 04). A build while the AppHost runs shows a fifth warning from locked bin folders; a clean build to a separate `--artifacts-path` shows only the 4 |

**AC2 in detail** (question: *Compare Apple's and Microsoft's main revenue drivers*):

| Model / round | Run | Calls | Outcome | Time |
|---|---|---|---|---|
| `qwen3:4b`, original prompt | 1 | AAPL + MSFT (`formType=10-K`) | Correct calls. Answer covers **only MSFT**, tags `[MSFT-FY2026-Item7]`/`Item8`, no Sources list | 768 s |
| | 2 | AAPL + MSFT | Same pattern | 611 s |
| | 3 | — | Cancelled to switch models | — |
| `qwen2.5:1.5b`, prompt fix | 1-3 | 0 / 0 / 1 | **Fail**: no real calls; **made-up cite tags and invented quotes** (run 2); one call printed as JSON text; `fiscalYear: 2023` invented | 14-19 s |
| `llama3.2`, prompt fix | 1 | 3 (blended + 2 without a ticker) | Apple only; Sources include NVDA | 111 s |
| | 2 | AAPL + MSFT | Arguments sent as `"null"` strings → `Error: Function failed.` ×2 → **invented FY2022 tags and fake URLs** → fixed by deviation 6 | 90 s |
| | 3 | AAPL + MSFT | **Correct facts for both** companies (iPhone/Mac/iPad/Wearables/Services; M365 Commercial cloud/Azure/Windows OEM/Xbox), but no cite tags and no Sources list | 92 s |
| `llama3.2`, all fixes | 1 | AAPL + MSFT (`top:"8"`, binds) | MSFT only (M365 +17 %, Azure +41 %, LinkedIn +11 %, Dynamics +15 %). The Sources list adds **invented** `MSFT-FY2025/FY2024` tags | 117 s |
| | 2 | 1 blended call, no ticker | Both companies' product lines, no tags | 83 s |
| | 3 | AAPL + MSFT with invented `fiscalYear: 2022` | Got "Fiscal year 2022 is not indexed… Indexed fiscal years: 2025", called `list_available_filings`, **didn't retry the search**, then invented tags (`MSFT-FY2026-Item10…12`) | 48 s |

**Success rate for the "≥ 2 filtered calls, AAPL and MSFT" part:**
- `qwen3:4b` 2/2
- `llama3.2` 2/3, then 1/3 (the regression is model variance: the prompt and tool descriptions didn't change between rounds)
- `qwen2.5:1.5b` 0/3

**Full AC2 (both companies cited with tags, plus a Sources list): 0/11.** The spec's own flakiness guidance applies. The fixes were:
- descriptions and prompt (deviation 8): checked;
- `llama3.2`: tried;
- the cloud switch: not available.

**Findings:**
- The code path is verified end to end: the model issues filtered calls, the built-in loop runs them, AG-UI streams call and result, and the tool validates the arguments and returns tagged excerpts.
- **3-4B local models are the limit.** They drop a company, skip the tags, or invent them.
- **Inventing citations is the worst failure:** `qwen2.5:1.5b` run 2 and `llama3.2` (rounds 1 and 2). Phase 06 should add an answer-faithfulness eval that checks every cited tag against the tags the tools actually returned. Phase 07 middleware could also strip or flag tags that were never returned.

**Why Apple gets dropped (measured):** for `ticker=AAPL`, the query "revenue drivers" retrieves tax and cover-page boilerplate (scores 0.57-0.60), because Apple's 10-K says "net sales". The query "net sales by product category" scores 0.69-0.74 and returns the product and region net-sales tables. So the model sees no useful Apple excerpt and silently answers for Microsoft only. This is a retrieval-quality problem for Phase 06 (hybrid search and multi-query fix it at the source); the prompt now tells the model to retry with filing wording.

**Latency:**
- `qwen3:4b` (always reasons): 10-13 min per comparison.
- `llama3.2`: 48-117 s.
- `qwen2.5:1.5b`: about 15 s, but unusable for tools.

`qwen3:4b` remains the repo default (AppHost `Parameters:chat-model`, AgentServer `AI:Chat:Model`). For demos use `llama3.2` (command below).

## Three agents compared (AC5, `llama3.2`, all fixes)
| Agent | Answer | Time |
|---|---|---|
| `chat-only` | Generic: "iPhone ~50-60 % of revenue", segment names, and a false claim to be "based on … recent SEC filings". No sources | 11 s |
| `standard-rag` | One search with the raw question returned only MSFT chunks: M365, Azure, search advertising, LinkedIn, Dynamics `[1]`, and "the sources do not provide information on Apple". Correctly refuses to invent the Apple half | 34 s (70 s in the earlier round) |
| `agentic-rag` | Per-company filtered searches (AC2). When it works, both companies are covered: `llama3.2` run 3 and round 2 run 2 had the right facts for both | 48-117 s |

This is the gap the phase exists to show: one blended search (standard RAG) loses the company with less similar text, and per-company tool calls recover it. Answer quality is then bounded by the model.

## TextSearchProvider on-demand mode: why a custom tool anyway
`TextSearchProviderOptions.SearchTime = OnDemandFunctionCalling` is a one-line agentic switch. It exposes a built-in `Search(query)` tool whose body would be the same `FilingSearch.SearchFilingsAsync` delegate standard-rag uses, so it would inherit every Phase 06 retrieval improvement for free. But its only parameter is the query string. For "Compare Apple and Microsoft" the model can then only phrase better queries, and every search still competes across all five companies. Measured above: company names aren't in the embedded text, so an unfiltered "Apple revenue" query returns other companies' chunks (Phase 03 check 2: 2 of 5 hits were AAPL). `search_filings` adds `ticker`, `formType` and `fiscalYear` as typed, index-validated filters, which is what Learn's agentic-RAG guidance asks for ("Support filtering… expose metadata filters as tool parameters"). It also returns deterministic cite tags instead of `[n]` positions, which would collide across several calls. The on-demand experiment itself wasn't run (it's optional in the spec).

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `FilingTools` | `AIFunctionFactory` (**used**), `FunctionInvokingChatClient` (**used**), `TextSearchProvider` on-demand tool | The tool bodies (filter validation, cite tags, result format) and their DI dependencies. The on-demand tool takes only a query string (section above) |
| `FilingSummary` | — | No built-in type describes "one indexed filing"; it's the wire shape of `/api/filings` |
| *(not added)* tool interface, catalog interface, citation recorder, tool-event sink, custom tool loop | `AIFunctionFactory`, `FunctionInvokingChatClient` (+ `FunctionInvoker`), AG-UI tool events, deterministic tags | Built-ins cover them |

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | §6.6 AppHost `chat-provider` / `chat-endpoint` / `chat-api-key`; AC7 | Not added; AC7 not run | Code on disk wins: Common is Ollama-only since the Phase 00 simplification, so the variables would be read by nothing (CLAUDE.md rule 3). How to add it later is in "How the secret parameter was handled" |
| 2 | `FilingChunkSchema.CollectionName(...)` / `CreateDefinition(...)` storage names | `FilingChunkRecord.CollectionName` (`sec_filing_chunks`) + `FilingChunkMetadataKeys` | Those helpers don't exist (Phase 03 deviation 1) |
| 3 | Missing table (`42P01`) → empty list + warning | No handler | AgentServer creates the table at startup (Phase 03 deviation 17), so the branch could never run |
| 4 | `fiscalYear` passed only if 1990..2100 | Validated against the **indexed** fiscal years (for the ticker, if one is given), with the valid years in the error | Measured: models invent years (2022, 2023). A plausible-range check passes them through to an empty result. Same rule as tickers (Learn row 7: validate tool parameters) |
| 5 | Excerpt attributes `cite`, `title`, `url` | Plus `score`; the tool description states the return shape | Learn row 7: "Include metadata… relevance scores" and "Describe the return schema" |
| 6 | `IncludeDetailedErrors = false` only | Also `FunctionInvoker`: catch **`JsonException`** only and return how to fix the arguments; everything else stays generic | Learn row 6. Measured: `llama3.2` sent `"fiscalYear":"null"`; with only "Function failed." it answered from memory with fake citations |
| 7 | `IncludeDetailedErrors = false`, `ToolMode = Auto` set explicitly | Not set | Both are the defaults (CLAUDE.md rule 3) |
| 8 | Prompt rule 6 as written | "Cover every company the question names. If a company's excerpts do not answer the question, search that company again with different wording (filings say 'net sales'…)" | Measured: `qwen3:4b` dropped Apple twice after an off-target first search |
| 9 | Header `Found 5 excerpts (AAPL 10-K FY2025) for "q".`; no-hit `(filters: ticker=XYZ)` | One filters description for both: `(filters: ticker=AAPL, formType=any, fiscalYear=any)` | Same information, one format, one line of code |
| 10 | `IReadOnlyList<AITool> AsTools()` + `[.. tools.AsTools()]` | `IList<AITool>`, assigned directly to `ChatOptions.Tools` | `ChatOptions.Tools` is an `IList<AITool>`; no copy needed |
| 11 | AC2 greps `^tool_call `; optional CLI argument formatting | Grepped the CLI's `🔎 name({json})` lines; CLI unchanged | The docs/01 §5.7 contract defines `🔎`. The JSON already shows `"ticker":"AAPL"` |
| 12 | `MapAGUI` | `MapAGUIServer` | Phase 04 deviation 1 |
| 13 | `AgentOptions.MaxInputTokens` | Not added | Phase 07 consumer (Phase 04 deviation 4 rule) |

## Anything Phase 06 must know
- `search_filings` calls the same unkeyed `IVectorSearchable<FilingChunkRecord>` as `/api/search` and standard-rag, so hybrid search, rerank and multi-query reach the agent automatically.
- The biggest quality gap is vocabulary ("revenue" vs "net sales"); multi-query should help most.
- Add evals for:
  - tool selection: expected `ticker` per company;
  - tool calls per request;
  - **citation faithfulness**: every `[TAG]` in an answer must appear in that run's tool results.

  Small models fail the last one (Results).
- Cite tags are `{Ticker}-FY{Year}-Item{N}` or `…-Chunk{NNN}`. `FilingTools.CiteTag` is private; expose it if evals need it.

## Commands & operating notes
- **Start, index, check the catalog** (bundled CLI; see Phase 04 for why):
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  dotnet build SECFilingsAgenticRag.slnx                      # before starting: a running AppHost locks bin folders
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost   # don't pipe it into Select-Object -First N: that kills the start
  & $aspire wait api; & $aspire wait indexing
  $env:Path = "$(Split-Path $aspire);$env:Path"; ./scripts/Invoke-Indexing.ps1
  Invoke-RestMethod http://localhost:5180/api/filings | Format-Table ticker, companyName, formType, fiscalYear, chunkCount
  & $aspire stop --apphost src/SECFilingsAgenticRag.AppHost
  ```
- **Agentic RAG from the CLI:**
  ```powershell
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --agent agentic-rag                    # REPL
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --ask "Compare Apple's and Microsoft's main revenue drivers" --agent agentic-rag --show-tool-args
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --ask "Which companies do you have filings for?" --agent agentic-rag --show-tool-args
  ```
  Tool calls print as `🔎 search_filings({"ticker":"AAPL",…})`, results as `↳ first 100 characters`; the summary line counts `toolCalls=`.
- **Faster demo model** (doesn't reason, calls tools; about 1-2 min per comparison on CPU instead of 10+):
  ```powershell
  ollama pull llama3.2
  & $aspire stop --apphost src/SECFilingsAgenticRag.AppHost
  ${env:Parameters__chat-model} = "llama3.2"            # same shell as `aspire start`; braces because of the '-'
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost; & $aspire wait api
  & $aspire describe api --format json | Select-String '"AI__Chat__Model"[^,]*'   # confirm
  Remove-Item 'Env:Parameters__chat-model'
  ```
  Without Aspire: `AI__Chat__Model=llama3.2`. `qwen2.5:1.5b` is fast but can't drive the tools (Results).
- **Configuration:** `Agent__MaxToolIterations` (1..20, default 6; Learn says 5-10 is typical), `Agent__RetrievalTop` (default excerpts per call; the tool clamps to 1..8). The catalog is cached for 60 s, so newly indexed filings appear in the tools and `/api/filings` within a minute.
- **Cloud provider:** not available yet (deviation 1). There are no keys to set, and nothing secret is in config or these notes.
- **Gotchas:**
  - Expect minutes per agentic answer on CPU: each tool round trip is a full model call over a growing prompt (5 excerpts × ≤ 1,200 characters per call).
  - Small models drop companies, skip tags, and **invent cite tags**. Check that tags appear in the `↳` results before trusting an answer.
  - "Thanks!" may still trigger searches with `llama3.2`.
  - **GPU:** this machine has only integrated `AMD Radeon (TM) Graphics` (0.5 GB reserved). Ollama 0.40.16 runs with `OLLAMA_VULKAN=true` and its discovery finds only `library=cpu`, so `ollama ps` shows `100% CPU`. Integrated GPUs are skipped unless `OLLAMA_IGPU_ENABLE` is set (untested; Ollama is *external*, not Microsoft). The gain would be modest because memory is shared.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.chatclientagentoptions.useprovidedchatclientasis | Meaning of `UseProvidedChatClientAsIs` (package v1.13) | `true`: ChatClientAgent adds no default decorators, so our `FunctionInvokingChatClient` is the only tool loop (builder path, not the `GetService` fallback) |
| 2 | https://learn.microsoft.com/agent-framework/concepts/agents/agent-pipeline#chatclientagent-pipeline | "By default, ChatClientAgent wraps the provided chat client with function-calling support" | Confirms the double-invocation risk the spec warns about; same as row 1 |
| 3 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.aifunctionfactory.create | Parameter binding: `CancellationToken` is bound from `InvokeAsync` and **not included in the JSON schema**; `[Description]` on the method and parameters | Verified by printing the schemas: no `cancellationToken`; only `query` required; optional parameters carry `default: null` (RAG guide: agentic RAG, retrieval as a tool) |
| 4 | https://learn.microsoft.com/agent-framework/integrations/by-component/ui/ag-ui/backend-tool-rendering | `FunctionCallContent` → `TOOL_CALL_START/ARGS/END`, `FunctionResultContent` → `TOOL_CALL_RESULT`; .NET client gets the same content types | No client change and no event sink; verified: both content types arrive |
| 5 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient.maximumconsecutiveerrorsperrequest and https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient.includedetailederrors | Defaults (3 consecutive errors; detailed errors `false`, raw exception kept on `FunctionResultContent.Exception`) | `MaximumConsecutiveErrorsPerRequest = 2`; `IncludeDetailedErrors` left at its default (deviation 7) |
| 6 | https://learn.microsoft.com/dotnet/ai/how-to/handle-invalid-tool-input | Mistyped arguments; a custom `FunctionInvoker` catching `JsonException` and returning actionable text so the model self-corrects | **Changed a decision** (deviation 6): `FunctionInvoker` catches only `JsonException`. Measured which inputs throw: `"fiscalYear":"null"` → `JsonException`; `"top":"8"` binds |
| 7 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-agentic | When agentic RAG beats standard RAG (query decomposition: "compare X and Y" = two lookups); tool descriptions should name the data source, parameters and **return schema**; return 3-5 chunks **with metadata and relevance scores**; **support filtering**; **iteration limits of 5-10**; **validate tool parameters**; evaluate tool-selection accuracy, calls per request, latency | **Changed decisions**: `score` attribute and return-shape description (deviation 5), fiscal-year validation against the index (deviation 4); `MaxToolIterations = 6` confirmed; eval dimensions handed to Phase 06 |
| 8 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide | Phase order | This phase = query phase, agentic RAG on top of Phase 03 retrieval; LLM evaluation of tool selection and citation faithfulness is Phase 06 |
| 9 | Package XML docs / compiler, `Microsoft.Extensions.AI` 10.11, `Microsoft.Agents.AI` 1.28 | `UseFunctionInvocation(ILoggerFactory?, Action<FunctionInvokingChatClient>?)`, `FunctionInvoker` delegate shape, `ChatOptions.Tools` is `IList<AITool>` | Compiles as written; `AsTools()` returns `IList<AITool>` (deviation 10) |
| 10 | *external* `.claude/skills/agent-framework-builder-dotnet` (`references/extensibility.md`) | "ChatClientAgent adds function invocation itself unless UseProvidedChatClientAsIs is set, so avoid layering it twice" | Same as rows 1-2 |
| 11 | *external* Ollama 0.40.16 `server.log` / `ollama ps` | GPU discovery (`OLLAMA_VULKAN:true`, only `library=cpu`), which model is loaded | GPU note in gotchas; AC6 evidence |

Docs vs reality: no Learn statement was contradicted. The Learn API pages are for MAF v1.13 and MEAI 10.6/11.0; the installed 1.28 / 10.11 packages behave the same for every API used here.

## Suggested commit message
```
feat(agent): agentic-rag agent with search_filings/list_available_filings tools (built-in function invocation + AG-UI), GET /api/filings

- FilingTools: tool bodies exposed with AIFunctionFactory; ticker/fiscal-year/form validated against the
  indexed catalog (GROUP BY over sec_filing_chunks, IMemoryCache 60 s); deterministic cite tags
- agentic-rag: one FunctionInvokingChatClient (MaxToolIterations cap, JsonException -> actionable error),
  UseProvidedChatClientAsIs; MapAGUIServer /agents/agentic-rag
- GET /api/filings
```
