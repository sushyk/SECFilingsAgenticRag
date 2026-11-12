using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SECFilingsAgenticRag.IngestionPipeline;

public static class IndexingFingerprint
{
    // Hashes the whole "Indexing" section, so any setting that shapes chunk content (and the "Cleaning:Version" a developer
    // bumps when cleaning logic changes) re-indexes affected filings on the next normal run.
    public static string Compute(string sourceSha256, IConfiguration configuration)
    {
        var settings = configuration.GetSection(IndexingOptions.SectionName).AsEnumerable(makePathsRelative: true)
            .Where(s => s.Value is not null && !s.Key.Equals(nameof(IndexingOptions.StagingPath), StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{s.Key.ToLowerInvariant()}={s.Value}")
            .Order(StringComparer.Ordinal);

        var input = $"{sourceSha256}|{string.Join('\n', settings)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
