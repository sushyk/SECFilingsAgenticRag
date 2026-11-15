# Phase 07 notes: Production hardening

## What was built
- **Common**
  - `Telemetry/TelemetryNames` (`SECFilingsAgenticRag.AI`, `.AgentServer`, `.IngestionPipeline`, `.RetrievalPipeline`) and `TelemetryOptions` (`Telemetry:EnableSensitiveData`, `false` in every config file).
  - `AI/ModelOptions`:
    - `ChatModelOptions` (`AI:Chat`): `[Required] Model`, `ContextLength`, `TimeoutSeconds` = 600;
    - `EmbeddingModelOptions` (`AI:Embedding`): `[Required] Model`.

    Both are bound with `ValidateDataAnnotations().ValidateOnStart()`.
  - `AddSecFilingsAI`:
    - registers the named `IHttpClientFactory` client `"ollama"`: infinite `HttpClient.Timeout`, `RemoveAllResilienceHandlers()`, then a tuned standard handler (§ Resilience);
    - registers the cl100k `Tokenizer`, moved here from IngestionPipeline so AgentServer shares the same instance type;
    - adds `UseOpenTelemetry(sourceName: TelemetryNames.AI, …EnableSensitiveData)` on the chat and embedding pipelines.
  - `ChatClientFactory.Create(services, model)` / `ForModel(services, model?)`: no `IConfiguration` parameter any more, and the HTTP client comes from the factory.
  - `AI/OllamaHealthCheck`: `GET api/tags` with a 5 s timeout. **Healthy** when both configured models are pulled, **Degraded** naming the missing models, **Unhealthy** when Ollama is unreachable.
  - `EmbeddingModelConstants.Name` was removed; the model name is configuration now.
- **IngestionPipeline**
  - `indexing.run` span with `secfilings.indexed/skipped/failed` counts.
  - One `indexing.document` span per filing (`document_id`, `ticker`, `status`, `chunk_count`).
  - Metrics `secfilings.ingestion.documents` (status) and `secfilings.ingestion.duration` (s).
- **RetrievalPipeline**
  - The `FilingSearch` span is now `retrieval.search`, with tags `secfilings.strategy`, `top`, `ticker`, `result_count` and `top_score`, plus error status.
  - Histogram `secfilings.retrieval.duration` (ms, strategy).
  - Spans `retrieval.rerank` (`candidate_count`) and `retrieval.multi_query` (`query_count`, `candidate_count`).
  - Cross-field rule: `Rerank:CandidatePool ≥ DefaultTop`.
  - `StructuredOutput` is now public, because the topic guard reuses it.
- **AgentServer**
  - `Sessions/PostgresAgentSessionStore` (built-in `AgentSessionStore`).
  - `Guardrails`: agent-run middleware delegates.
  - `ApiOptions` / `ChatRateLimitOptions` / `GuardrailsOptions`.
  - New `AgentOptions` fields: `MaxInputTokens`, `MaxExcerptTokens`, `SessionStore`, `CompactAboveTokens`, `MaxHistoryTurns`, `HardTokenBudget`, with cross-field rules.
  - Compaction (`CompactionProvider` on the `ChatClientBuilder`), `AIAgentBuilder.UseOpenTelemetry`, and a stable agent `Id`.
  - Scope prompt on all three agents.
  - `Prompts.Untrusted`: strips control tokens, collapses blank lines, escapes, and truncates by tokens.
  - One citation regex.
  - Rate limiting, request timeouts and the request-size limit on `/agents/*`; the Ollama health check.
- **ServiceDefaults**: `AddSource` / `AddMeter` with the four exact names, plus `Experimental.Microsoft.Extensions.AI*`, `Experimental.Microsoft.Agents.AI*` and (meters) `Microsoft.Extensions.AI*`. The Development-only `/health` now writes JSON with each check's status and description, because the default writer prints only `Degraded` and can't say which model is missing.
- **Functions host**: Ollama health check (`ready`); `AI:Embedding:Model` and `Telemetry` in `appsettings.json`.
- **AppHost**: new `embedding-model` parameter. A `WithAIModels(chat, embedding)` helper injects `AI__Chat__Model` and `AI__Embedding__Model` into `indexing`, `api`, `evals-retrieval` and `evals-answers`.
- **Evals**: `JudgeOptions` rule "OpenAICompatible requires Endpoint"; adapted to the new factory signature; the report header reads the embedding model from config.
- **README**: new "Operating the system" section; the configuration table is corrected.

