using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace SECFilingsAgenticRag.IngestionPipeline;

public static class IndexingServiceCollectionExtensions
{
    // The host supplies BlobContainerClient ("filings") and NpgsqlDataSource, and calls AddSecFilingsAI and
    // AddSecFilingsVectorStore(EmbeddingModelConstants.DocumentEmbeddingKey) before this.
    public static IServiceCollection AddSecFilingsIndexing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IndexingOptions>()
            .Bind(configuration.GetSection(IndexingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ChunkCleaningOptions>()
            .Bind(configuration.GetSection(ChunkCleaningOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddKeyedSingleton<IngestionDocumentReader, HtmlFilingReader>("text/html");

        // cl100k only approximates the embedding model's own tokenizer, which is close enough to size chunks.
        services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForEncoding("cl100k_base"));
        services.AddSingleton<IngestionChunker<string>>(sp => new HeaderChunker(new IngestionChunkerOptions(sp.GetRequiredService<Tokenizer>())
        {
            MaxTokensPerChunk = sp.GetRequiredService<IOptions<IndexingOptions>>().Value.MaxTokensPerChunk,
        }));

        // Not registered as IngestionChunkProcessor<string>: IndexingService runs it after the registered processors.
        services.AddSingleton<ChunkCleaningProcessor>();
        services.AddSingleton<IIndexingService, IndexingService>();
        return services;
    }
}
