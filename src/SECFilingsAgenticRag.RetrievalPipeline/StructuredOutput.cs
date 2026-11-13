using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SECFilingsAgenticRag.RetrievalPipeline;

// Shared by the LLM decorators and the topic guard: neither may fail because a model misbehaved, so every model failure becomes null.
public static class StructuredOutput
{
    // Learn: a JSON schema response format "might cause an error if the model does not support it"; without it, MEAI puts
    // the schema into the prompt instead. So the schema is tried first and the prompt-only mode once as a fallback.
    private static readonly bool[] Modes = [true, false];

    public static async Task<T?> TryGetAsync<T>(
        IChatClient chat, IList<ChatMessage> messages, ChatOptions options, ILogger logger, CancellationToken ct) where T : class
    {
        foreach (var useJsonSchema in Modes)
        {
            try
            {
                var response = await chat.GetResponseAsync<T>(messages, options, useJsonSchema, ct);
                if (response.TryGetResult(out var result))
                {
                    logger.LogDebug("{Type} parsed (JSON schema: {UseJsonSchema})", typeof(T).Name, useJsonSchema);
                    return result;
                }
                logger.LogDebug("Unusable {Type} (JSON schema: {UseJsonSchema}): {Text}", typeof(T).Name, useJsonSchema, response.Text);
            }
            // Only the caller's cancellation propagates; an HttpClient timeout surfaces as a cancellation too, but it is a model failure.
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(e, "{Type} request failed (JSON schema: {UseJsonSchema})", typeof(T).Name, useJsonSchema);
            }
        }

        logger.LogWarning("The model returned no usable {Type}", typeof(T).Name);
        return null;
    }
}