## Exact APIs used (installed packages; checked in their XML docs)
| Need | API (MAF 1.28.0 / Hosting 1.28.0-preview.261106.1, MEAI 10.11, Http.Resilience 10.11) |
|---|---|
| Session store | `Microsoft.Agents.AI.AgentSessionStore` (**in the abstractions package now, `[Experimental("MAAI001")]`**): `ValueTask<AgentSession?> GetSessionAsync(AIAgent, AgentSessionStoreKey, CancellationToken)`, `ValueTask SaveSessionAsync(AIAgent, AgentSessionStoreKey, AgentSession, CancellationToken)`, `ValueTask DeleteSessionAsync(AIAgent, AgentSessionStoreKey, CancellationToken)`; virtual `GetOrCreateSessionAsync` (default: Get, then `agent.CreateSessionAsync`) not overridden |
| Store key | `AgentSessionStoreKey { string SessionId; IReadOnlyDictionary<string,string>? Partitions }`, not the `string conversationId` shown on Learn |
| Session serialization | `agent.SerializeSessionAsync(session, JsonSerializerOptions?, ct)` → `JsonElement`; `agent.DeserializeSessionAsync(JsonElement, JsonSerializerOptions?, ct)` |
| Registration | `IHostedAgentBuilder.WithSessionStore(Func<IServiceProvider,string,AgentSessionStore>, ServiceLifetime, bool withIsolation)` / `WithInMemorySessionStore(withIsolation: false)` |
| Middleware | `AIAgentBuilder.Use(Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>> runFunc, Func<…, IAsyncEnumerable<AgentResponseUpdate>> runStreamingFunc)` |
| Agent telemetry | `AIAgentBuilder.UseOpenTelemetry(string? sourceName, Action<OpenTelemetryAgent>? configure)`; `OpenTelemetryAgent.EnableSensitiveData` |
| Builder order | first added = outermost (same as `ChatClientBuilder`). **Verified**: `.UseOpenTelemetry(...).Use(guardrails)` gives refused runs an `invoke_agent` span tagged `secfilings.refused=true` |
| Compaction | `CompactionProvider(CompactionStrategy, string? stateKey, ILoggerFactory?)`, `PipelineCompactionStrategy(IEnumerable<CompactionStrategy>)`, `ToolResultCompactionStrategy` / `SlidingWindowCompactionStrategy` / `TruncationCompactionStrategy(trigger, minimumPreservedGroups, target)`, `CompactionTriggers.TokensExceed/TurnsExceed`; `ChatClientBuilder.UseAIContextProviders(...)` |
| `MapAGUIServer` | returns `IEndpointConventionBuilder`, so `.RequireRateLimiting(...)`, `.WithRequestTimeout(...)` and `.WithMetadata(new RequestSizeLimitAttribute(...))` chain directly; no `MapGroup` needed |
| Resilience | `IHttpClientBuilder.RemoveAllResilienceHandlers()` (`EXTEXP0001`) + `AddStandardResilienceHandler()`; its options are the named `HttpStandardResilienceOptions` (`pipeline.PipelineName`), configured with `Configure<IOptions<ChatModelOptions>>` |
| History for the topic guard | `inner.GetService<ChatClientAgent>()?.ChatHistoryProvider` is `InMemoryChatHistoryProvider` → `GetMessages(session)` |

## Session store design
- One table `agent_sessions (agent_id, conversation_id, partitions jsonb, session json, turns, created_at, updated_at)` with PK `(agent_id, conversation_id, partitions)`. It is created lazily once, guarded by a `SemaphoreSlim` and a volatile flag.
- Keyed by **`agent.Id`** (Learn), and `ChatClientAgentOptions.Id = name` makes that id **stable**. The default `AIAgent.Id` is a random GUID per process (XML docs), so without it no session would survive a restart. The delegating middleware agents forward `Id` to the inner agent.
- `partitions` is part of the key, because an isolation decorator (`IsolationKeyScopedAgentSessionStore`) puts the caller's identity in the key. With isolation off it is `{}`.
- **`session` is `json`, not `jsonb`** (see "Found while verifying").
- **Concurrent runs on one thread: optimistic concurrency, first save wins.**
  - The AG-UI thread id never reaches agent middleware: AG-UI loads the session from the store **before** the run and saves it **after** the stream. So a per-thread lock in the middleware could not stop the second run from working on a stale session.
  - Instead, `turns` doubles as a row version. `GetSessionAsync` remembers it per session instance (`ConditionalWeakTable`, nothing extra persisted), and the upsert updates only `WHERE turns = @loaded`.
  - The losing run's turn is not stored and a warning is logged (`…was saved by a concurrent run; this run's turn was not stored`). Histories can't interleave, and the last write can't silently win.
- History lives in the session (`InMemoryChatHistoryProvider` state), and so does compaction's state. The table therefore persists the whole conversation. The client only ever sends a thread id.
- Changing an agent's tools or prompts in place still affects that agent's old rows (same `agent_id`).

## Compaction (§6.1b)
- Pipeline: `ToolResultCompaction(TokensExceed(CompactAboveTokens))` → `SlidingWindow(TurnsExceed(MaxHistoryTurns))` → `Truncation(TokensExceed(HardTokenBudget), minimumPreservedGroups: 4)`, behind a scoped `#pragma warning disable MAAI001`.
- **Where:** `UseAIContextProviders(compaction)` is the innermost stage of each agent's `ChatClientBuilder`. All three agents use `UseProvidedChatClientAsIs = true`, so the built pipeline is the whole pipeline:
  - agentic-rag: `FunctionInvokingChatClient` → compaction → Common's OTel/logging/Ollama client;
  - chat-only and standard-rag: compaction → the same client. They have no tools, so the dropped default function invocation did nothing for them.
- **Verified in the trace:** `compaction.provider.invoke` / `compaction.compact` spans appear before **each** `chat` span, including the model call after the tool call.
- Defaults are **4000 / 10 / 6000**, not the spec's 2500/10/3500. The spec sized them for a 4k window, and Phase 06 set `AI:Chat:ContextLength` = 8192, which leaves about 2k tokens for output. Validation: `CompactAboveTokens ≤ HardTokenBudget < AI:Chat:ContextLength`.
- `minimumPreservedGroups: 4` (default 32) lets the backstop actually act in short conversations while keeping the question, its sources and the latest tool rounds.
- `SummarizationCompactionStrategy` was not used (an extra CPU model call; the spec makes it optional).

