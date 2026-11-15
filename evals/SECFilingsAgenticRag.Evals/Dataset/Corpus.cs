using Npgsql;
using NpgsqlTypes;
using SECFilingsAgenticRag.Common.VectorStore;
using Columns = SECFilingsAgenticRag.Common.VectorStore.FilingChunkMetadataKeys;

namespace SECFilingsAgenticRag.Evals.Dataset;

// Raw SQL because MEVD's GetAsync(filter, top) returns records, not DISTINCT lists or GROUP BY aggregates.
public sealed class Corpus(NpgsqlDataSource dataSource)
{
    private const string Table = FilingChunkRecord.CollectionName, DocumentId = FilingChunkRecord.DocumentIdStorageName;
    private const int SnippetLength = 160;

    public async Task<IReadOnlySet<string>> DocumentIdsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""SELECT DISTINCT "{DocumentId}" FROM "{Table}" """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        HashSet<string> ids = [];
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<(int Documents, int Chunks)> SizeAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""SELECT COUNT(DISTINCT "{DocumentId}")::int, COUNT(*)::int FROM "{Table}" """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // Exists to author golden.json from what is really indexed, not from memory of the filings.
    public async Task PrintSectionsAsync(string? ticker, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT "{DocumentId}", "{Columns.Section}", COUNT(*)::int,
                   (array_agg(left(regexp_replace("{FilingChunkRecord.TextStorageName}", '\s+', ' ', 'g'), {SnippetLength})
                              ORDER BY "{Columns.ChunkIndex}"))[1:2]
            FROM "{Table}"
            WHERE @ticker::text IS NULL OR "{Columns.Ticker}" = upper(@ticker)
            GROUP BY 1, 2
            ORDER BY 1, MIN("{Columns.ChunkIndex}")
            """);
        // A null parameter needs an explicit type, or Postgres cannot infer one for "@ticker IS NULL".
        command.Parameters.Add(new NpgsqlParameter("ticker", NpgsqlDbType.Text) { Value = (object?)ticker ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(ct);
        string? document = null;
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) != document)
                Console.WriteLine($"\n## {document = reader.GetString(0)}");
            Console.WriteLine($"- {reader.GetString(1)} ({reader.GetInt32(2)} chunks)");
            foreach (var snippet in reader.GetFieldValue<string[]>(3))
                Console.WriteLine($"    > {snippet}");
        }
    }
}
