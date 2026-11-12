# Phase 04 notes: Chat agents (chat-only → standard RAG) over AG-UI + console client

## What was built
- `src/SECFilingsAgenticRag.AgentServer` (no new project):
  - `AgentNames` (`chat-only`, `standard-rag`): registration key and route segment.
  - `AgentOptions` (section `Agent`: `RetrievalTop` 1..20, default 5; `RecentMessageMemoryLimit` 0..20, default 4), `ValidateDataAnnotations().ValidateOnStart()`.
  - `Prompts` (`ChatOnly`, `StandardRag`, `FormatSources` = the `TextSearchProvider` `ContextFormatter`).
  - `AgentHostingExtensions.AddSecFilingsAgents(IHostApplicationBuilder)`: MAF hosting `AddAIAgent` for both agents, each `.WithInMemorySessionStore(withIsolation: false)`. `standard-rag` adds the built-in `TextSearchProvider` whose delegate is `FilingSearch.SearchFilingsAsync`.
  - `Program.cs`: `builder.AddSecFilingsAgents()`, `builder.Services.AddAGUIServer()`, `app.MapAGUIServer(name, $"/agents/{name}")` for both agents.
  - `appsettings.json`: `"Agent": { "RetrievalTop": 5, "RecentMessageMemoryLimit": 4 }`.
- `src/SECFilingsAgenticRag.AgentClient.Cli` (console, outside Aspire, no project references): one `Program.cs` on `AGUIChatClient` (`--api`, `--agent`, `--ask`, `--thread`, `--show-tool-args`; REPL with `/new`, `/exit`, Ctrl+C; summary line; exit codes 0/1/2).
- No AppHost change.

## Package versions (CPM)
| Package | Version | Project |
|---|---|---|
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | 1.28.0-preview.261106.1 (latest; prerelease only) | AgentServer |
| `AGUI.Client` | 1.0.1 (latest, **stable**) | AgentClient.Cli |
| *(transitive)* `Microsoft.Agents.AI` 1.28.0, `Microsoft.Agents.AI.Hosting` 1.28.0-preview.261106.1, `AGUI.Server`/`AGUI.Abstractions` 1.0.0 | via the AG-UI hosting package | AgentServer |

No diagnostics suppressed; no experimental-API warnings were raised.

## Exact API names used (installed packages, checked in their XML docs)
| Spec | Installed API | Note |
|---|---|---|
| `builder.AddAIAgent(name, (sp, key) => …)` → `IHostedAgentBuilder` | `HostApplicationBuilderAgentExtensions.AddAIAgent(IHostApplicationBuilder, string, Func<IServiceProvider, string, AIAgent>, ServiceLifetime)` in `Microsoft.Agents.AI.Hosting` | Needs `using Microsoft.Agents.AI.Hosting;`. Throws if the agent's `Name` differs from the key, so `Name = name` |
| `WithInMemorySessionStore(withIsolation: false)` | same | Isolation is **on** by default and then requires an isolation-key provider |
| `AsAIAgent(ChatClientAgentOptions, …)` | `ChatClientExtensions.AsAIAgent(IChatClient, ChatClientAgentOptions, ILoggerFactory?, IServiceProvider?)` | |
| `ChatClientAgentOptions.ChatOptions.Instructions` / `AIContextProviders` | same | |
| `TextSearchProvider(searchAsync, options, loggerFactory)` | same; `TextSearchProviderOptions.{SearchTime, RecentMessageMemoryLimit, ContextFormatter}` | `ContextFormatter` replaces `ContextPrompt`/`CitationsPrompt` |
| `MapAGUI(endpoints, agentName, pattern)` | **`MapAGUIServer(endpoints, agentName, pattern)`** | Renamed. Learn's API reference (package v1.13) still says `MapAGUI`; the getting-started page and the 1.28 package say `MapAGUIServer` |
| `services.AddAGUIServer()` | same | Configures AG-UI JSON only, no auth |
| `new AGUIChatClient(new AGUIChatClientOptions(http, url))` | same (`AGUI.Client`) | |
| `agui.AsAIAgent()` + `RunStreamingAsync` | **not used**: `IChatClient.GetStreamingResponseAsync` directly | Deviation 2 |
| `AsAGUIMessages()` (Learn sample) | `AsAGUIMessages(IEnumerable<ChatMessage>, JsonSerializerOptions)`, **not needed** | The client fills `messages` itself (below) |

