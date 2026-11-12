using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.AI;

namespace SECFilingsAgenticRag.Common.VectorStore;

// Mirrors the table that MEDI's VectorStoreWriter<string> creates (private, preview schema), hence the
// lower-case storage names. Only the cleaned chunk is stored.
public sealed class FilingChunkRecord
{
    public const string CollectionName = "sec_filing_chunks";
    public const string KeyStorageName = "key", TextStorageName = "content", DocumentIdStorageName = "documentid";

    // Random per write, so it is not stable across re-indexing.
    [VectorStoreKey(StorageName = KeyStorageName)]
    public Guid Key { get; set; }

    // Cleaned text without task prefixes: it is what gets embedded, returned to the LLM and cited. Full-text indexed so
    // EnsureCollectionExistsAsync creates the GIN index over to_tsvector('english', content) that hybrid search uses.
    [VectorStoreData(StorageName = TextStorageName, IsFullTextIndexed = true)]
    public string Text { get; set; } = "";

    [VectorStoreData(StorageName = "context")]
    public string? Context { get; set; }

    [VectorStoreData(StorageName = DocumentIdStorageName, IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.Ticker, IsIndexed = true)]
    public string Ticker { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.CompanyName)]
    public string CompanyName { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.Cik)]
    public string Cik { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.FormType, IsIndexed = true)]
    public string FormType { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.FiscalYear, IsIndexed = true)]
    public int FiscalYear { get; set; }

    // Npgsql requires DateTimeKind.Utc for timestamptz.
    [VectorStoreData(StorageName = FilingChunkMetadataKeys.FilingDate)]
    public DateTime FilingDate { get; set; }

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.Section)]
    public string Section { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.ChunkIndex)]
    public int ChunkIndex { get; set; }

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.ChunkCount)]
    public int ChunkCount { get; set; }

    // Hash of the source bytes and the indexing configuration: chunks with a stale fingerprint are re-indexed.
    [VectorStoreData(StorageName = FilingChunkMetadataKeys.IndexFingerprint)]
    public string IndexFingerprint { get; set; } = "";

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.ContainsInstructionLikeText, IsIndexed = true)]
    public bool ContainsInstructionLikeText { get; set; }

    [VectorStoreData(StorageName = FilingChunkMetadataKeys.SourceUrl)]
    public string SourceUrl { get; set; } = "";

    [VectorStoreVector(EmbeddingModelConstants.Dimensions, StorageName = "embedding",
        DistanceFunction = DistanceFunction.CosineSimilarity, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}
