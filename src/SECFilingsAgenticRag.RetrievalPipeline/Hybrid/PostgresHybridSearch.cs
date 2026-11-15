using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Npgsql;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline.Fusion;

namespace SECFilingsAgenticRag.RetrievalPipeline.Hybrid;

// Custom (no built-in fits): the PgVector connector does implement IKeywordHybridSearchable, but it ANDs every keyword
// (plainto_tsquery), so a natural-language question matched keywords for only 4 of 25 golden questions. This one ORs the
// terms and lets ts_rank_cd rank partial matches. The vector side is still the collection's own search (so the query
// embedding gets its task prefix); the two lists are fused with RRF.
public sealed class PostgresHybridSearch(
    VectorStoreCollection<Guid, FilingChunkRecord> collection, NpgsqlDataSource dataSource, RetrievalOptions options)
    : IVectorSearchable<FilingChunkRecord>, IKeywordHybridSearchable<FilingChunkRecord>
{
    // Postgres uses the connector's IsFullTextIndexed GIN index only when the WHERE clause repeats its exact expression
    // (default language english). plainto_tsquery accepts raw user text and does the stemming and stop-word removal;
    // swapping its "&" for "|" turns "every term must match" into "rank by how many terms match".
    private const string Document = $"""to_tsvector('english', "{FilingChunkRecord.TextStorageName}")""";

    // Keys only: the caller's filter is a LINQ expression this SQL cannot express, so it is applied by the connector when the
    // ranked keys are loaded (KeywordSearchAsync). Ranking re-tokenizes every match (~0.5 s for 824 chunks, measured); a
    // larger corpus needs a stored tsvector column or a cap on ranked matches.
    private const string KeywordSql = $"""
        SELECT "{FilingChunkRecord.KeyStorageName}"
        FROM "{FilingChunkRecord.CollectionName}", CAST(replace(plainto_tsquery('english', @query)::text, '&', '|') AS tsquery) AS q
        WHERE {Document} @@ q
        ORDER BY ts_rank_cd({Document}, q) DESC
        """;

    public IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> SearchAsync<TInput>(
        TInput searchValue, int top, VectorSearchOptions<FilingChunkRecord>? searchOptions = null,
        CancellationToken cancellationToken = default) where TInput : notnull =>
        SearchAsync(searchValue, searchValue as string ?? "", top, searchOptions?.Filter, cancellationToken);

    public IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> HybridSearchAsync<TInput>(
        TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<FilingChunkRecord>? searchOptions = null,
        CancellationToken cancellationToken = default) where TInput : notnull =>
        SearchAsync(searchValue, string.Join(' ', keywords), top, searchOptions?.Filter, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : collection.GetService(serviceType, serviceKey);

    private async IAsyncEnumerable<VectorSearchResult<FilingChunkRecord>> SearchAsync<TInput>(
        TInput searchValue, string keywords, int top, Expression<Func<FilingChunkRecord, bool>>? filter,
        [EnumeratorCancellation] CancellationToken ct) where TInput : notnull
    {
        var pool = Math.Max(options.Hybrid.CandidatePool, top);
        var vectorHits = await collection.SearchAsync(searchValue, pool, new() { Filter = filter }, ct)
            .Select(hit => hit.Record).ToListAsync(ct);
        var keywordHits = await KeywordSearchAsync(keywords, pool, filter, ct);

        foreach (var hit in RrfFusion.Fuse([vectorHits, keywordHits], options.Hybrid.RrfK).Take(top))
            yield return hit;
    }

    private async Task<IReadOnlyList<FilingChunkRecord>> KeywordSearchAsync(
        string keywords, int pool, Expression<Func<FilingChunkRecord, bool>>? filter, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(KeywordSql);
        command.Parameters.AddWithValue("query", keywords);
        List<Guid> ranked = [];
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                ranked.Add(reader.GetGuid(0));

        // Best-ranked keys first, one batch at a time, until the pool is full. The connector translates the key list and the
        // caller's filter (ticker, form, year, flagged chunks) into SQL over the indexed columns, and maps the rows. Its
        // results are unordered, so each batch is put back into rank order.
        List<FilingChunkRecord> hits = [];
        foreach (var batch in ranked.Chunk(pool))
        {
            Expression<Func<FilingChunkRecord, bool>> inBatch = record => batch.Contains(record.Key);
            if (filter is not null)
                inBatch = FilingFilters.And(inBatch, filter);
            var found = await collection.GetAsync(inBatch, batch.Length, cancellationToken: ct).ToDictionaryAsync(r => r.Key, cancellationToken: ct);
            hits.AddRange(batch.Where(found.ContainsKey).Select(key => found[key]));
            if (hits.Count >= pool)
                break;
        }
        return [.. hits.Take(pool)];
    }
}
