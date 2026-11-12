using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
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

        return builder;
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
