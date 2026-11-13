namespace SECFilingsAgenticRag.Common.AI;

// Dimensions is a constant because the vector column and its HNSW index are declared with it at compile time; the prefixes
// are the task instructions the configured embedding model (AI:Embedding:Model) was trained with.
public static class EmbeddingModelConstants
{
    public const int Dimensions = 768;
    public const string DocumentPrefix = "search_document: ";
    public const string QueryPrefix = "search_query: ";
    public const string DocumentEmbeddingKey = "document";
    public const string QueryEmbeddingKey = "query";
}
