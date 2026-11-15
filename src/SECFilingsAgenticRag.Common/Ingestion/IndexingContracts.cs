namespace SECFilingsAgenticRag.Common.Ingestion;

public sealed record IndexingRequest(IReadOnlyList<string>? Tickers = null, bool Force = false);

public enum DocumentIndexingStatus { Indexed, Skipped, Failed }

public sealed record DocumentIndexingResult(string DocumentId, DocumentIndexingStatus Status, int ChunkCount, TimeSpan Duration, string? Error);

public sealed record IndexingReport(DateTimeOffset StartedAt, TimeSpan Duration, IReadOnlyList<DocumentIndexingResult> Documents)
{
    public int Indexed => Documents.Count(d => d.Status == DocumentIndexingStatus.Indexed);
    public int Skipped => Documents.Count(d => d.Status == DocumentIndexingStatus.Skipped);
    public int Failed => Documents.Count(d => d.Status == DocumentIndexingStatus.Failed);
}
