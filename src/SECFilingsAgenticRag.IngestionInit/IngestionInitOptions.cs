using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace SECFilingsAgenticRag.IngestionInit;

public sealed class IngestionInitOptions
{
    public const string SectionName = "IngestionInit";

    [Required]
    public string DataPath { get; set; } = null!;

    [ValidateObjectMembers]
    public EdgarOptions Edgar { get; set; } = new();
}

public sealed class EdgarOptions
{
    public bool Enabled { get; set; } = true;
    // Empty default: configuration binding appends to a non-empty list, so defaults live in appsettings.json.
    public List<string> Tickers { get; set; } = [];
    public string FormType { get; set; } = "10-K";

    [Range(1, 10)]
    public int Count { get; set; } = 1;

    public string? UserAgent { get; set; }

    [Range(1, 10)]
    public int MaxRequestsPerSecond { get; set; } = 8;
}
