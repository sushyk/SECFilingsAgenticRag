namespace SECFilingsAgenticRag.Common.AI;

// The one embedding model of the system. Dimensions is a constant because the vector column and its
// HNSW index are declared with it at compile time; the prefixes are what nomic-embed-text was trained with.
public static class EmbeddingModelConstants
{
    public const string Name = "nomic-embed-text";
    public const int Dimensions = 768;
    public const string DocumentPrefix = "search_document: ";
    public const string QueryPrefix = "search_query: ";
    public const string DocumentEmbeddingKey = "document";
    public const string QueryEmbeddingKey = "query";
}
