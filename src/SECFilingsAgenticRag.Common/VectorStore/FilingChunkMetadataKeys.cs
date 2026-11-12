namespace SECFilingsAgenticRag.Common.VectorStore;

// MEDI's VectorStoreWriter lower-cases chunk metadata keys into column names, so the keys are the storage names.
public static class FilingChunkMetadataKeys
{
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        FiscalYear = "fiscalyear", FilingDate = "filingdate", Section = "section", ChunkIndex = "chunkindex",
        ChunkCount = "chunkcount", IndexFingerprint = "indexfingerprint",
        ContainsInstructionLikeText = "containsinstructionliketext", SourceUrl = "sourceurl";
}
