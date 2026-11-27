# ADR 01: Agent framework SDK

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

The project will grow one agent in three layers. Each layer builds on the one before it:

1. **Chatbot:** a plain conversational agent over a chat model, with no retrieval. This is the baseline.
2. **Standard RAG:** the same agent retrieves filing chunks before every answer and cites them.
3. **Agentic RAG:** the model decides for itself whether to search, how often, and with which filters (ticker, form type, fiscal year).

The agent framework is the foundation for all three layers, so it is the first decision. The framework must meet these requirements:

- **C# and .NET.** The framework must allow engineers to build agents on the .NET platform and write code in C#.
- **Official vendor backing.** The framework must be maintained by a vendor that commits to it long term, not by a community port.
- **One programming model for all three layers.** Moving up a layer should mean adding a capability to the same agent, not rewriting it on a different abstraction.
- **The three open agent protocols, built in:**
  - **MCP** (Model Context Protocol), which connects an agent to tools and data;
  - **A2A** (Agent-to-Agent), which lets an agent call agents built with other frameworks, and lets them call it;
  - **AG-UI** (Agent–User Interaction), which streams an agent's run to a user interface.

  Supporting all three keeps the agents interoperable at every boundary, so no boundary depends on a protocol we invent and maintain.
- **Built-in production concerns.** It must provide session persistence, middleware for guardrails, OpenTelemetry and evaluation, so the project doesn't have to write its own.
- **Provider-agnostic models.** The default models are local (Ollama), and a config switch should move chat to an OpenAI-compatible cloud model.

## Considered options

| Option | Vendor | C# / .NET | Why it is rejected |
|---|---|---|---|
| Google Agent Development Kit (ADK) | Google | No (Python, TypeScript, Go, Java, Kotlin) | No .NET SDK |
| OpenAI Agents SDK | OpenAI | No (Python, TypeScript) | No .NET SDK; built around OpenAI's APIs |
| Strands Agents | AWS | No (Python, TypeScript) | No .NET SDK |
| CrewAI | CrewAI | No (Python) | No .NET SDK |
| Pydantic AI | Pydantic | No (Python) | No .NET SDK |
| Semantic Kernel / AutoGen | Microsoft | Yes | Superseded: Microsoft Agent Framework is their direct successor, built by the same teams |
| Microsoft.Extensions.AI only, no agent framework | Microsoft | Yes | It gives us `IChatClient` and function invocation. We would have to build sessions, context providers, hosting, a UI protocol and agent evaluation ourselves |
| **Microsoft Agent Framework** | **Microsoft** | **Yes** | **Chosen** |

Most of the agent-framework ecosystem is Python- or TypeScript-first. The only way to use those frameworks here would be a separate agent service in another language behind an HTTP boundary. That service would duplicate configuration, telemetry and deployment, and it would split the codebase across two languages.

## Decision

Build all three agents on **Microsoft Agent Framework** (`Microsoft.Agents.AI`). Use the framework's own types for each layer, not custom abstractions:

| Layer | Agent | What the framework provides |
|---|---|---|
| Chatbot | `chat-only` | `ChatClientAgent` over the `IChatClient` from Microsoft.Extensions.AI |
| Standard RAG | `standard-rag` | The same agent plus the built-in `TextSearchProvider` with `BeforeAIInvoke`, which searches before every model call |
| Agentic RAG | `agentic-rag` | The same agent plus function tools, so the model chooses its own searches |

Register the agents with `AddAIAgent` and serve them with the framework's hosting packages. Take sessions, guardrail middleware, telemetry and agent evaluation from the framework as well.

Use each protocol where it serves the project:

| Protocol | Framework support | Planned use |
|---|---|---|
| AG-UI | `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` exposes an agent; `AGUIChatClient` consumes one | Serve every agent over AG-UI; the console client and a web UI connect through it |
| MCP | `McpClientTool`s are ordinary `AITool`s that are handed to the agent | Give `agentic-rag` its search tools from an MCP server, which other MCP hosts can also use |
| A2A | `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` exposes an agent with an agent card; `A2AAgent` wraps a remote A2A agent as an `AIAgent` | Not needed for the three layers. The option stays open: exposing an agent means registering it with `AddA2AServer` and mapping an endpoint, with no change to the agent |

## Consequences

- **Each layer adds to the previous one instead of replacing it.** The three agents can share one factory, one middleware pipeline and one hosting path. They differ only in instructions, context providers and tools.
- **The agents run inside the ASP.NET Core host.** DI, options validation, `IHttpClientFactory`, resilience handlers and OpenTelemetry apply to the agents with no bridging code.
- **Every boundary uses an open protocol.** Any MCP server can supply tools, any AG-UI client can talk to the agents, and A2A clients and agents can use them once an A2A endpoint is added.
- **Model providers stay swappable.** The framework sits on `IChatClient`, so changing between Ollama and an OpenAI-compatible endpoint is a configuration change.
- **Less custom code.** The project doesn't write its own agent factory, chat service, conversation store, streaming protocol or guard interfaces. Any custom type must say why no built-in fits.
- **Some packages are in preview, and some APIs are experimental.** Several hosting and integration packages ship as prereleases, and APIs can change between releases. Every phase verifies the APIs it uses against Microsoft Learn before writing code.
- **The framework sets dependency versions.** It pins its Microsoft.Extensions.AI version, so other Microsoft.Extensions.AI packages, such as the evaluation libraries, must stay on compatible versions.
- **There are fewer community examples than in the Python ecosystem.** Microsoft Learn and the framework's own samples are the main reference.

## References

- [Microsoft Agent Framework overview](https://learn.microsoft.com/agent-framework/overview/): the framework, its agent types and .NET support
- [Migrating from Semantic Kernel](https://learn.microsoft.com/agent-framework/migration-guide/from-semantic-kernel/) and [Migrating from AutoGen](https://learn.microsoft.com/agent-framework/migration-guide/from-autogen/): Agent Framework as the successor to both
- [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai): `IChatClient`, function invocation and provider independence
- [AG-UI integration](https://learn.microsoft.com/agent-framework/integrations/by-component/ui/ag-ui/): serving agents over AG-UI and consuming them with `AGUIChatClient`
- [MCP tools](https://learn.microsoft.com/agent-framework/agents/tools/hosted-mcp-tools): giving an agent tools from an MCP server
- [A2A server hosting](https://learn.microsoft.com/agent-framework/hosting/self-hosting/a2a/server): exposing an agent over A2A
- [Model Context Protocol](https://modelcontextprotocol.io/), [Agent2Agent protocol](https://a2a-protocol.org/) and [AG-UI protocol](https://docs.ag-ui.com/): the three open protocols
- [Google ADK](https://adk.dev/), [OpenAI Agents SDK](https://openai.github.io/openai-agents-python/), [Strands Agents](https://strandsagents.com/), [CrewAI](https://docs.crewai.com/) and [Pydantic AI](https://pydantic.dev/docs/ai/overview/): language support of the rejected frameworks
