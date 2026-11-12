using System.Text.Json;
using System.Text.Json.Serialization;

namespace SECFilingsAgenticRag.Common.Ingestion;

// The Function and its clients must agree on casing; string enums keep the report readable in curl and the dashboard.
public static class IndexingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
