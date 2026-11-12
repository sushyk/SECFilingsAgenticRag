using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.RetrievalPipeline;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    public const string VectorStrategy = "Vector";
    public const int TopLimit = 50;

    // The key of a keyed IVectorSearchable<FilingChunkRecord>; Phase 6 registers "Hybrid" next to "Vector".
    public string Strategy { get; set; } = VectorStrategy;

    [Range(1, TopLimit)]
    public int DefaultTop { get; set; } = 5;

    [Range(1, TopLimit)]
    public int MaxTop { get; set; } = 20;

    // Learn's enrichment guidance: guard against prompt injection. Chunks flagged at indexing time never reach the model.
    public bool ExcludeInstructionLikeChunks { get; set; } = true;

    public int ClampTop(int? requested) => Math.Clamp(requested ?? DefaultTop, 1, MaxTop);
}
