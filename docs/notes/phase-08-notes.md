# Phase 08 notes: MCP server (tools, resources, prompts); agentic-rag calls its tools over MCP

## What was built
- **`src/SECFilingsAgenticRag.McpServer`** (new ASP.NET Core host, Aspire resource `mcp`, fixed port **5190** via `launchSettings.json`):
  - `AddMcpServer(ServerInstructions = FilingPrompts.Instructions).WithHttpTransport(Stateless = true).WithResources<FilingResources>().WithPrompts<FilingPrompts>()`, `MapMcp("/mcp").WithRequestTimeout(120 s)`, `/health` + `/alive` from ServiceDefaults, CORS (`Cors:AllowedOrigins`, empty), Ollama health check (reused from Common), startup `EnsureCollectionExistsAsync`.
  - Tools: `McpServerTool.Create` over `FilingTools.AsTools()` through `AddOptions<McpServerOptions>().Configure<FilingTools>` (kept from the first attempt).
  - A call-tool filter (`WithRequestFilters(f => f.AddCallToolFilter(...))`) maps an argument-binding `JsonException` to the corrective message. This is now the **only** place for it.
  - `FilingResources` (`[McpServerResourceType]`): `secfilings://filings` (catalog JSON), `…/{ticker}/{formType}/{fiscalYear}` (Markdown outline), `…/items/{item}` (one Item in one `<excerpt>` fence, capped at `Mcp:MaxResourceTokens` = 6000).
  - `FilingPrompts` (`[McpServerPromptType]`): `analyze_risk_factors`, `compare_companies`, `summarize_filing_item` (embedded resource + instruction).
  - `McpResourceOptions` (`Mcp:MaxResourceTokens`).
- **RetrievalPipeline**: `Tools/FilingTools.cs` (moved from AgentServer with `git mv`, namespace `SECFilingsAgenticRag.RetrievalPipeline.Tools`; `CiteTag` public; new public `ItemOf(section)`; default top = `Retrieval:DefaultTop`) and `Tools/ToolText.cs` (`Untrusted`, `Attribute`, moved out of `Prompts`).
- **AgentServer**: the MCP server is gone (no `AddMcpServer`/`MapMcp`; package `ModelContextProtocol.AspNetCore` → `ModelContextProtocol.Core`). New `Tools/McpToolsProvider.cs` (an `AIContextProvider`) and `McpEndpointOptions` (`Mcp:Endpoint`, `Mcp:TimeoutSeconds`). The named HttpClient is `"mcp"`. `agentic-rag` gets its tools from the provider; `FunctionInvoker` unwraps MCP results and maps "unreachable". `FilingTools` stays registered for `GET /api/filings`. CORS kept for Phase 12.
- **AppHost**: `mcp` resource (`WithReference(db).WaitFor(db)`, `WithReference(ollama)`, `WithAIModels`, `WithExternalHttpEndpoints`); `api` gets `WithReference(mcp)` (no `WaitFor`); `evals-answers` gets `WaitFor(mcp)`.
- **Evals**: `Mcp` section in `appsettings.json`; Groundedness context now also includes agentic-rag's tool results (its searches run in `mcp`, out of `RecordingVectorSearch`'s reach).
- `.vscode/mcp.json` → `http://localhost:5190/mcp`. `docs/01`: config keys and decision #18 adjusted (deviations 2, 3, 9).

Deviations / history: a first attempt hosted `/mcp` inside AgentServer for external clients only, while `agentic-rag` kept calling `FilingTools` in-process; this phase replaces it.

## Path taken for the agent's tools
**Primary path (context provider), verified.** `McpToolsProvider.ProvideAIContextAsync` returns `new AIContext { Tools = [.. mcpClientTools] }`. Under `UseProvidedChatClientAsIs = true` those tools reached `ChatOptions.Tools`, our `FunctionInvokingChatClient` invoked them, and every invocation was an MCP `tools/call`:
- `aspire logs mcp`: `Tool search_filings query=Apple supply chain risks ticker=AAPL … results=8`;
- `aspire logs api`: 0 `Tool search_filings` lines.