## Guardrails (§6.4)
Order inside `Guardrails.RunStreamingAsync`, which wraps every agent:
1. **Token limit.** `Tokenizer.CountTokens` on the last user message. Over `Agent:MaxInputTokens` → `Your message is too long (N tokens; the limit is M). Please shorten it.`
2. **Topic guard.** `StructuredOutput.TryGetAsync<TopicDecision>` (schema mode, then prompt mode, then `null`) with `Temperature 0`, `MaxOutputTokens 60`.
   - `Allowed: false` → fixed refusal; the inner agent is never called.
   - `null` or an exception → **fail open** with a warning.
   - **Follow-up strategy:** the previous user message, read from the session history, is passed to the classifier as context (`Previous user message: … / Message to classify: …`). There is no word-count heuristic.
   - Verified: "And what about their competition?" after the NVIDIA question was allowed, and the agent searched `ticker=NVDA`.
   - Client: `ChatClientFactory.ForModel(services, Guardrails:TopicGuard:Model)`, i.e. the `AI:Chat` client when that is null.
3. **Citation check.** standard-rag and agentic-rag, when `Guardrails:RequireCitations` is on. Every update is forwarded at once; only a 64-character tail is kept to spot `Prompts.Citation()` (`[n]` or `[TICKER-FY2025-Item7]`). If no marker appears, one extra update `_Note: this answer does not cite any retrieved filing excerpt._` is sent with the answer's message id.
4. **Telemetry and metrics.** The middleware tags `Activity.Current` (the `invoke_agent` span) with `secfilings.agent` and `secfilings.refused`, and records `secfilings.chat.turns` (agent, refused) and `secfilings.chat.duration` (s, agent).

A refusal is a normal assistant reply inside the 200 AG-UI stream. 413 and 429 happen before the stream starts.

**Untrusted-context formatting** (verified, not re-implemented): standard-rag keeps `TextSearchProviderOptions.ContextFormatter = Prompts.FormatSources`, agentic-rag keeps the `<excerpt>` tool format. Both now go through `Prompts.Untrusted`, which:
- removes `<|…|>` chat-template tokens and `<think>`/`</think>`;
- collapses more than three newlines to three;
- truncates by **tokens** (`Agent:MaxExcerptTokens` = 512 for sources, a 300-token constant for tool excerpts, which replaces the former 1,200-character cut);
- escapes `<` and `>`.

The indexing-time flag and exclusion (Phase 02/03) stay as the first layer.

## Resilience (§6.3)
- **Approach: `RemoveAllResilienceHandlers()`** on the named `"ollama"` client, then a tuned standard handler. The ServiceDefaults exclusion fallback wasn't needed.
- With `AI:Chat:TimeoutSeconds` = 600 the budget is:
  - attempt timeout 600 s;
  - 2 retries;
  - total timeout 3 × 600 + 30 = 1830 s;
  - circuit-breaker sampling 1200 s (≥ 2 × attempt).

  Retries happen **only** on `HttpRequestException` and 502/503/504, never on a timeout or a 500.
- **Verified in the logs:** `Execution attempt. Source: 'ollama-standard//Standard-Retry' … Result: '200', Attempt: '0', Execution Time: 42989ms`, and a 117 s agentic chat span completed. Under the old defaults that would have been cancelled at 10 s and retried.
- **Streaming caveat:** for a streamed completion the attempt timeout covers only the request up to the response headers. The body is bounded by the request's `CancellationToken`, i.e. `Api:ChatTimeoutSeconds` = 900.
- **Client audit:**
  - The Ollama client was the only `HttpClient` on the defaults that mattered.
  - IngestionInit → Function: no such client exists (the job never calls indexing, Phase 01 notes). IngestionInit's `edgar` client already removes the handlers.
  - AgentClient.Cli: a plain `HttpClient` with an infinite timeout, no ServiceDefaults; unchanged.
  - Evals judge: Ollama judge → the same `"ollama"` client. The OpenAICompatible judge uses the OpenAI SDK's `NetworkTimeout` (`Evals:Judge:TimeoutSeconds`), outside `IHttpClientFactory`.
  - The Aspire `reindex` commands use an infinite-timeout client (Phase 02).

## API hardening (§6.5)
- **Rate limiting:** `AddRateLimiter` plus the `"chat"` policy, a fixed window partitioned by `RemoteIpAddress`, `QueueLimit = 0`, 429. It is configured through `Configure<IOptions<ChatRateLimitOptions>>`, so the validated options drive it. `app.UseRateLimiter()`.
- **Request timeouts:** default 30 s, `"chat"` = `Api:ChatTimeoutSeconds` on `/agents/*`, `"search"` = 120 s on `/api/search`. `/mcp` gets `DisableRequestTimeout()`, because Streamable HTTP keeps a GET open.
- **Request size:** `RequestSizeLimitAttribute(Api:MaxRequestBytes)` metadata on `/agents/*` → **413 `application/problem+json`** (verified).
- **Errors:** `AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages`, already in place since Phase 03. **No custom `IExceptionHandler`** (deviation 6).
- **Health:** the Aspire Npgsql check plus `OllamaHealthCheck` (`ready` tag) in AgentServer and the Functions host. `/health` and `/alive` stay Development-only (`MapDefaultEndpoints`); in production expose them behind auth or on an internal port. The Functions host maps no health endpoint, so its check is registered for an orchestrator to query.
- **Options audit** (all bound with `ValidateOnStart`):

  | Options | DataAnnotations | Cross-field rule |
  |---|---|---|
  | `ChatModelOptions`, `EmbeddingModelOptions` (new) | yes | — |
  | `TelemetryOptions` (new) | — | — |
  | `IngestionOptions`, `ChunkCleaningOptions` (Phase 02) | yes | — |
  | `RetrievalOptions` | yes | strategy is a registered key (Phase 03); `Rerank:CandidatePool ≥ DefaultTop` (new) |
  | `AgentOptions` (incl. `SessionStore` enum) | yes | `CompactAboveTokens ≤ HardTokenBudget < ContextLength` |
  | `GuardrailsOptions`, `ApiOptions`, `ChatRateLimitOptions` (new) | yes where numeric | — |
  | `IngestionInitOptions`, `EvalOptions` (earlier phases) | yes | — |
  | `JudgeOptions` | yes | OpenAICompatible requires `Endpoint` (new) |

  `VectorStoreOptions` doesn't exist on disk (Phase 00 simplification).