## How thread ids are carried
- Learn ("Conversation continuity"): `AGUIChatClient` is **stateless**. A continuation is a new `RunAgentInput` with the same `threadId`, sent through `ChatOptions.RawRepresentationFactory`. `ChatOptions.ConversationId` and client-side `AgentSession` state play no part.
- AgentClient.Cli generates a GUID thread id on the first run (or takes `--thread`) and sends `RawRepresentationFactory = _ => new RunAgentInput { ThreadId = thread }` on every run. Verified by capturing the request body with a local `HttpListener`: the client fills in `runId` (`run_…`), `protocolVersion: "1.0"` and `messages` from the call arguments, so only the new user message goes over the wire.
- **The message id matters.** `new ChatMessage(...)` has no `MessageId`, which goes on the wire as `"id":""`. With a blank id the second turn had **no memory** ("I don't have that information"). A `curl` probe with ids `m1`/`m2` on the same thread did remember. The server reconciles incoming messages with the stored session by id. Fix: `MessageId = Guid.NewGuid().ToString("N")`. After it: `PELICAN` recalled on the second run.
- `parentRunId` is **not** required for the hosted session to resume: `curl` with only `threadId` recalled the previous turn (Learn's sample sends it; MAF's `MapAGUIServer` keys the session store on the thread id alone).
- Server side, `MapAGUIServer(agentName, pattern)` resolves the session store keyed by the agent name per request, so the same thread id under `chat-only` and `standard-rag` is two separate sessions. `--agent` is fixed per process, so changing it means a new process and a new thread unless `--thread` is passed explicitly (that then starts a fresh session in the other agent).

## `TextSearchProvider` memory scope
**Per session.** The 1.28 XML docs define `TextSearchProvider.TextSearchProviderState` as "the per-session state of a TextSearchProvider stored in the AgentSession" (`RecentMessagesText`), and its key is `TextSearchProviderOptions.StateKey`. So one provider instance per agent is safe, and `RecentMessageMemoryLimit` stays at 4. The state is saved with the session through the session store.

By default only **user** messages from that memory go into the search input (`RecentMessageRolesIncluded` = `User`).

## Qwen3 reasoning and `/no_think` (measured)
Ollama 0.40.16, `qwen3:4b` (`359d7dd4bcda`), CPU only, about 10 tokens/s:

| Request | Result |
|---|---|
| default (no `think`) | Reasoning arrives in Ollama's separate `thinking` field, so content has **no `<think>` tags**. OllamaSharp turns it into `TextReasoningContent`, and MAF AG-UI streams it as `REASONING_*` events |
| system prompt ending in `/no_think` | **No effect**: the model still reasons in `thinking` |
| `"think": false` | Worse: untagged reasoning ("Hmm, the user asked me…") leaks into `content` |

Decision: no `/no_think`, no `think` option, no `<think>` stripping code. AgentClient.Cli prints only `update.Text` (TextContent), so reasoning is never shown. AC3's `<think>` grep found nothing (Results). If a future model or Ollama version puts tags back into content, add a streaming filter then.

**Latency consequence:** this `qwen3:4b` build always reasons. A 3-word "say hi" spent over 300 s reasoning, and a standard-rag answer takes about 8 minutes (Results). The spec's 30-90 s assumes a non-reasoning answer. That is a model choice, not code: `AI:Chat:Model` / `Parameters:chat-model` switches it (AC5 with `qwen2.5:1.5b`: 39 s).

