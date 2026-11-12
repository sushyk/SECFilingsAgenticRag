using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Npgsql;

namespace SECFilingsAgenticRag.Common.VectorStore;

public static class VectorStoreServiceCollectionExtensions
{
    // Registered as the abstract VectorStore so MEDI's VectorStoreWriter can use it without referencing the connector.
    // Requires an NpgsqlDataSource built with UseVector(). embeddingKey picks the generator the store embeds with:
    // Document on the indexing host, Query on the Api, so one table is written and searched with the right prefixes.
    public static IServiceCollection AddSecFilingsVectorStore(this IServiceCollection services, string embeddingKey)
    {
        services.AddSingleton<Microsoft.Extensions.VectorData.VectorStore>(sp =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
            EnableVectorExtension(dataSource);
            return new PostgresVectorStore(
                dataSource,
                ownsDataSource: false,
                new PostgresVectorStoreOptions
                {
                    EmbeddingGenerator = sp.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(embeddingKey),
                });
        });

        services.AddSingleton<VectorStoreCollection<Guid, FilingChunkRecord>>(sp =>
            sp.GetRequiredService<Microsoft.Extensions.VectorData.VectorStore>().GetCollection<Guid, FilingChunkRecord>(FilingChunkRecord.CollectionName));

        return services;
    }

    // Neither MEVD nor Aspire runs this inside the database (Aspire's creation script runs at server level).
    // Synchronous because a DI factory cannot await; it runs once, when the store is first resolved.
    private static void EnableVectorExtension(NpgsqlDataSource dataSource)
    {
        using var connection = dataSource.OpenConnection();
        using (var command = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", connection))
            command.ExecuteNonQuery();
        connection.ReloadTypes();
    }
}
