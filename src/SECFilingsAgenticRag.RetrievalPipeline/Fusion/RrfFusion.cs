using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline.Fusion;

// Reciprocal Rank Fusion: combines ranked lists whose scores are not comparable (cosine vs ts_rank_cd, or one list per
// query variant). The fused score is higher-is-better like every strategy's, but tiny (1/61 ≈ 0.016 for one rank-1 hit).
public static class RrfFusion
{
    public const int DefaultK = 60;

    // Each list is ordered best-first. Ties go to the better single rank, then to first-seen order, so results are deterministic.
    public static IReadOnlyList<VectorSearchResult<FilingChunkRecord>> Fuse(
        IEnumerable<IReadOnlyList<FilingChunkRecord>> rankings, int k = DefaultK)
    {
        Dictionary<Guid, (FilingChunkRecord Record, double Score, int BestRank, int FirstSeen)> fused = [];
        foreach (var ranking in rankings)
            for (var rank = 1; rank <= ranking.Count; rank++)
            {
                var record = ranking[rank - 1];
                var score = 1.0 / (k + rank);
                fused[record.Key] = fused.TryGetValue(record.Key, out var seen)
                    ? (seen.Record, seen.Score + score, Math.Min(seen.BestRank, rank), seen.FirstSeen)
                    : (record, score, rank, fused.Count);
            }

        return [.. fused.Values
            .OrderByDescending(f => f.Score).ThenBy(f => f.BestRank).ThenBy(f => f.FirstSeen)
            .Select(f => new VectorSearchResult<FilingChunkRecord>(f.Record, f.Score))];
    }
}