## Results (acceptance criteria, §9)
Run under Aspire with the bundled 13.6.4 CLI (`aspire start` / `wait` / `stop` all worked, so Phase 03's unverified check 9 is now confirmed: `api` is healthy on 5180). Docker Desktop had to be started first. Default chat model `qwen3:4b`, CPU only.

| # | Check | Result |
|---|---|---|
| 1 | `aspire start; wait ingestion-init/indexing; Invoke-Indexing.ps1; wait api` | **Pass**: `api` and `indexing` healthy; indexing `indexed=0 skipped=5 failed=0` (persistent Postgres volume kept the 824 chunks). The script first needed `-BaseUrl`; fixed afterwards (deviation 10) |
| 2 | Raw AG-UI `curl.exe -N` to `/agents/standard-rag` | **Pass**: `RUN_STARTED` at 83 s, `REASONING_*` (1,537 deltas) 83-386 s, `TEXT_MESSAGE_CONTENT` **439 deltas spread over 386-472 s** (progressive), `RUN_FINISHED` at 472 s. The text has `[1]`, `[4]`, `[5]` markers and a `Sources:` list of `NVIDIA CORP 10-K FY2026 — Item 1A. Risk Factors` titles. The model listed titles without URLs |
| 3 | `--ask "What are NVIDIA's main risk factors?"` | **Pass**: exit 0; 17 risk factors cited `[1]`/`[4]`/`[5]`; Sources list (all `Item 1A`, one `Item 7`); `agent=standard-rag thread=8e22… toolCalls=0 elapsedMs=623410`. `<think>` grep over all output: **0 matches** |
| 3 | Follow-up `--ask "Which of those relate to export controls?" --thread <id>` | **Pass**: stays on NVIDIA, refers to "your original list", cites export-control passages ("export controls may disrupt our supply and distribution chain…"); exit 0, 693 s. Same thread id in the summary line |
| 3 | `--ask "What was Apple's fiscal year end date?"` (no `--thread`) | **Pass**: new thread; "Apple's fiscal year ends on the last Saturday of September [1][2]", Sources `Apple Inc. 10-K FY2025 — Item 7 / Item 8` with sec.gov URLs; 224 s |
| 4 | Same NVIDIA question, `--agent chat-only` | **Pass**: no `[n]` markers, no Sources list; four generic points (AI-demand volatility, AMD/Intel competition, supply chain, R&D cost) with the note "based on general market knowledge… not direct SEC filings"; 94 s. **Difference:** standard-rag returned 17 filing-specific factors (data-center energy capacity, channel inventory, the EU AI Act, state AI laws effective 2026-01-01, export controls) traceable to Item 1A; chat-only was plausible but generic, unverifiable and missed every FY2026-specific item. That is the hallucination and staleness gap RAG closes |
| 5 | `${env:Parameters__chat-model}="qwen2.5:1.5b"`, restart, `--ask "What does Apple sell?"` | **Pass**: "Apple sells smartphones, personal computers, tablets, wearables, and accessories. [1]", exit 0, **39 s**; `ollama ps` lists `qwen2.5:1.5b` loaded. The 1.5B model cited `[1]` but skipped the Sources list. Env var removed, stack stopped |
| 6 | `dotnet build SECFilingsAgenticRag.slnx` | **0 errors; 4 warnings, none new**: `NU1903` (high-severity advisory GHSA-f7vh-h5g3-7jrh on `Aspire.Hosting` / `Aspire.Hosting.AppHost` 13.6.4, AppHost project, untouched here). 13.6.5 is available; bumping every `Aspire.*` package plus the SDK attribute is a cross-cutting change left for the user |
| 7 | `aspire stop` | **Pass** |
| — | Model-name grep (`qwen\|nomic\|llama\d\|llama:\|gpt-`) | Only `Common/AI/EmbeddingModelConstants.cs` (pre-existing, Phase 00) |
| — | `--ask "hello" --agent chat-only` (step 5 check) | exit 0, summary line, 50 s |
| — | API down (`--ask "hi"` after `aspire stop`) | exit **2**, stderr `API unreachable at http://localhost:5180/agents/standard-rag: No connection could be made…` |
| — | Search input over 2,000 characters | Run continues without sources (see "Anything Phase 05 must know") |

**Latency (CPU, qwen3:4b):** time to `RUN_STARTED` is 10-20 s for chat-only and about 80 s for standard-rag (prompt processing of ~2.5k source tokens). The reasoning phase then dominates: 4-11 minutes per standard-rag answer, against the spec's 30-90 s. With `qwen2.5:1.5b` (no reasoning) a standard-rag answer took 39 s. Runs of 623 s and 693 s exceeded Common's 10-minute Ollama `HttpClient.Timeout` without failing: for a streamed response that timeout covers only the response headers, as the spec's cross-cutting timeout rule says.

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | `MapAGUI(AgentNames.X, "/agents/x")` | `MapAGUIServer(AgentNames.X, $"/agents/{AgentNames.X}")` | Renamed in the installed 1.28 package (spec §6.4 allowed "whichever exists") |
| 2 | Client: `agui.AsAIAgent()` so "the client side has an AgentSession carrying the AG-UI thread id"; iterate `RunStreamingAsync` | `AGUIChatClient` used as the `IChatClient` it is: `GetStreamingResponseAsync([message], new ChatOptions { RawRepresentationFactory = _ => new RunAgentInput { ThreadId = thread } })` | Learn: the client is stateless and the thread id travels in `RunAgentInput`, not in a client session. A client-side `ChatClientAgent` would add its own in-memory history and resend it every turn, which conflicts with the server-owned session. So `Microsoft.Agents.AI` is not referenced by the client. `update.Text` / `Contents` rendering is identical |
| 3 | Add `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Hosting` to AgentServer | Only `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | Learn (self-hosting): protocol integration packages already reference `Microsoft.Agents.AI.Hosting`; "install it directly only when your host doesn't use a protocol integration package". Its nuspec also pins `Microsoft.Agents.AI` 1.28.0 |
| 4 | `AgentNames.AgenticRag`; `AgentOptions.MaxToolIterations` / `MaxInputTokens` and their `appsettings` keys | Omitted | CLAUDE.md "every line has purpose": nothing reads them until Phases 05/07, which add them with their consumers (same rule as Phase 03 deviation 3) |
| 5 | `ChatMessage` as is | `MessageId = Guid.NewGuid()` on each user message | Without it, follow-ups lost the conversation (see "How thread ids are carried") |
| 6 | `/no_think`, `<think>` stripping | Neither | Measured: `/no_think` is ignored, and content never contains tags on this Ollama (see "Qwen3 reasoning") |
| 7 | Errors in red unless `Console.IsOutputRedirected` | Errors go to **stderr**, red unless `Console.IsErrorRedirected` | Errors belong on stderr so stdout stays the answer plus the summary line; the color check must look at the stream being written |
| 8 | Ctrl+C cancels the current answer | Done in the REPL. In `--ask` mode Ctrl+C ends the process (default behavior) | One-shot has nothing to return to |
| 9 | Phase 03 deviation 9: add `JsonStringEnumConverter` with the first wire enum | Not added | No enum of ours crosses the wire; AG-UI endpoints use their own JSON options (`ConfigureAGUIJsonOptions`) |
| 10 | AC1 `./scripts/Invoke-Indexing.ps1` | **Fixed the script** (pre-existing Phase 02 bug): it now parses `aspire describe indexing --format json` and takes `resources[displayName=indexing].urls[name=http].url` | The old regex took the first `localhost`/`127.0.0.1` URL in the JSON text, which is Azurite's `127.0.0.1:10000` from the storage connection strings under `environment`, so it printed an empty report and exited 0. Verified: with the 13.6.4 CLI on PATH it finds the Function (`skipped=1`); with the mismatched global 13.2.2 CLI it exits 2 with "Cannot find the indexing endpoint" |

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `AgentNames` | — | Our registration keys and routes |
| `AgentOptions` | Options pattern (**used**) | The pattern needs a class per section |
| `Prompts` (incl. `FormatSources`) | `TextSearchProviderOptions.ContextFormatter` (**used**), the default `ContextPrompt`/`CitationsPrompt` | Our instructions, and untrusted-data delimiting plus numbering that the default format doesn't do |
| `AgentHostingExtensions` | MAF hosting `AddAIAgent` (**used**), `TextSearchProvider` (**used**), `InMemoryAgentSessionStore` (**used**) | Registration entry point only (docs/01 rule 4) |
| *(not added)* custom context provider, chat service, SSE writer/events, conversation store, client SSE parser, client session | `TextSearchProvider`, `MapAGUIServer`, `AgentSessionStore`, `AGUIChatClient` | Built-ins cover all of them |

## Anything Phase 05 must know
- Add `AgentNames.AgenticRag`, one more `builder.AddAIAgent(...)` with its session store, and one more `app.MapAGUIServer(...)` line. `MaxToolIterations` goes into `AgentOptions` with that agent.
- `MapAGUIServer` already streams tool calls: AgentClient.Cli prints `🔎 name(...)` / `↳ summary` for `FunctionCallContent` / `FunctionResultContent` and counts `toolCalls` (only exercised with 0 calls so far).
- Clients must give every message a non-empty unique `id` (deviation 5).
- A search input over 2,000 characters makes `SearchFilingsAsync` throw; `TextSearchProvider` **catches it**, logs `TextSearchProvider: Failed to search for data due to error` (`ArgumentException: The query exceeds 2000 characters`), and the run continues with **no Sources block at all** (verified with a 2,416-character question). The input is the current user message plus up to `RecentMessageMemoryLimit` earlier user messages, so very long pasted questions silently lose retrieval. The prompt still forbids outside knowledge.
- MEAI and MAF ActivitySources are still not exported (Phase 07).

## Commands & operating notes
- **Start the stack** (bundled CLI that matches the 13.6.4 AppHost; the global `aspire` is 13.2.2):
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  dotnet build SECFilingsAgenticRag.slnx           # before starting: a running AppHost locks bin folders
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost
  & $aspire wait api; & $aspire wait indexing
  $env:Path = "$(Split-Path $aspire);$env:Path"   # the script calls `aspire`; it must be the 13.6.4 CLI
  ./scripts/Invoke-Indexing.ps1
  & $aspire stop --apphost src/SECFilingsAgenticRag.AppHost
  ```
  Docker Desktop must be running first (Postgres is a persistent container; the index survives restarts).
- **AgentClient.Cli** (own terminal, outside Aspire; API default `SECFILINGS_API`, then `http://localhost:5180`):
  ```powershell
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli                                   # REPL: /new, /exit, Ctrl+C cancels the answer
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --agent chat-only                # baseline chatbot
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --ask "What are NVIDIA's main risk factors?"
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --ask "Which of those relate to export controls?" --thread <id>
  dotnet run --project src/SECFilingsAgenticRag.AgentClient.Cli -- --api http://localhost:5181 --agent standard-rag --show-tool-args
  ```
  One-shot prints the answer, then `agent=<name> thread=<id> toolCalls=<n> elapsedMs=<ms>`. Exit codes: 0 completed, 1 run error / broken stream / unknown agent (HTTP 404), 2 API unreachable. Errors go to stderr.
- **Raw AG-UI** (body from a file, because PowerShell 7.3+ re-quotes native arguments):
  ```powershell
  '{"threadId":"t1","runId":"r1","messages":[{"id":"m1","role":"user","content":"What are NVIDIA''s main risk factors?"}],"tools":[],"context":[],"state":{},"forwardedProps":{}}' |
    Set-Content -Encoding utf8NoBOM "$env:TEMP\agui.json"
  curl.exe -N -X POST http://localhost:5180/agents/standard-rag -H "Content-Type: application/json" -H "Accept: text/event-stream" --data-binary "@$env:TEMP\agui.json"
  ```
  Events: `RUN_STARTED`, `REASONING_START` / `REASONING_MESSAGE_*` (qwen3's thinking), `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` (`delta`) / `TEXT_MESSAGE_END`, `RUN_FINISHED` (or `RUN_ERROR`). Only `threadId`, `runId` and `messages` are required. Reuse `threadId` with a **new message id** to continue.
- **Configuration** (env vars override `appsettings.json`): `Agent__RetrievalTop` (1..20, still clamped by `Retrieval__MaxTop`), `Agent__RecentMessageMemoryLimit` (0..20), `AI__Chat__Model`; under Aspire `${env:Parameters__chat-model} = "qwen2.5:1.5b"` before `aspire start`.
- **Gotchas:**
  - **Trust model:** `withIsolation: false` is acceptable only because this is a single-user local demo. The AG-UI thread id is a resume identifier, not an auth token, and the in-memory store keys by `(agent, threadId)` only. Any multi-user deployment needs ASP.NET Core auth on `/agents/*`, isolation on (`WithInMemorySessionStore()` / `WithSessionStore(...)` default) and `AddHttpContextAccessor()` + `UseClaimsBasedAgentIsolation()` (`Microsoft.Agents.AI.Hosting.AspNetCore`). Learn also warns against exposing AG-UI directly to untrusted clients, which can inject system/assistant messages, tools or state (Phase 07 guardrails).
  - Sessions live in memory: restarting `api` forgets every thread; an old `--thread` id silently starts a fresh conversation.
  - `RUN_STARTED` (and the HTTP response) only arrives after the prompt is processed, about 10 s for chat-only and about 80 s for standard-rag on CPU. Clients must not time out on headers (`HttpClient.Timeout = Infinite`).
  - With `qwen3:4b` expect minutes per answer (reasoning). Switch `chat-model` for demos.
  - Don't add response compression for `text/event-stream`.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/agent-framework/integrations/by-component/ui/ag-ui/getting-started | Server `AddAGUIServer` + `MapAGUIServer`; client `AGUIChatClient(new AGUIChatClientOptions(http, url))`; **"AGUIChatClient is stateless"**, continuation = same `threadId` (+ `parentRunId`) in `RunAgentInput` via `RawRepresentationFactory`, send only new messages; event names; SSE keepalive | **Changed decisions**: `MapAGUIServer` (deviation 1); client uses `IChatClient` directly with `RawRepresentationFactory` instead of `AsAIAgent()` + session (deviation 2). `parentRunId` found unnecessary for MAF hosting (tested) |
| 2 | https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.hosting.agui.aspnetcore.aguiendpointroutebuilderextensions.mapagui (package v1.13) | Overloads `(endpoints, agentName, pattern)`, `(endpoints, IHostedAgentBuilder, pattern)`; remarks: keyed session store, ThreadId as conversation id, **trust model** | **Docs vs reality:** reference names it `MapAGUI`; installed 1.28 has only `MapAGUIServer` with the same overloads (XML docs: session store resolved per request, partitioned by registration name) |
| 3 | https://learn.microsoft.com/agent-framework/hosting/self-hosting/ | `AddAIAgent`, `IHostedAgentBuilder`, `WithInMemorySessionStore(withIsolation: false)` as the dev-only path; "protocol integration packages already reference Microsoft.Agents.AI.Hosting"; history in session state is persisted with the session; isolation via `UseClaimsBasedAgentIsolation` | `.WithInMemorySessionStore(withIsolation: false)` per agent; no custom history provider; **changed a decision**: no direct Hosting / Agents.AI references (deviation 3); trust-model note |
| 4 | https://learn.microsoft.com/dotnet/api/microsoft.agents.ai.hosting.inmemoryagentsessionstore | Keys by `(agent.Id, conversationId)`, no principal dimension; multi-user needs `IsolationKeyScopedAgentSessionStore` | Gotcha "Trust model" |
| 5 | https://learn.microsoft.com/agent-framework/integrations/by-component/ui/ag-ui/security-considerations | ThreadId is not proof of ownership; message/tool/state injection when clients are untrusted; trusted-frontend pattern | Single-user only; client sends only user messages; hardening is Phase 07 |
| 6 | https://learn.microsoft.com/agent-framework/agents/rag | `TextSearchProvider(SearchAdapter, options)` on `ChatClientAgentOptions.AIContextProviders`; options table (`SearchTime` default `BeforeAIInvoke`, `ContextFormatter` overrides `ContextPrompt`/`CitationsPrompt`, `RecentMessageMemoryLimit` default 0, `RecentMessageRolesIncluded` default User) | `TextSearchProvider` with our delegate and `Prompts.FormatSources`; no custom context provider (RAG guide: **information retrieval** step of the query phase, prompt grounding) |
| 7 | Package XML docs `Microsoft.Agents.AI` 1.28.0 (`TextSearchProvider`, `TextSearchProviderState`, `TextSearchProviderOptions.StateKey`) | Where the recent-message memory lives | **Per session** (state bag); `RecentMessageMemoryLimit = 4` kept |
| 8 | Package XML docs `Microsoft.Agents.AI.Hosting` / `.Hosting.AGUI.AspNetCore` 1.28.0-preview.261106.1, `AGUI.Client` / `AGUI.Abstractions` 1.0.1 | Exact signatures (`AddAIAgent` factory shape and name check, `WithInMemorySessionStore` isolation default, `AddAGUIServer` = JSON only, `RunAgentInput` required fields, `AsAGUIMessages` now needs `JsonSerializerOptions`) | API table above |
| 9 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide and https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-prompt-engineering | Where grounding fits (query phase: retrieve, then augment the prompt and generate); retrieved content can carry injected instructions | standard-rag = retrieval (Phase 03) + augmentation (`ContextFormatter`) + grounded generation (instructions: sources only, `[n]` citations, "not covered" fallback); `<source>` fencing with `<`/`>` escaped; chat-only is the ungrounded baseline. LLM evaluation of both is Phase 06 |
| 10 | *external* Ollama 0.40.16 `/api/chat` (`think` parameter, `message.thinking` field), measured directly | qwen3 reasoning shape, `/no_think` | No `/no_think`, no tag stripping (deviation 6) |

No Learn page was contradicted by behavior except the `MapAGUI` → `MapAGUIServer` rename (row 2) and the getting-started sample's parameterless `AsAGUIMessages()` (row 8).

## Suggested commit message
```
feat(agent): named agents (chat-only, standard-rag) via MAF hosting + AG-UI endpoints; AgentClient.Cli on AGUIChatClient
```