The fallback (startup connect) was not needed.
- Connect is lazy (first agentic-rag run), behind a `SemaphoreSlim`. A failed connect is not cached.
- `tools/list` runs **once per process**. The stateless server sends no `list_changed`, so a new server tool reaches `api` after an `api` restart.
- No session-expiry handling: the server is stateless, so there is no session to expire. Verified: stop and start `mcp`, and the same `api` process works again.

## What the model sees (measured)
- `McpClientTool.InvokeAsync` returns a **`TextContent`** for a successful text result. The same TextContent then reached the AG-UI client JSON-serialized: `↳ {"text":"Found 5 excerpts…`. That is the same class of bug as the first attempt's `MarshalResult`.
- For a server error result it returns the whole **`CallToolResult` as a `JsonElement`** (SDK behavior, observed in its result projection).
- Fix, in the existing `FunctionInvoker` (no new type): `TextContent` → `.Text`; `JsonElement` → the text blocks of the deserialized `CallToolResult`. After the fix, `↳ Found 8 excerpts for "Apple supply chain risks" (filters: ticker=AAPL, …)`: plain text, the same as before the move.

## Errors (measured)
| Case | Result |
|---|---|
| `tools/call search_filings {fiscalYear:"null"}` over JSON-RPC | `isError: true`, text `Error: the arguments do not match the tool schema…` (from the call-tool filter; the binding `JsonException` propagates through `AddCallToolFilter`, so the filter sees it. Without the filter the SDK sends its generic message, per its XML docs) |
| Old client-side `catch (JsonException)` | Deleted: `McpClientTool` only serializes the arguments, so binding can no longer fail in `api` |
| `mcp` stopped, agentic-rag asked | Exit 0, no HTTP 500. Tool result `Error: the filing search service (MCP server) is unreachable…`; the answer says it couldn't search. If `mcp` is down at the *first* connect, the provider instead returns `AIContext.Instructions` with the same text and no tools (also observed, see deviation 2) |

## Results (acceptance criteria)
Run under Aspire with the bundled 13.6.4 CLI, Docker Desktop (started for this run), native Ollama, `${env:Parameters__chat-model} = "llama3.2"` (Phase 07 practice: `qwen3:4b` takes 10+ min per answer). Resources were rebuilt with `aspire resource <name> rebuild` / `stop`+`start`, never by `dotnet build` against a running resource.

