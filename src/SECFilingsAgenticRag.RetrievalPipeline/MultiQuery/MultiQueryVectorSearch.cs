using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline.Fusion;

namespace SECFilingsAgenticRag.RetrievalPipeline.MultiQuery;

// Custom (no built-in fits): MEVD has no query-rewriting decorator; implements the built-in IVectorSearchable.
// It targets the vocabulary gap measured in Phase 05: users say "revenue drivers", Apple's 10-K says "net sales".
public sealed class MultiQueryVectorSearch(
    IVectorSearchable<FilingChunkRecord> inner, IChatClient chat, RetrievalOptions options, ILogger<MultiQueryVectorSearch> logger)
    : IVectorSearchable<FilingChunkRecord>
{
    public sealed record QueryVariants(string[] Queries);

    private const string Instructions =
        "Rewrite the user's question into alternative search queries for a database of SEC 10-K filings. " +
        "Keep every company name, ticker and year. Use the wording filings use and expand financial jargon " +
        "(e.g. \"capex\" -> \"capital expenditures\", \"revenue\" -> \"net sales\"). Return short search-style queries, not answers.";

    private static readonly ChatOptions ChatOptions = new() { Temperature = 0 };

    public async IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> SearchAsync<TInput>(
        TInput searchValue, int top, VectorSearchOptions<FilingChunkRecord>? searchOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TInput : notnull
    {
        if (searchValue is not string question)
        {
            await foreach (var hit in inner.SearchAsync(searchValue, top, searchOptions, cancellationToken))
                yield return hit;
            yield break;
        }

        using var activity = FilingSearch.ActivitySource.StartActivity("retrieval.multi_query");
        var variants = await StructuredOutput.TryGetAsync<QueryVariants>(chat,
            [new(ChatRole.System, Instructions), new(ChatRole.User, $"Write {options.MultiQuery.Variants} queries for: {question}")],
            ChatOptions, logger, cancellationToken);
        var queries = (variants?.Queries ?? []).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim())
            .Prepend(question).Distinct(StringComparer.OrdinalIgnoreCase).Take(options.MultiQuery.Variants + 1);

        // Sequential on purpose: every variant needs an embedding call, and a local Ollama serves one request at a time anyway.
        List<IReadOnlyList<FilingChunkRecord>> rankings = [];
        foreach (var query in queries)
            rankings.Add(await inner.SearchAsync(query, top, searchOptions, cancellationToken).Select(hit => hit.Record).ToListAsync(cancellationToken));

        activity?.SetTag("secfilings.query_count", rankings.Count).SetTag("secfilings.candidate_count", rankings.Sum(r => r.Count));
        foreach (var hit in RrfFusion.Fuse(rankings, options.Hybrid.RrfK).Take(top))
            yield return hit;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
}
