namespace SECFilingsAgenticRag.Common.Telemetry;

// Each name is both an ActivitySource and a Meter. ServiceDefaults cannot reference Common, so it repeats these strings.
public static class TelemetryNames
{
    public const string AI = "SECFilingsAgenticRag.AI";
    public const string Agent = "SECFilingsAgenticRag.AgentServer";
    public const string Ingestion = "SECFilingsAgenticRag.IngestionPipeline";
    public const string Retrieval = "SECFilingsAgenticRag.RetrievalPipeline";
}

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    // Prompts and responses carry user questions, filing text and whole chat histories into traces, which live in a store with
    // weaker access control than the database and cannot be redacted afterwards. Enable only through the environment, briefly.
    public bool EnableSensitiveData { get; set; }
}
