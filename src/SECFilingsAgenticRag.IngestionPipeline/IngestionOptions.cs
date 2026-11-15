using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.IngestionPipeline;

public sealed class IngestionOptions
{
    public const string SectionName = "Indexing";

    [Range(64, 4096)]
    public int MaxTokensPerChunk { get; set; } = 512;

    // A location, not content, so it is excluded from the index fingerprint.
    public string? StagingPath { get; set; }
}

public sealed class ChunkCleaningOptions
{
    public const string SectionName = "Indexing:Cleaning";

    public bool MarkdownArtifacts { get; set; } = true;
    public bool Boilerplate { get; set; } = true;

    [Range(2, 1000)]
    public int BoilerplateMinRepeatCount { get; set; } = 5;

    [Range(10, 400)]
    public int BoilerplateMaxLineLength { get; set; } = 80;

    public bool PromptInjection { get; set; } = true;
    public InstructionLikeTextMode PromptInjectionMode { get; set; } = InstructionLikeTextMode.Flag;
}

public enum InstructionLikeTextMode { Flag, Exclude }
