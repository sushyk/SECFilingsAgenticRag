using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.RetrievalPipeline;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    public const string VectorStrategy = "Vector";
    public const string HybridStrategy = "Hybrid";
    public const int TopLimit = 50;

    // The key of a keyed IVectorSearchable<FilingChunkRecord>.
    public string Strategy { get; set; } = VectorStrategy;

    [Range(1, TopLimit)]
    public int DefaultTop { get; set; } = 5;

    [Range(1, TopLimit)]
    public int MaxTop { get; set; } = 20;

    // Learn's enrichment guidance: guard against prompt injection. Chunks flagged at indexing time never reach the model.
    public bool ExcludeInstructionLikeChunks { get; set; } = true;

    public RerankOptions Rerank { get; set; } = new();
    public MultiQueryOptions MultiQuery { get; set; } = new();
    public HybridOptions Hybrid { get; set; } = new();

    public int ClampTop(int? requested) => Math.Clamp(requested ?? DefaultTop, 1, MaxTop);

    // The composed pipeline's name, reported by /api/search and the span, and asserted by the eval matrix.
    public string Describe() =>
        Strategy + (MultiQuery.Enabled ? "+MultiQuery" : "") + (Rerank.Enabled ? "+Rerank" : "");
}

// A null Model means the registered AI:Chat client; set one to rerank or rewrite with a different Ollama model.
public sealed class RerankOptions
{
    public bool Enabled { get; set; }
    public int CandidatePool { get; set; } = 20;
    public string? Model { get; set; }
}

public sealed class MultiQueryOptions
{
    public bool Enabled { get; set; }
    public int Variants { get; set; } = 3;
    public string? Model { get; set; }
}

public sealed class HybridOptions
{
    public int CandidatePool { get; set; } = 40;

    // Also fuses the multi-query variants, so one k governs every RRF in the pipeline.
    public int RrfK { get; set; } = 60;
}
