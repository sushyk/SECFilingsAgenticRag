using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace SECFilingsAgenticRag.Common.AI;

public static class AIServiceCollectionExtensions
{
    // The chat client has no UseFunctionInvocation: agents add their own with an iteration cap.
    public static IServiceCollection AddSecFilingsAI(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSecFilingsAI(configuration, embeddingGenerator: null);

    // Test seam: swaps the Ollama generator while keeping the real prefix and dimension checks.
    internal static IServiceCollection AddSecFilingsAI(
        this IServiceCollection services, IConfiguration configuration,
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? embeddingGenerator)
    {
        var chatModel = configuration["AI:Chat:Model"]
            ?? throw new InvalidOperationException("AI:Chat:Model is not configured.");

        services.AddSingleton(sp => ChatClientFactory.Create(sp, configuration, chatModel));

        embeddingGenerator ??= _ => new OllamaApiClient(ChatClientFactory.NewOllamaHttpClient(configuration), EmbeddingModelConstants.Name);
        AddEmbeddingGenerator(services, EmbeddingModelConstants.DocumentEmbeddingKey, embeddingGenerator, EmbeddingModelConstants.DocumentPrefix);
        AddEmbeddingGenerator(services, EmbeddingModelConstants.QueryEmbeddingKey, embeddingGenerator, EmbeddingModelConstants.QueryPrefix);

        return services;
    }

    private static void AddEmbeddingGenerator(
        IServiceCollection services, string key,
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> create, string prefix) =>
        services.AddKeyedEmbeddingGenerator(key, create)
            .Use(async (values, options, inner, cancellationToken) =>
            {
                var embeddings = await inner.GenerateAsync(values.Select(v => prefix + v), options, cancellationToken);
                if (embeddings.FirstOrDefault(e => e.Vector.Length != EmbeddingModelConstants.Dimensions) is { } wrong)
                    throw new InvalidOperationException(
                        $"{EmbeddingModelConstants.Name} returned {wrong.Vector.Length} dimensions but the vector column holds {EmbeddingModelConstants.Dimensions}.");
                return embeddings;
            })
            .UseLogging()
            .UseOpenTelemetry();
}
