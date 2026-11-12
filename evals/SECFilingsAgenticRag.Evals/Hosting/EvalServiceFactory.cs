using Npgsql;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;

namespace SECFilingsAgenticRag.Evals.Hosting;

// One container per retrieval variant, built by the same registration calls the API makes. Variants differ only in
// configuration, which is the claim the matrix proves: no strategy is selected by code.
public static class EvalServiceFactory
{
    public static ServiceProvider Create(
        IConfiguration baseConfiguration, IReadOnlyDictionary<string, string?> overrides, NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(overrides)
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(loggerFactory)
            .AddSingleton(dataSource)
            .AddSecFilingsAI(configuration)
            .AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey)
            .AddSecFilingsRetrieval(configuration)
            .BuildServiceProvider();
    }
}