## Telemetry (§6.2), verified source names
- **Traces:**
  - `SECFilingsAgenticRag.AI` (chat + embeddings, MEAI);
  - `SECFilingsAgenticRag.AgentServer` (MAF `invoke_agent`, compaction and `execute_tool` spans);
  - `SECFilingsAgenticRag.RetrievalPipeline` (`retrieval.*`);
  - `SECFilingsAgenticRag.IngestionPipeline` (`indexing.*`);
  - the wildcards `Experimental.Microsoft.Extensions.AI*` / `Experimental.Microsoft.Agents.AI*`;
  - Npgsql spans come from the Aspire Npgsql integration (**verified**, no `Npgsql.OpenTelemetry` needed);
  - HTTP spans to Ollama come from HttpClient instrumentation.
- **Meters:** the same four names, `Microsoft.Extensions.AI*` and the `Experimental.*` names.
- Sensitive data: if `Telemetry:EnableSensitiveData` is turned on, prompts appear on both the agent span and the chat spans. That is acceptable for a short debugging session. MEAI and MAF also honor `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`.

## Configuration-only models (§6.6)
- **Offenders found by the step 1 grep:**
  - `Common/AI/EmbeddingModelConstants.cs` (`Name = "nomic-embed-text"` and its comment);
  - an `AgentHostingExtensions.cs` comment (`llama3.2`);
  - the case-insensitive `llama` also matches every "Ollama", as Phase 00 deviation 4 recorded.

  All model names are now configuration.
- **Final grep:**
  - the spec's pattern `qwen|nomic|llama|gpt-` → hits only the provider name "Ollama"/"ollama" (45 lines; provider, connection-string and type names);
  - Phase 00's precise pattern `qwen|nomic|llama\d|llama:|gpt-` → **0 hits**.
- Environment overrides: `AI__Chat__Model`, `AI__Chat__ContextLength`, `AI__Chat__TimeoutSeconds`, `AI__Embedding__Model`, `Retrieval__Rerank__Model`, `Retrieval__MultiQuery__Model`, `Guardrails__TopicGuard__Model`, `Evals__Judge__Model`. `AI__Chat__Provider/Endpoint/ApiKey` don't exist: Common is Ollama-only since Phase 00.

## Results (acceptance criteria, §9)
**How they were run.** The bundled Aspire CLI 13.6.4 (`aspire start`, `wait`, `resource api restart|stop|start`, `otel traces|spans|logs`) drove the stack, with Docker Desktop and native Ollama 0.40.16 on CPU. The corpus was already indexed (824 chunks). The chat model was **`llama3.2`** (`${env:Parameters__chat-model}`), because `qwen3:4b` takes 5-13 minutes per answer (Phase 04/05). The topic guard used the same model.

