using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.Telemetry;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline;

// The read-side mirror of MEDI's IngestionPipeline, after the proposed Microsoft.Extensions.DataRetrieval RetrievalPipeline
// (dotnet/extensions#7507): query → search → results, traced as one span. The proposal's types are unpublished and cannot
// carry a VectorSearchOptions filter, so the stages run on the built-in IVectorSearchable, and the query and result processors
// it describes (multi-query, rerank) are decorators behind it. Every consumer calls this one method.
public static class FilingSearch
{
    public const int MaxQueryLength = 2000;

    internal static readonly ActivitySource ActivitySource = new(TelemetryNames.Retrieval);
    private static readonly Histogram<double> Duration =
        new Meter(TelemetryNames.Retrieval).CreateHistogram<double>("secfilings.retrieval.duration", "ms");

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
        var strategy = options.Describe();
        var started = Stopwatch.GetTimestamp();
        // No query text: it is user data (Telemetry:EnableSensitiveData governs that for the model spans).
        using var activity = ActivitySource.StartActivity("retrieval.search");
        activity?.SetTag("secfilings.strategy", strategy).SetTag("secfilings.top", topK)
            .SetTag("secfilings.ticker", tickers is null ? null : string.Join(',', tickers));

        // A string search value is embedded by the store's keyed query generator, which adds the query task prefix.
        var searchOptions = new VectorSearchOptions<FilingChunkRecord>
        {
            Filter = FilingFilters.Build(tickers, formTypes, fiscalYears, options.ExcludeInstructionLikeChunks),
        };
        List<VectorSearchResult<FilingChunkRecord>> hits;
        try
        {
            // Decorators and other strategies may not preserve order; every strategy scores higher-is-better.
            hits = [.. (await search.SearchAsync(query, topK, searchOptions, ct).ToListAsync(ct)).OrderByDescending(hit => hit.Score)];
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message).AddException(ex);
            throw;
        }

        activity?.SetTag("secfilings.result_count", hits.Count).SetTag("secfilings.top_score", hits.FirstOrDefault()?.Score);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("strategy", strategy));
        return hits;
    }
}
