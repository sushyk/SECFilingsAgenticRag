using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.AgentServer.Tools;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;

namespace SECFilingsAgenticRag.AgentServer;

public static class AgentHostingExtensions
{
    // Requires AddSecFilingsAI and AddSecFilingsRetrieval. Each session store keys by agent name and AG-UI thread id; isolation
    // is off because this is a single-user local demo. A shared deployment must keep it on (claims-based isolation), since a
    // thread id is a resume identifier, not proof of ownership.
    public static IHostApplicationBuilder AddSecFilingsAgents(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<AgentOptions>()
            .BindConfiguration(AgentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.AddAIAgent(AgentNames.ChatOnly, (sp, name) => CreateAgent(sp, name, Prompts.ChatOnly))
            .WithInMemorySessionStore(withIsolation: false);
        builder.AddAIAgent(AgentNames.StandardRag, (sp, name) => CreateAgent(sp, name, Prompts.StandardRag, CreateFilingSearch(sp)))
            .WithInMemorySessionStore(withIsolation: false);

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<FilingTools>();
        builder.AddAIAgent(AgentNames.AgenticRag, CreateAgenticRag)
            .WithInMemorySessionStore(withIsolation: false);

        return builder;
    }

    // Agentic RAG: the model decides whether, what and how often to search. UseProvidedChatClientAsIs stops ChatClientAgent from
    // adding its own default FunctionInvokingChatClient, so ours, which carries the iteration cap, is the only tool loop.
    private static AIAgent CreateAgenticRag(IServiceProvider sp, string name)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var maxToolIterations = sp.GetRequiredService<IOptions<AgentOptions>>().Value.MaxToolIterations;

        return sp.GetRequiredService<IChatClient>().AsBuilder()
            .UseFunctionInvocation(loggerFactory, invoker =>
            {
                invoker.MaximumIterationsPerRequest = maxToolIterations;
                // Below the default of 3: on CPU every failed round trip costs tens of seconds.
                invoker.MaximumConsecutiveErrorsPerRequest = 2;
                // Small models send optional arguments as the string "null" (measured with llama3.2), which fails binding. The generic
                // "Function failed." made the model answer from memory; this message makes it retry. Other errors stay generic.
                invoker.FunctionInvoker = async (context, ct) =>
                {
                    try
                    {
                        return await context.Function.InvokeAsync(context.Arguments, ct);
                    }
                    catch (JsonException)
                    {
                        return "Error: the arguments do not match the tool schema. Send numbers as JSON numbers and omit optional " +
                               "parameters you have no value for; never send the string \"null\".";
                    }
                };
            })
            .Build(sp)
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = name,
                UseProvidedChatClientAsIs = true,
                ChatOptions = new() { Instructions = Prompts.AgenticRag, Tools = sp.GetRequiredService<FilingTools>().AsTools() },
            }, loggerFactory, sp);
    }

    private static AIAgent CreateAgent(IServiceProvider sp, string name, string instructions, params AIContextProvider[] contextProviders) =>
        sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new() { Instructions = instructions },
            AIContextProviders = contextProviders,
        }, sp.GetRequiredService<ILoggerFactory>(), sp);

    // Standard RAG: the code, not the model, searches before every model call. The delegate is the one Phase 3 search
    // function, so the agent inherits its query validation, flagged-chunk exclusion and whatever strategy is composed behind it.
    private static TextSearchProvider CreateFilingSearch(IServiceProvider sp)
    {
        var search = sp.GetRequiredService<IVectorSearchable<FilingChunkRecord>>();
        var retrieval = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        var agent = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

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
                ContextFormatter = Prompts.FormatSources,
            },
            sp.GetRequiredService<ILoggerFactory>());
    }
}
