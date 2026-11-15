using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SECFilingsAgenticRag.Common.Filings;
using SECFilingsAgenticRag.IngestionInit.Edgar;

namespace SECFilingsAgenticRag.IngestionInit;

public sealed class DownloadFilingsStep(EdgarClient edgar, IOptions<IngestionInitOptions> options, ILogger<DownloadFilingsStep> log)
{
    private readonly IngestionInitOptions _options = options.Value;

    private EdgarOptions Edgar => _options.Edgar;

    internal static string SidecarPath(string htmlPath) => Path.ChangeExtension(htmlPath, ".metadata.json");

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Edgar.Enabled)
        {
            log.LogInformation("IngestionInit download: disabled");
            return;
        }

        var tickers = Edgar.Tickers.Select(ticker => ticker.ToUpperInvariant()).ToList();
        var missing = tickers.Where(ticker => PresentPairs(ticker) < Edgar.Count).ToList();
        var present = tickers.Except(missing).Sum(PresentPairs);
        if (missing.Count == 0)
        {
            log.LogInformation("IngestionInit download: skipped (all {Count} tickers present)", tickers.Count);
            return;
        }

        int downloaded = 0, failed = 0;
        if (Edgar.UserAgent?.Contains('@') != true)
        {
            log.LogError("Set the SEC User-Agent: dotnet user-secrets set Parameters:sec-user-agent \"Your Name you@example.com\" --project src/SECFilingsAgenticRag.AppHost");
            failed = missing.Count;
        }
        else
        {
            try
            {
                var companies = await edgar.GetCompanyTickersAsync(ct);
                foreach (var ticker in missing)
                {
                    try
                    {
                        var result = await DownloadTickerAsync(ticker, companies[ticker], ct);
                        downloaded += result.Downloaded;
                        present += result.Present;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        log.LogWarning(ex, "IngestionInit download failed for {Ticker}", ticker);
                        failed++;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "IngestionInit download failed to fetch the SEC ticker list");
                failed = missing.Count;
            }
        }

        log.LogInformation("IngestionInit download: downloaded={Downloaded} present={Present} failed={Failed}", downloaded, present, failed);
        if (failed > 0)
        {
            log.LogWarning("IngestionInit download: {Failed} ticker(s) failed; continuing with the filings on disk", failed);
        }
    }

    private async Task<(int Downloaded, int Present)> DownloadTickerAsync(string ticker, CompanyTicker company, CancellationToken ct)
    {
        var (companyName, filings) = await edgar.GetSubmissionsAsync(company.Cik10, ct);

        int downloaded = 0, present = 0;
        foreach (var filing in filings.Where(filing => filing.Form == Edgar.FormType).Take(Edgar.Count))
        {
            if (filing.ReportDate is null)
            {
                log.LogWarning("{Ticker} {Accession} has no report date; using the filing date", ticker, filing.AccessionNumber);
            }

            var reportDate = filing.ReportDate ?? filing.FilingDate;
            var url = EdgarClient.DocumentUrl(company.Cik10, filing.AccessionNumber, filing.PrimaryDocument);
            var metadata = new FilingMetadata(
                ticker, companyName, company.Cik10, filing.Form, filing.AccessionNumber,
                filing.FilingDate, reportDate, reportDate.Year, filing.PrimaryDocument, url.ToString());

            var htmlPath = Path.Combine(_options.DataPath, FilingBlobConventions.BlobName(metadata, ".htm"));
            if (IsCompletePair(htmlPath))
            {
                present++;
                continue;
            }

            await edgar.DownloadToFileAsync(url, htmlPath, ct);
            await File.WriteAllTextAsync(SidecarPath(htmlPath), FilingMetadataJson.Serialize(metadata), ct);
            downloaded++;
        }

        return (downloaded, present);
    }

    private int PresentPairs(string ticker)
    {
        var directory = Path.Combine(_options.DataPath, ticker, Edgar.FormType);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.htm").Count(IsCompletePair) : 0;
    }

    private static bool IsCompletePair(string htmlPath) => File.Exists(htmlPath) && File.Exists(SidecarPath(htmlPath));
}

public sealed class UploadFilingsStep(BlobContainerClient container, IOptions<IngestionInitOptions> options, ILogger<UploadFilingsStep> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var dataPath = options.Value.DataPath;
        var files = Directory.Exists(dataPath) ? Directory.GetFiles(dataPath, "*.htm", SearchOption.AllDirectories) : [];
        if (files.Length == 0)
        {
            log.LogWarning("No filings found; set the SEC user agent and restart so the download step can fetch them");
            return;
        }

        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        int uploaded = 0, unchanged = 0, invalid = 0;
        foreach (var file in files)
        {
            var metadata = await ReadMetadataAsync(file, ct);
            if (metadata is null)
            {
                invalid++;
                continue;
            }

            await using var content = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
            var blobClient = container.GetBlobClient(FilingBlobConventions.BlobName(metadata, ".htm"));
            if (await IsCurrentAsync(blobClient, hash, ct))
            {
                unchanged++;
                continue;
            }

            content.Position = 0;
            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/html" },
                Metadata = FilingBlobConventions.ToBlobMetadata(metadata, hash),
            }, ct);
            uploaded++;
        }

        log.LogInformation("IngestionInit upload: scanned={Scanned} uploaded={Uploaded} unchanged={Unchanged} invalid={Invalid}",
            files.Length, uploaded, unchanged, invalid);
    }

    private async Task<FilingMetadata?> ReadMetadataAsync(string htmlPath, CancellationToken ct)
    {
        var sidecar = DownloadFilingsStep.SidecarPath(htmlPath);
        try
        {
            return FilingMetadataJson.Deserialize(await File.ReadAllTextAsync(sidecar, ct));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning(ex, "Skipping {File}: missing or unreadable {Sidecar}", htmlPath, sidecar);
            return null;
        }
    }

    private static async Task<bool> IsCurrentAsync(BlobClient blobClient, string hash, CancellationToken ct)
    {
        try
        {
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            return properties.Value.Metadata.TryGetValue(FilingBlobConventions.ContentSha256, out var stored)
                && stored.Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
