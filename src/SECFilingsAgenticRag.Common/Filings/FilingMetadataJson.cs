using System.Text.Json;

namespace SECFilingsAgenticRag.Common.Filings;

public static class FilingMetadataJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Serialize(FilingMetadata metadata) => JsonSerializer.Serialize(metadata, Options);

    public static FilingMetadata? Deserialize(string json) => JsonSerializer.Deserialize<FilingMetadata>(json, Options);
}