| # | Check | Result |
|---|---|---|
| 1 | `dotnet build SECFilingsAgenticRag.slnx` | **0 errors, 4 warnings**: the pre-existing `NU1903` on `Aspire.Hosting`/`.AppHost` 13.6.4 (Phase 04–07, left to you; 13.6.5 exists). No new warning. One run hit `MSB4166` (MSBuild worker nodes exited) right after `aspire stop`; `dotnet build-server shutdown` + `-nodeReuse:false` built clean |
| 2 | `aspire start; wait mcp; wait api; describe` | **Pass**: `mcp` Running/Healthy on `http://localhost:5190`, `api` on 5180 |
| 3 | `tools/list`, `tools/call search_filings {supply chain risks, AAPL}` | **Pass**: both tools (`search_filings` required `query`; `list_available_filings` no params); 5 AAPL `<excerpt cite="AAPL-FY2025-Item1A" …>` blocks. `initialize` → protocol `2025-06-18`, the instructions, no `Mcp-Session-Id` needed |
| 4 | `AgentClient.Cli --agent agentic-rag --show-tool-args` "What supply chain risks does Apple report?" | **Pass** (after the unwrap fix): `🔎 search_filings({"query":"Apple supply chain risks","ticker":"AAPL","top":"8"})`, plain-text `↳`, answer cites `[AAPL-FY2025-Item1A]`; `mcp` log has the Tool line, `api` has none; 151 s |
| 5 | `POST :5180/mcp` | **Pass**: 404; AgentServer references only `ModelContextProtocol.Core` (no `ModelContextProtocol.AspNetCore` in its assets) |
| 6 | Degradation | **Pass**: `mcp` stopped → run completes, exit 0, "couldn't search… try again shortly"; chat-only (12 s) and standard-rag (78 s) answer. `mcp` started → agentic-rag works again **without restarting `api`** (`Found 8 excerpts`). Caveat: with `mcp` down, llama3.2 still invented sec.gov URLs under "Sources"; the Phase 07 citation note was appended. That is model behavior |
| 7 | Corrective errors | **Pass**: see Errors. Only the server mapping exists, so there is no client mapping to check in check 4's run |
| 8 | `/api/filings`, `/api/search` | **Pass**: 5 filings (AAPL 121 … TSLA 197 chunks); search `strategy=Vector`, 3 AAPL Item 1A hits 0.73/0.72/0.70 |
| 9 | Resources | **Pass**: `resources/list` = `secfilings://filings` only; both templates listed; catalog = 5 filings (camelCase JSON); outline of `aapl/10-k/2025` (case-insensitive) lists `Item 1A. Risk Factors | AAPL-FY2025-Item1A | 24 | …/items/1A`; item `1a` → one `<excerpt cite="AAPL-FY2025-Item1A" …>` starting `PART I Item 1A. Risk Factors`, heading once, truncation note at 6000 tokens (36 KB); `XYZ` → `-32602 Unknown ticker 'XYZ'. Indexed tickers: AAPL, AMZN, MSFT, NVDA, TSLA.`; `items/99Z` → `-32602 … Its items: 1, 1A, … 16.` Traces: the item read has 3 `postgresql` spans (catalog, sections `GROUP BY`, `WHERE documentid = $1 AND section = ANY($2) ORDER BY chunkindex`) and **no embeddings span**; `tools/call` has `embeddings nomic-embed-text` |
| 10 | Prompts | **Pass**: three prompts with arguments (`ticker*, fiscalYear`; `tickers*, topic*`; `ticker*, fiscalYear*, item*, formType`); `analyze_risk_factors {ticker:"aapl"}` → "…Apple Inc. (AAPL) reports in its 10-K for FY2025… Call search_filings…"; `compare_companies {AAPL,XYZ}` → `-32602` listing tickers; `summarize_filing_item` → `resource` block with URI `secfilings://filings/AAPL/10-K/2025/items/1A` + text instruction; `completion/complete` for `item` "1" → `1, 1A, 1B, 1C` |
| 11 | Build `evals-answers` only | **Pass**: `dotnet build evals/SECFilingsAgenticRag.Evals` 0/0. Not run (see Commands) |

