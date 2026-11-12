using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.Evals.Answers;

// Custom, evals only (no built-in fits): nothing built in captures what was retrieved. Groundedness must be judged against
// what the agent actually saw, whether a context provider (standard-rag) or a tool (agentic-rag) searched.
public sealed class RecordingVectorSearch(IVectorSearchable<FilingChunkRecord> inner) : IVectorSearchable<FilingChunkRecord>
{
    private readonly ConcurrentQueue<FilingChunkRecord> retrieved = new();

    // Draining, not reading: questions run one at a time, so a drain before and after each run scopes the recording to it.
    public IReadOnlyList<FilingChunkRecord> TakeRetrieved()
    {
        List<FilingChunkRecord> records = [];
        while (retrieved.TryDequeue(out var record))
            records.Add(record);
        return [.. records.DistinctBy(record => record.Key)];
    }

    public async IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> SearchAsync<TInput>(
        TInput searchValue, int top, VectorSearchOptions<FilingChunkRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TInput : notnull
    {
        await foreach (var hit in inner.SearchAsync(searchValue, top, options, cancellationToken))
        {
            retrieved.Enqueue(hit.Record);
            yield return hit;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
}
