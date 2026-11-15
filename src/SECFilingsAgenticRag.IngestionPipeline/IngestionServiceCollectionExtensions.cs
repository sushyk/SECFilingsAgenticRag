using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace SECFilingsAgenticRag.IngestionPipeline;

public static class IngestionServiceCollectionExtensions
{
    // The host supplies BlobContainerClient ("filings") and NpgsqlDataSource, and calls AddSecFilingsAI and
    // AddSecFilingsVectorStore(EmbeddingModelConstants.DocumentEmbeddingKey) before this.
    public static IServiceCollection AddSecFilingsIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ChunkCleaningOptions>()
            .Bind(configuration.GetSection(ChunkCleaningOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddKeyedSingleton<IngestionDocumentReader, HtmlFilingReader>("text/html");

        services.AddSingleton<IngestionChunker<string>>(sp => new HeaderChunker(new IngestionChunkerOptions(sp.GetRequiredService<Tokenizer>())
        {
            MaxTokensPerChunk = sp.GetRequiredService<IOptions<IngestionOptions>>().Value.MaxTokensPerChunk,
        }));

        // Not registered as IngestionChunkProcessor<string>: IngestionService runs it after the registered processors.
        services.AddSingleton<ChunkCleaningProcessor>();
        services.AddSingleton<IIngestionService, IngestionService>();
        return services;
    }
}