Latency: a `search_filings` call over MCP was 2.3–4.8 s here (the first embedding after a model swap; Phase 05's warm 50–70 ms is the steady state). Item read ≈ 0.4 s; outline/catalog reads 5–50 ms.

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | Item filter `r.Section.StartsWith($"Item {item}.")` | Exact section names from the outline `GROUP BY`, then `itemSections.Contains(r.Section)` | Measured `NotSupportedException: Unsupported method call: String.StartsWith`; Learn's connector page lists no `StartsWith`, but lists `Contains`. Still all in the database |
| 2 | `Mcp:Endpoint` default `https+http://mcp/mcp` | `http://mcp/mcp` (docs/01 updated) | Measured: `HttpClientTransportOptions.Endpoint` throws `Endpoint must use HTTP or HTTPS scheme`. The service-discovery handler resolves the host `mcp` for a plain scheme (Learn row 6) |
| 3 | Standard resilience handler, 0 retries, attempt = total, breaker ≥ 2× | `RemoveAllResilienceHandlers()` + `HttpClient.Timeout = Mcp:TimeoutSeconds` | With 0 retries the handler adds only a breaker and a limiter, whose `BrokenCircuitException` would be one more failure type to map. Intent kept: one bounded attempt, no retries; the server bounds the call at 120 s too |
| 4 | Filter includes `!r.ContainsInstructionLikeText` | Item chunks loaded, then partitioned on the flag | The spec also asks to say how many flagged chunks were excluded; partitioning the Item's own rows (≤ 40) avoids a second count query. No whole-filing in-memory filtering |
| 5 | `item` must match `^\d{1,2}[A-C]?$` | Matched against the filing's own Items instead | Strictly stronger; the error lists that filing's Items |
| 6 | Resource-not-found error | `McpProtocolException(…, McpErrorCode.InvalidParams)` (-32602) | 2.4.0 XML docs: `ResourceNotFound` (-32002) is the legacy code; new code should use `InvalidParams` |
| 7 | `FilingTools` default top from `AgentOptions.RetrievalTop` | `RetrievalOptions.DefaultTop` (both 5) | As the spec asks; `Agent:RetrievalTop` stays for standard-rag |
| 8 | `compare_companies` text "in their latest indexed 10-K" | "in their latest indexed filings", step 1 names each ticker, fiscal year and form | Form and year come from the catalog, not a hard-coded "10-K" |
| 9 | `evals-answers` `WithReference(mcp).WaitFor(mcp)` | `WaitFor(mcp)` only; evals use `http://localhost:5190/mcp` | The evals host has no ServiceDefaults/service discovery, so the reference's env vars would be read by nothing. docs/01 decision 18 corrected: answer evals call over MCP |
| 10 | Completion handler for `ticker`/`fiscalYear` (optional) | Not added | `[AllowedValues]` already completes `item`; a custom handler is optional. Note: `[AllowedValues]` is **not enforced** by the SDK (item `4` was accepted; the filing's Items validate instead) |
| 11 | Rate limit / request size on `/mcp` | Only the request timeout | Tool calls are cheap embedding + SQL; Kestrel's default body limit applies |
| 12 | `0 warnings` | 4 pre-existing `NU1903` | Phase 07 deviation 16 |

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `McpToolsProvider` | MCP SDK `McpClient`/`McpClientTool` (**used**), `ChatOptions.Tools` at agent build, `HostedMcpServerTool`, `Microsoft.Agents.AI.Mcp` 1.28.0-alpha | No built-in context provider supplies MCP tools (the MAF MCP package has only MCP-Tasks wrappers and MCP skills); `McpClient` setup is async and `AddAIAgent`'s factory isn't; `HostedMcpServerTool` needs a provider that calls MCP (Ollama doesn't) |
| `McpEndpointOptions`, `McpResourceOptions` | Options pattern (**used**) | Our configuration |
| `FilingResources`, `FilingPrompts` | `WithResources<T>`/`WithPrompts<T>` (**used**) | The attribute model needs a declaring type; no built-in resources/prompts over a vector store |
| `ToolText` | — | Fencing/escaping shared by three callers (moved, not new logic) |
| *(not added)* tool interface, completion handler, reconnect/session logic, call-result wrapper type | `FunctionInvoker`, stateless transport, `AddCallToolFilter` | Built-ins cover them |

## Commands & operating notes
- **Start / stop** (bundled CLI matching 13.6.4; add `--non-interactive` from agents):
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  $ah = "src\SECFilingsAgenticRag.AppHost\SECFilingsAgenticRag.AppHost.csproj"
  ${env:Parameters__chat-model} = "llama3.2"     # optional fast model
  & $aspire start --non-interactive --apphost $ah; & $aspire wait mcp --apphost $ah; & $aspire wait api --apphost $ah
  & $aspire stop --non-interactive --apphost $ah
  ```
- **Code changed in one resource while running:** `& $aspire resource api rebuild --apphost $ah` (also `mcp`). Never `dotnet build` a running resource (locked outputs). After `aspire stop`, if `dotnet build` fails with `MSB4166`, run `dotnet build-server shutdown` and build with `-nodeReuse:false`.
- **PowerShell JSON-RPC check** (stateless: no session header, no `notifications/initialized` needed):
  ```powershell
  function Rpc($method, $params = '{}') {
    $r = Invoke-WebRequest -Method Post http://localhost:5190/mcp -ContentType application/json -SkipHttpErrorCheck `
      -Headers @{ Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2025-06-18" } `
      -Body ('{"jsonrpc":"2.0","id":1,"method":"' + $method + '","params":' + $params + '}')
    (($r.Content -split "`n" | Where-Object { $_ -like 'data:*' } | ForEach-Object { $_.Substring(5) }) -join '') | ConvertFrom-Json
  }
  (Rpc tools/list).result.tools.name
  (Rpc tools/call '{"name":"search_filings","arguments":{"query":"supply chain risks","ticker":"AAPL"}}').result.content[0].text
  (Rpc resources/read '{"uri":"secfilings://filings/AAPL/10-K/2025/items/1A"}').result.contents[0].text
  (Rpc prompts/get '{"name":"analyze_risk_factors","arguments":{"ticker":"AAPL"}}').result.messages
  ```
  Bodies are built as JSON strings: `ConvertTo-Json` of nested hashtables is fine too, but keep `-Depth 10`.
- **AgentServer alone:** `$env:Mcp__Endpoint = "http://localhost:5190/mcp"` (plus the Phase 03 connection strings).
- **Answer evals (not run here: about an hour on CPU):** start the stack (`mcp` must be up), then
  `dotnet run --project evals/SECFilingsAgenticRag.Evals -- answers --agent standard-rag,agentic-rag --limit 5`
  or `& $aspire resource evals-answers start --apphost $ah` (waits for `mcp`). Evals call `Mcp:Endpoint` = `http://localhost:5190/mcp`.
- **External clients:** VS Code uses `.vscode/mcp.json` (`http://localhost:5190/mcp`); MCP Inspector: Streamable HTTP, same URL; Claude Desktop: `npx mcp-remote http://localhost:5190/mcp` (*external*, unverified). Browser-hosted clients need `Cors__AllowedOrigins__0=<origin>` on `mcp`.
- **Configuration:** `Mcp__Endpoint`, `Mcp__TimeoutSeconds` (api, evals); `Mcp__MaxResourceTokens` (mcp). **`Retrieval:*` now lives in two hosts**: api (standard-rag, `/api/search`) and mcp (agentic-rag's tools). Override both, e.g. `Retrieval__Strategy=Hybrid` on `api` *and* `mcp`.
- **Gotchas:**
  - `npx @modelcontextprotocol/inspector` stays blocked by the tool policy (unpinned download); use the JSON-RPC check.
  - A tool added on the server reaches agentic-rag only after an `api` restart (tools/list once per process).
  - `/mcp` answers JSON-RPC, not health probes; use `/health` (Development only).
  - **Security:** `mcp` is unauthenticated on localhost and is both an internal dependency and an external surface. Before exposing it, add ASP.NET Core auth on `mcp` and send a token from `api`'s `"mcp"` HttpClient.
  - Phase 07's note about `/mcp` + `DisableRequestTimeout()` on AgentServer is obsolete.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision in code |
|---|-----|----------------|------------------|
| 1 | https://learn.microsoft.com/agent-framework/agents/tools/local-mcp-tools | `McpClient.CreateAsync`, `ListToolsAsync` → `AITool`s for an agent; `McpServerTool.Create(agent.AsAIFunction())` | Client in `McpToolsProvider`; server tools via `McpServerTool.Create(AIFunction)` |
| 2 | https://learn.microsoft.com/agent-framework/concepts/agents/conversations/context-providers and https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.aicontext (v1.13) | Override `ProvideAIContextAsync`; `AIContext.Tools` are per-invocation tools; one provider instance serves all sessions, so no session state in it | `McpToolsProvider` (singleton, no per-session state); verified tools reach the loop under `UseProvidedChatClientAsIs` |
| 3 | https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.aicontextprovider (security remarks) | Provider output is merged unfiltered; external data can inject | Tool results stay `<excerpt>`-fenced by `ToolText` on the server |
| 4 | https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/postgres-connector | Supported filter clauses: `==`, `&&`, `Contains`… no `StartsWith` | **Changed a decision** (deviation 1) |
| 5 | https://learn.microsoft.com/dotnet/core/extensions/service-discovery | `https+http://` scheme priority; handler resolves a plain `https://catalog` BaseAddress | **Changed a decision** (deviation 2): `http://mcp/mcp` |
| 6 | https://learn.microsoft.com/dotnet/core/resilience/http-resilience#remove-standard-resilience-handlers (Phase 01/07 rows) | Replacing the ServiceDefaults handler for one client (`EXTEXP0001`) | `RemoveAllResilienceHandlers()` on `"mcp"` (deviation 3) |
| 7 | Package XML docs `ModelContextProtocol.Core`/`ModelContextProtocol`/`.AspNetCore` 2.4.0 | `McpClient`/`HttpClientTransport` live in Core; `EnableStandaloneGetStream`; `Stateless` defaults to true (SEP-2567); resource/prompt return types and DI parameter binding; instance types constructed per call; `[AllowedValues]` completions; `AddCallToolFilter`; non-`McpException` tool errors get a generic message; `InvalidParams` over legacy `ResourceNotFound` | AgentServer references only Core; stateless set explicitly; no GET stream; call-tool filter; deviation 6 |
| 8 | Package XML docs `Microsoft.Agents.AI.Abstractions` 1.28.0 | `AIContextProvider` members, `InvokingContext` | Signatures |
| 9 | Package XML docs `Microsoft.Extensions.VectorData.Abstractions` 10.11 | `GetAsync(filter, top, FilteredRecordRetrievalOptions { OrderBy })` | Item chunks ordered by `ChunkIndex` in SQL (`ORDER BY "chunkindex" ASC`, captured) |
| 10 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide and …/rag-agentic | Retrieval as a tool behind a boundary; tool descriptions are the contract | Query phase unchanged; one tool definition for in-house and external agents |
| 11 | *external* `Microsoft.Agents.AI.Mcp` 1.28.0-alpha nupkg (XML docs, inspected in a scratch folder) | Whether MAF ships an MCP-tools context provider | It doesn't (MCP Tasks + skills only), so `McpToolsProvider` is justified |
| 12 | *external* MCP specification 2025-06-18 (Streamable HTTP, tools/resources/prompts, control model) | Stateless servers, SSE `data:` responses, primitives' controllers | Tools for the agent; resources/prompts for hosts |
| 13 | *external* `.claude/skills/agent-framework-builder-dotnet`, `.claude/skills/aspire-orchestration` | MCP integration guidance; `aspire resource <name> rebuild`, no `dotnet build` on running resources, `--non-interactive` | Operating procedure above |

Docs vs reality: the spec's `StartsWith` and `https+http://` endpoint both fail at run time (deviations 1, 2); Learn's MAF API pages are v1.13 while 1.28 behaves the same for everything used.

## Suggested commit message
```
feat(mcp): MCP server with tools, resources and prompts; agentic-rag calls its tools over MCP

- SECFilingsAgenticRag.McpServer (resource mcp, :5190, stateless Streamable HTTP): search_filings /
  list_available_filings, catalog/outline/item resources, three prompts, corrective call-tool filter
- FilingTools + ToolText moved to RetrievalPipeline.Tools; CiteTag/ItemOf public
- AgentServer: MCP server removed; McpToolsProvider (AIContextProvider over McpClient) supplies
  agentic-rag's tools; FunctionInvoker unwraps MCP results and maps an unreachable server
- AppHost: mcp resource, api references it, evals-answers waits for it; .vscode/mcp.json -> 5190
```
