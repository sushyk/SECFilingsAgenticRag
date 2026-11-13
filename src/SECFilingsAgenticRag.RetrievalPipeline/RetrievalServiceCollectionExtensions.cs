using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline.Hybrid;
using SECFilingsAgenticRag.RetrievalPipeline.MultiQuery;
using SECFilingsAgenticRag.RetrievalPipeline.Rerank;

namespace SECFilingsAgenticRag.RetrievalPipeline;

public static class RetrievalServiceCollectionExtensions
{
    private static readonly string[] Strategies = [RetrievalOptions.VectorStrategy, RetrievalOptions.HybridStrategy];

    // The host calls AddSecFilingsAI and AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey) and registers an
    // NpgsqlDataSource before this. Each strategy is a keyed IVectorSearchable and the decorators wrap the chosen one, so every
    // combination is reachable by configuration alone (the eval matrix relies on it).
    public static IServiceCollection AddSecFilingsRetrieval(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetrievalOptions>()
            .Bind(configuration.GetSection(RetrievalOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Rerank.CandidatePool >= options.DefaultTop,
                $"{RetrievalOptions.SectionName}:Rerank:CandidatePool must be at least DefaultTop: the reranker can only reorder what it was given.")
            .Validate<IServiceProviderIsKeyedService>(
                (options, container) => container.IsKeyedService(typeof(IVectorSearchable<FilingChunkRecord>), options.Strategy),
                $"{RetrievalOptions.SectionName}:Strategy must be one of: {string.Join(", ", Strategies)}.")
            .ValidateOnStart();

        services.AddKeyedSingleton<IVectorSearchable<FilingChunkRecord>>(RetrievalOptions.VectorStrategy,
            (sp, _) => sp.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>());
        services.AddKeyedSingleton<IVectorSearchable<FilingChunkRecord>>(RetrievalOptions.HybridStrategy,
            (sp, _) => ActivatorUtilities.CreateInstance<PostgresHybridSearch>(sp, Options(sp)));

        services.AddSingleton(sp =>
        {
            var options = Options(sp);
            var search = sp.GetRequiredKeyedService<IVectorSearchable<FilingChunkRecord>>(options.Strategy);
            if (options.MultiQuery.Enabled)
                search = new MultiQueryVectorSearch(search, ChatClientFactory.ForModel(sp, options.MultiQuery.Model), options,
                    sp.GetRequiredService<ILogger<MultiQueryVectorSearch>>());
            if (options.Rerank.Enabled)
                search = new RerankingVectorSearch(search, ChatClientFactory.ForModel(sp, options.Rerank.Model), options,
                    sp.GetRequiredService<ILogger<RerankingVectorSearch>>());
            return search;
        });

        return services;
    }

    private static RetrievalOptions Options(IServiceProvider sp) => sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;

}
