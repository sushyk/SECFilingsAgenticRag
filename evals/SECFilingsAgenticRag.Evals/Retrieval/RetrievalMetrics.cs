using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.Evals.Dataset;

namespace SECFilingsAgenticRag.Evals.Retrieval;

// Custom (no built-in fits): neither MEAI.Evaluation nor MAF evaluation ships ranked-retrieval metrics.
// All of them are computed on one retrieval of max(k) results, so every k sees the same ranking.
public static class RetrievalMetrics
{
    public const int MrrDepth = 10;

    public static double HitAt(GoldenQuestion question, IReadOnlyList<FilingChunkRecord> hits, int k) =>
        hits.Take(k).Any(question.IsRelevant) ? 1 : 0;

    public static double ReciprocalRank(GoldenQuestion question, IReadOnlyList<FilingChunkRecord> hits)
    {
        var rank = hits.Take(MrrDepth).ToList().FindIndex(question.IsRelevant) + 1;
        return rank == 0 ? 0 : 1.0 / rank;
    }

    // Cross-company questions expect one chunk per company, so recall shows whether every company was found.
    public static double RecallAt(GoldenQuestion question, IReadOnlyList<FilingChunkRecord> hits, int k) =>
        (double)question.Expected.Count(e => hits.Take(k).Any(e.IsSatisfiedBy)) / question.Expected.Count;

    // Nearest-rank percentile: with ~25 samples interpolation would suggest precision the data does not have.
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1)];
    }
}