| # | Check | Result |
|---|---|---|
| — | `dotnet build SECFilingsAgenticRag.slnx` | **0 errors; 4 warnings**, all the pre-existing `NU1903` on `Aspire.Hosting` / `.AppHost` 13.6.4 (deviation 16). No new warning |
| 1 | One trace end to end | **Pass.** `aspire otel traces api` showed one trace for `POST /agents/agentic-rag` (24 spans, 2 m 24 s). Spans in order: `postgresql` (`CREATE TABLE IF NOT EXISTS agent_sessions`, `SELECT session…`) → `invoke_agent agentic-rag` (`secfilings.agent=agentic-rag`, `secfilings.refused=false`) → `chat llama3.2` 11.8 s (topic guard) → `compaction.provider.invoke` / `compaction.compact` → `chat llama3.2` → `execute_tool search_filings` → `postgresql` (catalog `GROUP BY`) → **`retrieval.search`** (`secfilings.strategy=Vector`, `top=8`, `ticker=NVDA`, `result_count=8`, `top_score=0.727`) → `embeddings nomic-embed-text` → `POST http://localhost:11434/api/embed` → `postgresql` (vector query) → `compaction.*` → `chat llama3.2` → `POST /api/chat` → `postgresql` (`INSERT INTO agent_sessions … ON CONFLICT`) |
| 2 | Persistence across an `api` restart | **Pass** (after the jsonb fix, "Found while verifying" 1). Turn 1 thread `90bbf556…`; `aspire resource api restart`; the follow-up "And what about their competition?" had the same thread id, the agent searched `{"query":"NVIDIA competition","ticker":"NVDA"}` (it remembered NVIDIA), and the row `(agentic-rag, 90bbf556…)` has `turns = 2` |
| 3 | Per-agent sessions | **Pass.** "Continue please." with the same thread id on `standard-rag`: a new row `(standard-rag, 90bbf556…, turns 1)`; the answer had no NVIDIA-conversation memory (it searched the literal text and said the filings don't cover it) |
| 4 | Scope | **Pass.** "What's a good recipe for lasagna?" and "Should I buy NVDA stock?" → the fixed refusal, `toolCalls=0`, 2.3 s / 2.7 s; their `invoke_agent standard-rag` spans have `secfilings.refused=true` (also seen on `chat-only`) |
| 5 | Input limits | **Pass.** `('revenue ' * 1500)` → `Your message is too long (1501 tokens; the limit is 1000). Please shorten it.` (160 ms, no model call); a 70 KB body → **413** `application/problem+json` |
| 6 | Rate limiting | **Pass.** In a fresh window, 31 requests → `413 ×30, 429 ×1` (last two `413 429`). An earlier run in a window already used by 5 requests gave `413 ×26, 429 ×5`, as expected |
| 7 | Health | **Pass.** Normal start: `200 Healthy`. With `${env:Parameters__chat-model} = "does-not-exist"`: `200` with `{"status":"Degraded","checks":{"self":…Healthy,"PostgreSql":…Healthy,"ollama":{"status":"Degraded","description":"Ollama has not pulled: does-not-exist."}}}`; indexing still works (`Invoke-Indexing.ps1 -Tickers AAPL` → `skipped=1`). Override removed afterwards |
| 8 | No model literals | **Pass with Phase 00's pattern** (`qwen\|nomic\|llama\d\|llama:\|gpt-`): 0 hits. The spec's literal pattern only matches the provider name "Ollama" (see §6.6 above) |
| 9 | `aspire stop` | **Pass** |
| §7.2 | Fail fast / env override | **Pass.** `AI__Chat__Model=""` → startup `OptionsValidationException: DataAnnotation validation failed for 'ChatModelOptions' members: 'Model' with the error: 'The Model field is required.'`; with `AI__Chat__Model=llama3.2` (outside Aspire) the host starts |
| §6.3 | Resilience | **Pass.** Polly logs `ollama-standard//Standard-Retry … Attempt: '0'` for 43 s and 117 s calls; no retries, no cancellation at 10/30 s. A deliberately 2-minute generation wasn't run on its own: the 117 s agentic chat call covers it |
| §10 | Two concurrent runs on one thread | **Pass.** Two `AgentClient.Cli --thread conc… --agent chat-only` at once: both answered (17 s, 25 s); the row has `turns = 1` and 2 history messages (no interleaving); log: `Conversation conc1729659801 of chat-only was saved by a concurrent run; this run's turn was not stored` |
| §6.1b | 4-turn compaction check | **Not run** as a dedicated 4-turn conversation (about 5 minutes with `llama3.2`, much longer with `qwen3:4b`; Phase 09 adds the deterministic test). Verified instead: compaction spans run before every model call, and the persisted session keeps the full history (the state bag holds both the history and compaction's own state) |
| §6.4.5 | Citation note | **Not triggered** in these runs: every RAG answer cited. The streaming path that forwards updates is exercised by every run |

## Found while verifying (bugs fixed in this phase's own code)
1. **`jsonb` broke every resumed conversation.**
   - Symptom: every second turn returned AG-UI `RUN_ERROR` / `StreamingError` (empty CLI answer, exit 0).
   - Cause: Postgres `jsonb` stores object keys in its own order (`{"text": "Hi", "$type": "text"}`). System.Text.Json reads the polymorphic `$type` discriminator only when it comes first, so `DeserializeSessionAsync` failed for any content item.
   - Fix: the `session` column is `json`, which stores the text as written. `partitions` stays `jsonb` because it needs equality in the primary key.
   - The spec's `jsonb` is wrong for STJ polymorphic payloads (deviation 1).
2. **Disposed `JsonDocument`.** The first version parsed the row with `using var json = JsonDocument.Parse(...)`. The session's state bag can keep referring to that element, so the row is now parsed with `JsonSerializer.Deserialize<JsonElement>` (a self-owned clone).
3. **AG-UI hides the exception.** The stream ends with `StreamingError`, and no error is logged. To diagnose, wrap the run temporarily, or inspect the stored row.

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | `session jsonb`, `NpgsqlDbType.Jsonb` | `session json` / `NpgsqlDbType.Json`; table also has `partitions jsonb` in the PK | jsonb reorders keys and breaks STJ `$type` metadata ("Found while verifying" 1); the installed key type `AgentSessionStoreKey` has partitions that are part of the identity |
| 2 | Members `GetSessionAsync(agent, string conversationId)` … | `AgentSessionStoreKey` overloads; class is `[Experimental("MAAI001")]` (scoped pragma in the store file) | Installed 1.28 API; Learn's sample shows the older `string` shape |
| 3 | Concurrency: a per-key `SemaphoreSlim` in the middleware, or reject | Optimistic concurrency in the store (row version = `turns`; first save wins; warning) | The thread id never reaches middleware, and the session is loaded before middleware runs, so a lock there can't prevent a stale overwrite |
| 4 | Topic-guard follow-ups: skip below 12 words, or pass the previous message | Pass the previous user message (from `InMemoryChatHistoryProvider.GetMessages(session)`) | One code path, no magic word count; verified on the AC2 follow-up |
| 5 | Middleware order "guardrails outside `UseOpenTelemetry`" | `UseOpenTelemetry(...)` first (outer), then `Use(guardrails)` | First added = outermost; with guardrails outside, a refusal would have no agent span. The spec allowed the swap; verified `secfilings.refused=true` on refused spans |
| 6 | Custom `IExceptionHandler` (logs, ProblemDetails, no stack trace outside Development) | Not added | Built-ins already do exactly that: `UseExceptionHandler` + `AddProblemDetails` log the exception and write ProblemDetails, and WebApplication shows the developer page only in Development |
| 7 | `static class Guardrails` | `sealed class Guardrails(agentName, citesSources, services)`, one instance per agent; delegates are instance methods | The spec also says "resolve services through the factory's IServiceProvider (closure), not statics" |
| 8 | Compaction defaults 2500 / 10 / 3500 | 4000 / 10 / 6000; `Truncation minimumPreservedGroups: 4` | Sized for the configured 8192 window (Phase 06) instead of a 4k one; the default 32 groups would make the backstop inert |
| 9 | `retrieval.search` span at the two call sites plus inside `PostgresHybridSearch` | One span inside `FilingSearch.SearchFilingsAsync` (renamed from `RetrievalPipeline.Process`, tags `rag.*` → `secfilings.*`) | That method is the one function all three callers (standard-rag, the tool, `/api/search`) go through; the hybrid SQL appears as its child Npgsql span |
| 10 | `indexing.embed_batch` span in the writer | Not added | MEDI's `VectorStoreWriter` has no batch hook; the built-in MEAI `embeddings` span already covers each batch |
| 11 | `indexing.document` as the parent of its work | Recorded after the document finishes, with its measured start time, as a sibling of the MEDI/embedding spans under `indexing.run` | `IngestionPipeline.ProcessAsync` reports a document only after processing it |
| 12 | Metric `secfilings.Ingestion.documents` | `secfilings.ingestion.documents` | OTel naming is lower case; the capital `I` looks like a typo |
| 13 | `ChatModelOptions.Provider`, `EmbeddingOptions.Provider/Dimensions`, `AI__Chat__Provider/Endpoint/ApiKey` | Only `Model` (+ `ContextLength`, `TimeoutSeconds`) and `AI:Embedding:Model`; dimensions stay `EmbeddingModelConstants.Dimensions` | Ollama-only Common (Phase 00); the vector column's dimension is a compile-time attribute value |
| 14 | `Parameters:embedding-dimensions` | Not added | Same reason; an embedding model must still produce 768 dimensions |
| 15 | Attempt timeout `max(chat, embedding TimeoutSeconds)` | One `AI:Chat:TimeoutSeconds` (600) for the shared Ollama client | Embeddings take milliseconds; the chat call sets the budget |
| 16 | `0 warnings` | 4 warnings, all the pre-existing `NU1903` (GHSA-f7vh-h5g3-7jrh on `Aspire.Hosting` 13.6.4, AppHost) | Bumping every `Aspire.*` package, the SDK attribute and the bundled CLI to 13.6.5 is a cross-cutting toolchain change (Phase 04 left it to you); no new warnings |
| 17 | `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` latest stable (10.0.15) | 10.0.14 | Same patch level as every other `Microsoft.Extensions.*` package in CPM |
| 18 | Tokenizer registered in IngestionPipeline | Moved to `AddSecFilingsAI` (Common) | AgentServer must not reference the ingestion library; one registration for both hosts |
| 19 | Tool excerpt cut at 1,200 characters | 300 tokens (`Prompts.Untrusted`) | Spec: truncate by token count, not characters |

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `PostgresAgentSessionStore` | `AgentSessionStore` (**used**, base class); `InMemoryAgentSessionStore`, `NoopAgentSessionStore` | MAF ships no durable store; Learn says to provide one |
| `Guardrails` | `AIAgentBuilder.Use(runFunc, runStreamingFunc)` (**used**), `GetResponseAsync<T>` via `StructuredOutput` (**used**), `Tokenizer.CountTokens` (**used**) | The delegate bodies (our limits, scope and citation format) |
| `OllamaHealthCheck` | `IHealthCheck` (**used**); Aspire health checks | No Aspire client integration for native Ollama; must check configured model names |
| `ChatModelOptions`, `EmbeddingModelOptions`, `TelemetryOptions`, `AgentOptions` additions, `GuardrailsOptions`/`TopicGuardOptions`, `ApiOptions`, `ChatRateLimitOptions` | Options pattern (**used**) | Our configuration sections |
| `TelemetryNames` | — | The shared source/meter names |
| *(not added)* conversation-store interface, stored-conversation record, chat service, SSE event types, topic-guard or validator interfaces, custom `IExceptionHandler`, `ChatHistoryProvider`, per-thread lock | `AgentSessionStore`, AG-UI events, MAF middleware, `GetResponseAsync<T>`, `UseExceptionHandler` + ProblemDetails, `InMemoryChatHistoryProvider` in session state | Built-ins cover them |

## Hardening checklist
- [x] No model names, dimensions or endpoints in code; all in `appsettings.json`, overridable by environment variables (`AI__Chat__Model`, `AI__Embedding__Model`, …); the AppHost parameters are the single source under Aspire. (Dimensions: a code constant by design, deviation 13.)
- [x] All options are validated on start; a missing required setting fails fast with a clear message.
- [x] Environment-variable overrides for deployment are documented in the README.
- [x] Secrets only in user-secrets or the environment (never in appsettings or git). There are none in this phase.
- [x] Sessions persisted through `PostgresAgentSessionStore`, keyed by `(agent, thread)`; isolation noted for multi-user.
- [x] One trace per agent run covering the agent, tools, retrieval, embeddings and SQL; custom metrics visible.
- [x] LLM HTTP calls: long timeouts, no retry on timeout, valid circuit breaker.
- [x] Guardrails as MAF middleware: scope prompt, topic guard (`GetResponseAsync<T>`), untrusted-context delimiting, token limit (ML.Tokenizers), citation check.
- [x] API: rate limiting on `/agents/*`, request size limit, ProblemDetails, request timeouts, health (Postgres + Ollama models).

## Next steps
- **Authentication and authorization** on `/agents/*`, `/api/*` and `/mcp` (Learn: AG-UI must not face untrusted clients directly). Then turn session isolation on: `WithSessionStore(..., withIsolation: true)` (the default) with `AddHttpContextAccessor()` + `UseClaimsBasedAgentIsolation()` (`Microsoft.Agents.AI.Hosting.AspNetCore`). The isolation key lands in `AgentSessionStoreKey.Partitions`, which the table already keys on.
- **Multi-tenant session ownership:** tenant id in the partitions; row-level access in Postgres.
- **Retention:** sessions hold the full chat history (PII-sensitive and growing) forever. Add a TTL job (`DELETE … WHERE updated_at < now() - interval …`), encryption at rest, and a "forget this conversation" endpoint through `DeleteSessionAsync`.
- Rate limiting per authenticated user instead of per IP.
- Phase 09: the tests listed in its §8.7, including a deterministic compaction test and the `TelemetryNames` = ServiceDefaults names assertion.

## Commands & operating notes
- **Start / check / stop** (bundled CLI matching the 13.6.4 AppHost):
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  dotnet build SECFilingsAgenticRag.slnx                       # before starting: a running AppHost locks bin folders
  ${env:Parameters__chat-model} = "llama3.2"                   # optional: fast demo model
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost; & $aspire wait api
  Invoke-WebRequest http://localhost:5180/health -SkipHttpErrorCheck | Select-Object StatusCode, Content
  & $aspire stop --apphost src/SECFilingsAgenticRag.AppHost
  ```
- **Rebuild only the API while the AppHost runs:** `& $aspire resource api stop --apphost src/SECFilingsAgenticRag.AppHost; dotnet build src/SECFilingsAgenticRag.AgentServer; & $aspire resource api start --apphost src/SECFilingsAgenticRag.AppHost`.
- **Traces from the terminal:** `& $aspire otel traces api --apphost src/SECFilingsAgenticRag.AppHost`, then `& $aspire otel spans api --apphost src/SECFilingsAgenticRag.AppHost --trace-id <id> --format Json -n 100`. The flag is `--trace-id`; `-t` is not accepted on `spans`. `--search` did not filter by attribute here; filter the JSON instead.
- **Conversations:**
  ```powershell
  $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
  'select agent_id, conversation_id, turns, updated_at from agent_sessions order by updated_at;' | docker exec -i $pg sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d secfilings'
  ```
  Forget all: `DROP TABLE agent_sessions;` (recreated lazily). The evals use `Agent:SessionStore` = `InMemory` (the code default); AgentServer's `appsettings.json` sets `Postgres`.
- **Configuration** (env vars):
  - `Agent__SessionStore` (`Postgres` | `InMemory`), `Agent__MaxInputTokens`, `Agent__MaxExcerptTokens`, `Agent__CompactAboveTokens`, `Agent__MaxHistoryTurns`, `Agent__HardTokenBudget`;
  - `Guardrails__TopicGuard__Enabled`, `Guardrails__TopicGuard__Model`, `Guardrails__RequireCitations`;
  - `Api__ChatTimeoutSeconds`, `Api__MaxRequestBytes`;
  - `RateLimiting__Chat__PermitLimit`, `RateLimiting__Chat__WindowSeconds`;
  - `AI__Chat__Model`, `AI__Chat__ContextLength`, `AI__Chat__TimeoutSeconds`, `AI__Embedding__Model`;
  - `Telemetry__EnableSensitiveData` (environment only, temporarily).
- **Gotchas:**
  - The topic guard costs one model call per message (about 2-12 s with `llama3.2`). With `qwen3:4b` its 60-token output budget is spent on reasoning, so it fails open after two attempts. Set `Guardrails__TopicGuard__Model=llama3.2`, or disable the guard.
  - Off-topic small talk ("What is my name?") is refused by design; greetings and thanks are allowed.
  - Rate limiting counts **every** `/agents/*` request from your IP, including 413s and CLI runs.
  - AG-UI swallows exceptions as `RUN_ERROR` / `StreamingError` without logging them ("Found while verifying" 3).
  - A run on a thread that another run saved in the meantime is not stored (warning in the log). That is intended.
  - Never store the session as `jsonb`.
  - `/health` is mapped in Development only.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/agent-framework/hosting/self-hosting/ (#persist-hosted-sessions, #secure-session-continuation) | Durable store is the app's job; key by `agent.Id` + opaque id; independent session per get; use the agent's serialization APIs; history in session state is persisted with it; sensitive data; isolation via `IsolationKeyScopedAgentSessionStore` / `UseClaimsBasedAgentIsolation` | `PostgresAgentSessionStore`; stable `Id = name` (the default id is random); `withIsolation: false` justified only for single-user; next steps. **Docs vs reality:** Learn's sample shows `string sessionStoreId`; 1.28 uses `AgentSessionStoreKey` (with partitions) and marks the type `MAAI001` |
| 2 | https://learn.microsoft.com/agent-framework/concepts/agents/conversations/compaction | `MAAI001`; strategies and triggers; register on `ChatClientBuilder.UseAIContextProviders` so it runs inside the tool loop and doesn't change stored history; agent-level registration skips the tool loop | Compaction is the innermost chat-client stage for all three agents; verified in traces before every model call. **Docs vs reality:** the ctor parameter is `minimumPreservedGroups`, not `minimumPreserved` |
| 3 | https://learn.microsoft.com/agent-framework/concepts/agents/middleware/termination | Guardrail middleware short-circuits by returning a response; `Use(runFunc, runStreamingFunc)` | `Guardrails` returns a refusal without calling the inner agent; both delegates supplied (non-streaming = streaming + `ToAgentResponseAsync`) |
| 4 | https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.hosting.agentsessionstore + package XML docs (`Microsoft.Agents.AI.Abstractions` 1.28.0) | Exact abstract members, `GetOrCreateSessionAsync` default, `AgentSessionStoreKey` | API table |
| 5 | Package XML docs `Microsoft.Agents.AI` 1.28.0 (`AIAgentBuilder.Use`, `UseOpenTelemetry`, `OpenTelemetryAgent.EnableSensitiveData` honours `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`, `ChatClientAgentOptions.Id`, `AIAgent.Id` random by default, `InMemoryChatHistoryProvider.GetMessages`, `UseProvidedChatClientAsIs`) | Signatures and defaults | Agent pipeline; topic-guard follow-up context |
| 6 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.resiliencehttpclientbuilderextensions.removeallresiliencehandlers and https://learn.microsoft.com/dotnet/core/resilience/http-resilience#remove-standard-resilience-handlers (Phase 01 rows 1-2, re-applied) | Replacing the ServiceDefaults handler for one named client; `EXTEXP0001` | `"ollama"` client; scoped pragma |
| 7 | Package XML docs `Microsoft.Extensions.Http.Resilience` 10.11 (`IHttpStandardResiliencePipelineBuilder.PipelineName`, `HttpStandardResilienceOptions`) | How to configure the handler from DI options | Named options `Configure<IOptions<ChatModelOptions>>` (there is no `Configure((o, sp) => …)` on this builder) |
| 8 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.chatclientstructuredoutputextensions.getresponseasync (Phase 06 row 6) | `useJsonSchemaResponseFormat` fallback | Topic guard reuses `StructuredOutput` (schema → prompt → null = fail open) |
| 9 | https://learn.microsoft.com/dotnet/ai/how-to/use-tokenizers | `CountTokens`, `GetIndexByTokenCount` | Input limit and token-based excerpt truncation |
| 10 | Package XML docs `Microsoft.Extensions.AI` 10.11 (`UseOpenTelemetry(ILoggerFactory?, string? sourceName, Action<…>?)` for chat and embeddings; `EnableSensitiveData` default from the env var) | Overloads | `sourceName: TelemetryNames.AI` on both pipelines |
| 11 | ASP.NET Core rate limiting, request timeouts and request size: https://learn.microsoft.com/aspnet/core/performance/rate-limit, https://learn.microsoft.com/aspnet/core/performance/timeouts, https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis (endpoint metadata) | `AddRateLimiter` / `RequireRateLimiting` / `RejectionStatusCode`, `AddRequestTimeouts` / `WithRequestTimeout` / `DisableRequestTimeout`, `RequestSizeLimitAttribute` metadata | §6.5 wiring; behavior verified (413/429) rather than re-read in depth |
| 12 | https://learn.microsoft.com/aspnet/core/fundamentals/error-handling (ProblemDetails + `UseExceptionHandler`) | Built-in handler logs and writes ProblemDetails | No custom `IExceptionHandler` (deviation 6) |
| 13 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-prompt-engineering and https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-enrichment-phase (Phase 02 rows 12/14) | Retrieved text can carry injected instructions; guard at enrichment | Defense in depth: index-time flag (kept) + query-time `Untrusted` fencing, scope prompt, topic guard (RAG guide: **query phase**, prompt grounding) |
| 14 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide | Phase order | This phase is operational (cross-cutting); no new RAG phase. Guardrails sit around the query phase, telemetry spans the indexing and query phases |
| 15 | *external* PostgreSQL docs on `json` vs `jsonb` (jsonb "does not preserve … the order of object keys") and System.Text.Json polymorphism (metadata properties must come first unless `AllowOutOfOrderMetadataProperties`) | Why resumed sessions failed | `session json` (deviation 1) |
| 16 | *external* Ollama `/api/tags` response shape (`models[].name`, untagged pulls listed as `name:latest`) | Health check parsing | `OllamaHealthCheck` |

## Suggested commit message
```
feat: production hardening — persistent conversations, OpenTelemetry, resilience, guardrails

- PostgresAgentSessionStore (built-in AgentSessionStore), sessions keyed by agent + AG-UI thread,
  optimistic concurrency (first save wins); session stored as json (jsonb breaks STJ $type)
- GenAI + custom spans/metrics (SECFilingsAgenticRag.*), one trace per run in the Aspire dashboard
- Built-in compaction (CompactionProvider on the chat client, inside the tool loop)
- Ollama HttpClient via IHttpClientFactory with tuned resilience (long timeouts, no retry on timeout)
- Guardrails as MAF agent-run middleware: token limit (ML.Tokenizers), topic guard (GetResponseAsync<T>),
  citation check; hardened untrusted-context escaping
- Rate limiting on /agents/*, request size limits, request timeouts, Ollama/Postgres health checks, ValidateOnStart
- config-only model names (AI:Chat:Model, AI:Embedding:Model) with env-var overrides and AppHost parameters
```
