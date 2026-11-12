using System.Diagnostics;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline;

// The read-side mirror of MEDI's IngestionPipeline, after the proposed Microsoft.Extensions.DataRetrieval RetrievalPipeline
// (dotnet/extensions#7507): query → search → results, traced as one span. The proposal's types are unpublished and cannot
// carry a VectorSearchOptions filter, so the stages run on the built-in IVectorSearchable, and the query and result processors
// it describes (multi-query, rerank) arrive in Phase 6 as decorators behind it. Every consumer calls this one method.
public static class FilingSearch
{
    public const string ActivitySourceName = "SECFilingsAgenticRag.RetrievalPipeline";
    public const int MaxQueryLength = 2000;

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // The query is untrusted (user-typed or LLM-generated), so it is validated here rather than by each caller.
    public static async Task<IReadOnlyList<VectorSearchResult<FilingChunkRecord>>> SearchFilingsAsync(
        this IVectorSearchable<FilingChunkRecord> search, RetrievalOptions options, string query,
        IReadOnlyCollection<string>? tickers = null, IReadOnlyCollection<string>? formTypes = null,
        IReadOnlyCollection<int>? fiscalYears = null, int? top = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        query = query.Trim();
        if (query.Length > MaxQueryLength)
            throw new ArgumentException($"The query exceeds {MaxQueryLength} characters.", nameof(query));

        var topK = options.ClampTop(top);
        using var activity = ActivitySource.StartActivity("RetrievalPipeline.Process");
        activity?.SetTag("rag.strategy", options.Strategy).SetTag("rag.topK", topK);

        // A string search value is embedded by the store's keyed query generator, which adds the query task prefix.
        var searchOptions = new VectorSearchOptions<FilingChunkRecord>
        {
            Filter = FilingFilters.Build(tickers, formTypes, fiscalYears, options.ExcludeInstructionLikeChunks),
        };
        var hits = await search.SearchAsync(query, topK, searchOptions, ct).ToListAsync(ct);

        activity?.SetTag("rag.results.count", hits.Count);
        // Decorators and other strategies may not preserve order; every strategy scores higher-is-better.
        return [.. hits.OrderByDescending(hit => hit.Score)];
    }
}
