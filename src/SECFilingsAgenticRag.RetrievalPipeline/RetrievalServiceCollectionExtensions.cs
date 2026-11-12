using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline;

public static class RetrievalServiceCollectionExtensions
{
    // The host calls AddSecFilingsAI and AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey) before this.
    // Each strategy is a keyed IVectorSearchable, so the container itself is the list of valid strategies.
    public static IServiceCollection AddSecFilingsRetrieval(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetrievalOptions>()
            .Bind(configuration.GetSection(RetrievalOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IServiceProviderIsKeyedService>(
                (options, container) => container.IsKeyedService(typeof(IVectorSearchable<FilingChunkRecord>), options.Strategy),
                $"{RetrievalOptions.SectionName}:Strategy must be one of: {RetrievalOptions.VectorStrategy}.")
            .ValidateOnStart();

        services.AddKeyedSingleton<IVectorSearchable<FilingChunkRecord>>(RetrievalOptions.VectorStrategy,
            (sp, _) => sp.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>());
        services.AddSingleton(sp => sp.GetRequiredKeyedService<IVectorSearchable<FilingChunkRecord>>(
            sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.Strategy));

        return services;
    }
}
