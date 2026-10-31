using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.AI;

namespace SECFilingsAgenticRag.Common.VectorStore;

// Mirrors the table that MEDI's VectorStoreWriter<string> creates (private, preview schema), hence the
// lower-case storage names. Only the cleaned chunk is stored.
public sealed class FilingChunkRecord
{
    public const string CollectionName = "sec_filing_chunks";

    // Random per write, so it is not stable across re-indexing.
    [VectorStoreKey(StorageName = "key")]
    public Guid Key { get; set; }

    // Cleaned text without task prefixes: it is what gets embedded, returned to the LLM and cited.
    [VectorStoreData(StorageName = "content")]
    public string Text { get; set; } = "";

    [VectorStoreData(StorageName = "context")]
    public string? Context { get; set; }

    [VectorStoreData(StorageName = "documentid", IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData(StorageName = "ticker", IsIndexed = true)]
    public string Ticker { get; set; } = "";

    [VectorStoreData(StorageName = "companyname")]
    public string CompanyName { get; set; } = "";

    [VectorStoreData(StorageName = "cik")]
    public string Cik { get; set; } = "";

    [VectorStoreData(StorageName = "formtype", IsIndexed = true)]
    public string FormType { get; set; } = "";

    [VectorStoreData(StorageName = "fiscalyear", IsIndexed = true)]
    public int FiscalYear { get; set; }

    // Npgsql requires DateTimeKind.Utc for timestamptz.
    [VectorStoreData(StorageName = "filingdate")]
    public DateTime FilingDate { get; set; }

    [VectorStoreData(StorageName = "section")]
    public string Section { get; set; } = "";

    [VectorStoreData(StorageName = "chunkindex")]
    public int ChunkIndex { get; set; }

    [VectorStoreData(StorageName = "chunkcount")]
    public int ChunkCount { get; set; }

    [VectorStoreData(StorageName = "containsinstructionliketext", IsIndexed = true)]
    public bool ContainsInstructionLikeText { get; set; }

    [VectorStoreData(StorageName = "sourceurl")]
    public string SourceUrl { get; set; } = "";

    [VectorStoreVector(EmbeddingModelConstants.Dimensions, StorageName = "embedding",
        DistanceFunction = DistanceFunction.CosineSimilarity, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}
