using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SECFilingsAgenticRag.AgentServer.Sessions;
using SECFilingsAgenticRag.AgentServer.Tools;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.Telemetry;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;

namespace SECFilingsAgenticRag.AgentServer;

public static class AgentHostingExtensions
{
    // Requires AddSecFilingsAI and AddSecFilingsRetrieval (and an NpgsqlDataSource for the Postgres store), and Mcp:Endpoint for
    // agentic-rag's tools. Sessions are keyed by
    // agent and AG-UI thread id; isolation is off because this is a single-user local demo. A shared deployment must keep it on
    // (authentication plus claims-based isolation), since a thread id is a resume identifier, not proof of ownership.
    public static IHostApplicationBuilder AddSecFilingsAgents(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<AgentOptions>()
            .BindConfiguration(AgentOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(agent => agent.CompactAboveTokens <= agent.HardTokenBudget,
                $"{AgentOptions.SectionName}:CompactAboveTokens must not exceed HardTokenBudget: gentle compaction has to run first.")
            .Validate<IOptions<ChatModelOptions>>((agent, chat) => chat.Value.ContextLength is not { } window || agent.HardTokenBudget < window,
                $"{AgentOptions.SectionName}:HardTokenBudget must be below AI:Chat:ContextLength, or the model has no room to answer.")
            .ValidateOnStart();
        builder.Services.AddOptions<GuardrailsOptions>().BindConfiguration(GuardrailsOptions.SectionName).ValidateOnStart();
        builder.Services.AddOptions<McpEndpointOptions>().BindConfiguration(McpEndpointOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        // ServiceDefaults' standard handler (10 s attempt, 3 retries) would cut a reranked search on CPU and then repeat it. The
        // model's tool loop is the retry, so a tool call gets exactly one attempt, bounded by Mcp:TimeoutSeconds.
        var mcp = builder.Services.AddHttpClient(McpToolsProvider.HttpClientName, (sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<McpEndpointOptions>>().Value.TimeoutSeconds));
#pragma warning disable EXTEXP0001 // experimental; Learn's documented way to replace the ServiceDefaults handler for one client
        mcp.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<McpToolsProvider>();
        builder.Services.AddSingleton<PostgresAgentSessionStore>();

        // Registration needs the choice before the container exists; the bound options validate the same value on start.
        var store = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>()?.SessionStore ?? SessionStoreKind.InMemory;
        void Host(string name, Func<IServiceProvider, string, AIAgent> create)
        {
            var hosted = builder.AddAIAgent(name, create);
            if (store == SessionStoreKind.Postgres)
                hosted.WithSessionStore((sp, _) => sp.GetRequiredService<PostgresAgentSessionStore>(), withIsolation: false);
            else
                hosted.WithInMemorySessionStore(withIsolation: false);
        }

        Host(AgentNames.ChatOnly, (sp, name) => CreateAgent(sp, name, Chat(sp), new() { Instructions = Prompts.ChatOnly }));
        Host(AgentNames.StandardRag, (sp, name) =>
            CreateAgent(sp, name, Chat(sp), new() { Instructions = Prompts.StandardRag }, CreateFilingSearch(sp)));
        // The tools come from the mcp resource, so every LLM tool call is an MCP tools/call.
        Host(AgentNames.AgenticRag, (sp, name) => CreateAgent(sp, name, WithToolLoop(sp, Chat(sp)),
            new() { Instructions = Prompts.AgenticRag }, sp.GetRequiredService<McpToolsProvider>()));

        return builder;
    }

    // UseProvidedChatClientAsIs stops ChatClientAgent from adding its own default decorators, so the pipeline built here is the
    // whole one: agentic-rag's function invocation (with the iteration cap) is the only tool loop, and compaction is the innermost
    // stage. On the chat client it runs before every model call, tool rounds included, and compacts only the request: the stored
    // history stays complete (Learn: registering it on the agent options would skip the tool loop and could persist summaries).
    // Telemetry wraps the guardrails, so a refused run still gets its invoke_agent span with secfilings.refused.
    private static AIAgent CreateAgent(
        IServiceProvider sp, string name, ChatClientBuilder chat, ChatOptions chatOptions, params AIContextProvider[] contextProviders)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var telemetry = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        var guardrails = new Guardrails(name, citesSources: name != AgentNames.ChatOnly, sp);

        return chat
            .UseAIContextProviders(Compaction(sp.GetRequiredService<IOptions<AgentOptions>>().Value, loggerFactory))
            .Build(sp)
            .AsAIAgent(new ChatClientAgentOptions
            {
                // A stable id: the session store keys rows by agent id, and the default id is random per process.
                Id = name,
                Name = name,
                UseProvidedChatClientAsIs = true,
                ChatOptions = chatOptions,
                AIContextProviders = contextProviders,
            }, loggerFactory, sp)
            .AsBuilder()
            .UseOpenTelemetry(TelemetryNames.Agent, agent => agent.EnableSensitiveData = telemetry.EnableSensitiveData)
            .Use(guardrails.RunAsync, guardrails.RunStreamingAsync)
            .Build(sp);
    }

    private static ChatClientBuilder Chat(IServiceProvider sp) => sp.GetRequiredService<IChatClient>().AsBuilder();

    // Agentic RAG: the model decides whether, what and how often to search.
    private static ChatClientBuilder WithToolLoop(IServiceProvider sp, ChatClientBuilder chat) =>
        chat.UseFunctionInvocation(sp.GetRequiredService<ILoggerFactory>(), invoker =>
        {
            invoker.MaximumIterationsPerRequest = sp.GetRequiredService<IOptions<AgentOptions>>().Value.MaxToolIterations;
            // Below the default of 3: on CPU every failed round trip costs tens of seconds.
            invoker.MaximumConsecutiveErrorsPerRequest = 2;
            // McpClientTool returns a TextContent, or the whole CallToolResult as JSON when the server reports an error; both would
            // reach the model and the AG-UI client JSON-serialized (measured: {"text":"Found 5 excerpts…"}), so the tool's own
            // text is passed on. Argument errors are such results; an exception here means mcp is unreachable. Returned as a
            // result it is not counted as a consecutive error, so the run ends with the model saying search is unavailable.
            invoker.FunctionInvoker = async (context, ct) =>
            {
                try
                {
                    return await context.Function.InvokeAsync(context.Arguments, ct) switch
                    {
                        TextContent text => text.Text,
                        JsonElement error => string.Join("\n", error.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!
                            .Content.OfType<TextContentBlock>().Select(block => block.Text)),
                        var other => other,
                    };
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return McpToolsProvider.Unavailable;
                }
            };
        });

    // Gentlest first: collapse old tool output, then drop old turns, then cut to the hard budget as a backstop. Token counts are
    // the framework's own estimates.
#pragma warning disable MAAI001 // experimental compaction API (Learn: concepts/agents/conversations/compaction)
    private static CompactionProvider Compaction(AgentOptions agent, ILoggerFactory loggerFactory) => new(
        new PipelineCompactionStrategy([
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(agent.CompactAboveTokens)),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(agent.MaxHistoryTurns)),
            // Four groups keep the current question, its sources and the latest tool rounds even when over budget.
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(agent.HardTokenBudget), minimumPreservedGroups: 4),
        ]),
        loggerFactory: loggerFactory);
#pragma warning restore MAAI001

    // Standard RAG: the code, not the model, searches before every model call. The delegate is the one Phase 3 search
    // function, so the agent inherits its query validation, flagged-chunk exclusion and whatever strategy is composed behind it.
    private static TextSearchProvider CreateFilingSearch(IServiceProvider sp)
    {
        var search = sp.GetRequiredService<IVectorSearchable<FilingChunkRecord>>();
        var retrieval = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        var agent = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var tokenizer = sp.GetRequiredService<Tokenizer>();

        return new TextSearchProvider(
            async (query, ct) => (await search.SearchFilingsAsync(retrieval, query, top: agent.RetrievalTop, ct: ct))
                .Select(hit => new TextSearchProvider.TextSearchResult
                {
                    SourceName = FilingCitation.Title(hit.Record),
                    SourceLink = hit.Record.SourceUrl,
                    Text = hit.Record.Text,
                }),
            new TextSearchProviderOptions
            {
                SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
                RecentMessageMemoryLimit = agent.RecentMessageMemoryLimit,
                ContextFormatter = results => Prompts.FormatSources(results, tokenizer, agent.MaxExcerptTokens),
            },
            sp.GetRequiredService<ILoggerFactory>());
    }
}
