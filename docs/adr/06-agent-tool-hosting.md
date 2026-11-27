# ADR 06: Agent tool hosting

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

The agentic RAG agent ([ADR 01](01-agent-framework-sdk.md)) searches by calling tools. It will have at least two:

- `search_filings`: search the filing chunks, optionally filtered by ticker, form type and fiscal year;
- `list_available_filings`: list which companies and filings are indexed, so the model knows what it can search.

The tools are thin. The real work is the retrieval library behind them ([ADR 04](04-vector-database.md), [ADR 05](05-ingestion-and-retrieval-hosting.md)). The question is where the tools live: **inside the agent's process as function tools, or in an MCP server the agent connects to**.

The design must meet these requirements:

- **Build once, reuse everywhere.** A filing-search tool is useful beyond this one agent: to future agents, to agents built with other frameworks and languages, and to developer tools such as VS Code's agent mode or Claude Desktop. Building the tool once should make it available to all of them, with no per-consumer code.
- **An open standard, not our own protocol.** Discovery, invocation and schemas must follow a published specification, consistent with ADR 01's rule that no boundary depends on a protocol we invent.
- **No custom tool plumbing in the agent.** The agent should receive tools through the framework's own types, and the tool logic should be written once.
- **Latency stays dominated by the model.** A tool call must not add noticeable time compared with the model's own generation time.
- **Runs locally,** with the same code in production.

## Considered options

| Option | Why it is rejected |
|---|---|
| In-process function tools only (`AIFunctionFactory.Create`) | The simplest and fastest option. But the tools exist only inside this agent's process. Every other agent, framework or developer tool would need its own copy |
| A custom REST API, with a tool wrapper per consumer | Reusable in principle, but every consumer writes and maintains its own wrapper. There is no standard way to discover the tools or their schemas |
| In-process tools for our agent, plus an MCP server for everyone else | Two paths to the same tools that can drift apart. Our own agent would never exercise the path that other consumers use |
| A2A: expose filing search as an agent | The wrong granularity. A2A connects agents, not tools, and a "search agent" would add a model call to every search |
| **An MCP server that our own agent also consumes over MCP** | **Chosen** |

## Decision

Serve the agent's tools from an **MCP server**, and have the agentic RAG agent consume them **over MCP**, exactly as any other client would:

| Part | Choice |
|---|---|
| Tool definitions | Written once as `AIFunction`s in the retrieval library, next to the search code they call, so they stay host-agnostic |
| Server | A separate ASP.NET Core process, built on the official MCP C# SDK and serving over the Streamable HTTP transport |
| What it serves | The filing tools. Later also MCP **resources** (the filing catalog, outlines, full section text) and **prompts** (reusable task templates), so clients get data and workflows as well as tools |
| Agent side | The agent connects with the SDK's `McpClient`, lists the server's tools and passes them to the agent. Each `McpClientTool` is an ordinary `AITool`, so the agent needs no custom tool type |
| Other clients | VS Code, Claude Desktop, MCP Inspector and future agents connect to the same endpoint |

MCP is chosen for these reasons:

- **Build once, reuse by any agent.** A tool built as an MCP server works with every MCP-capable agent and host, today's and future ones, in any framework or language, with no integration code per consumer.
- **It is the open standard for agent tools.** MCP is a published specification with an official C# SDK maintained in collaboration with Microsoft, and Agent Framework treats MCP tools as ordinary agent tools.
- **Our agent proves the contract.** Because the agent uses the same MCP endpoint as everyone else, every chat exercises the path other consumers rely on.

## Consequences

- **The tools are reusable from day one.** Any new agent gets filing search by pointing an MCP client at the server, and developers can try the tools from their editor.
- **The agent has no tool plumbing of its own.** It discovers tools at run time, so adding a tool to the server makes it available to the agent with no agent change.
- **Each tool call crosses the network.** A local HTTP round trip adds milliseconds, against seconds of model time per answer.
- **The agent depends on another process.** If the MCP server is down, the agent has no tools. It must tell the user it can't search right now, rather than answer from memory.
- **The tool contract is public.** Names, descriptions and parameter schemas are what every client and model sees. Changing them can break consumers, so they are versioned with care. Small models also depend heavily on clear tool descriptions.
- **The endpoint needs protecting in production.** An open MCP endpoint lets anyone search the index. The MCP specification defines an OAuth-based authorization flow for HTTP transports, and production deployments must use it or a network boundary.
- **Tool results are untrusted input.** Results contain filing text, which ends up in prompts, so they are escaped and fenced as data, never treated as instructions.
- **There is one more process to run.** It has to be started, configured and monitored alongside the other hosts, locally and in production.
- **We revisit this decision** if tool-call latency becomes significant relative to model time, or if a tool needs state shared with the agent's process that can't cross a process boundary.

## References

- [Model Context Protocol specification](https://modelcontextprotocol.io/specification/latest): tools, resources and prompts
- [MCP tools](https://modelcontextprotocol.io/specification/latest/server/tools): tool discovery, invocation and schemas
- [MCP transports](https://modelcontextprotocol.io/specification/latest/basic/transports): stdio and Streamable HTTP
- [MCP authorization](https://modelcontextprotocol.io/specification/latest/basic/authorization): the OAuth-based authorization flow for HTTP transports
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk): the official SDK, maintained in collaboration with Microsoft
- [Build an MCP server in .NET](https://learn.microsoft.com/dotnet/ai/quickstarts/build-mcp-server) and [Get started with MCP in .NET](https://learn.microsoft.com/dotnet/ai/get-started-mcp): building servers and clients with the SDK
- [Agent Framework: MCP tools](https://learn.microsoft.com/agent-framework/agents/tools/local-mcp-tools): giving an agent tools from an MCP server
- [Agent Framework: function tools](https://learn.microsoft.com/agent-framework/agents/tools/function-tools): the rejected in-process alternative
- [VS Code: MCP servers](https://code.visualstudio.com/docs/agent-customization/mcp-servers): using an MCP server from the editor
- [Agent2Agent protocol](https://a2a-protocol.org/): agent-to-agent communication, the rejected alternative
