using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using OllamaSharp;
using SECFilingsAgenticRag.Common.Telemetry;

namespace SECFilingsAgenticRag.Common.AI;

public static class AIServiceCollectionExtensions
{
    private const int OllamaRetries = 2;

    // The chat client has no UseFunctionInvocation: agents add their own with an iteration cap.
    public static IServiceCollection AddSecFilingsAI(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSecFilingsAI(configuration, embeddingGenerator: null);

    // Test seam: swaps the Ollama generator while keeping the real prefix and dimension checks.
    internal static IServiceCollection AddSecFilingsAI(
        this IServiceCollection services, IConfiguration configuration,
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? embeddingGenerator)
    {
        services.AddOptions<ChatModelOptions>().Bind(configuration.GetSection(ChatModelOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmbeddingModelOptions>().Bind(configuration.GetSection(EmbeddingModelOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<TelemetryOptions>().Bind(configuration.GetSection(TelemetryOptions.SectionName)).ValidateOnStart();
        AddOllamaHttpClient(services, configuration);

        // cl100k only approximates the models' own tokenizers, which is close enough to size chunks and enforce input limits.
        services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForEncoding("cl100k_base"));
        services.AddSingleton(sp => ChatClientFactory.Create(sp, sp.GetRequiredService<IOptions<ChatModelOptions>>().Value.Model!));

        embeddingGenerator ??= sp => new OllamaApiClient(
            ChatClientFactory.OllamaHttpClient(sp), sp.GetRequiredService<IOptions<EmbeddingModelOptions>>().Value.Model!);
        var sensitiveData = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()?.EnableSensitiveData ?? false;
        AddEmbeddingGenerator(services, EmbeddingModelConstants.DocumentEmbeddingKey, embeddingGenerator, EmbeddingModelConstants.DocumentPrefix, sensitiveData);
        AddEmbeddingGenerator(services, EmbeddingModelConstants.QueryEmbeddingKey, embeddingGenerator, EmbeddingModelConstants.QueryPrefix, sensitiveData);

        return services;
    }

    // ServiceDefaults gives every factory client a 10 s attempt / 30 s total timeout with 3 retries, which cancels a CPU
    // completion and then repeats it. The Ollama client gets its own budget instead: (retries + 1) x attempt fits in the total,
    // the circuit breaker samples at least two attempts (its validation rule), and only connection failures and gateway errors
    // are retried. A timeout or a 500 (often out of memory or a failed model load) would only repeat the expensive work.
    private static void AddOllamaHttpClient(IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddHttpClient(ChatClientFactory.OllamaHttpClientName, client =>
        {
            client.BaseAddress = new Uri(configuration.GetConnectionString("ollama")
                ?? throw new InvalidOperationException("ConnectionStrings:ollama is not configured, e.g. \"http://localhost:11434\"."));
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
#pragma warning disable EXTEXP0001 // experimental; Learn's documented way to replace the ServiceDefaults handler for one client
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        var pipeline = builder.AddStandardResilienceHandler();
        services.AddOptions<HttpStandardResilienceOptions>(pipeline.PipelineName).Configure<IOptions<ChatModelOptions>>((resilience, chat) =>
        {
            var attempt = TimeSpan.FromSeconds(chat.Value.TimeoutSeconds);
            resilience.Retry.MaxRetryAttempts = OllamaRetries;
            resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is HttpRequestException ||
                args.Outcome.Result?.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
            resilience.AttemptTimeout.Timeout = attempt;
            resilience.TotalRequestTimeout.Timeout = attempt * (OllamaRetries + 1) + TimeSpan.FromSeconds(30);
            resilience.CircuitBreaker.SamplingDuration = attempt * 2;
        });
    }

    private static void AddEmbeddingGenerator(
        IServiceCollection services, string key,
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> create, string prefix, bool sensitiveData) =>
        services.AddKeyedEmbeddingGenerator(key, create)
            .Use(async (values, options, inner, cancellationToken) =>
            {
                var embeddings = await inner.GenerateAsync(values.Select(v => prefix + v), options, cancellationToken);
                if (embeddings.FirstOrDefault(e => e.Vector.Length != EmbeddingModelConstants.Dimensions) is { } wrong)
                    throw new InvalidOperationException(
                        $"The embedding model returned {wrong.Vector.Length} dimensions but the vector column holds {EmbeddingModelConstants.Dimensions}.");
                return embeddings;
            })
            .UseLogging()
            .UseOpenTelemetry(sourceName: TelemetryNames.AI, configure: otel => otel.EnableSensitiveData = sensitiveData);
}
