# Phase 01 notes: IngestionInit (EDGAR download + blob staging)

## What was built
- `src/SECFilingsAgenticRag.IngestionInit` (console, exits when done): `DownloadFilingsStep`, `UploadFilingsStep`, `Edgar/` client (`EdgarUrls`, `EdgarJsonParser`, `EdgarClient`, `SecThrottlingHandler`, `AddEdgarClient`), `IngestionInitOptions`, and an internal `FilingFiles` helper (sidecar path and "complete pair" check, shared by both steps).
- `SECFilingsAgenticRag.Common/Filings`: `FilingMetadata`, `FilingBlobConventions`, `FilingMetadataJson`. Phase 00's simplification removed them, so this phase creates them.
- AppHost: `sec-user-agent` parameter and the `ingestion-init` project resource (reference + wait on `filings`, env `IngestionInit__DataPath` / `IngestionInit__Edgar__UserAgent`).

## Results
- Packages: `Aspire.Azure.Storage.Blobs` 13.6.4 (same as the other Aspire packages), `Microsoft.Extensions.Hosting` 10.0.14, `Microsoft.Extensions.Http` 10.0.14.
- Blob container client: `builder.AddAzureBlobContainerClient("filings")` (exists in 13.6.4; resolves `BlobContainerClient`).
- Azurite workaround: none needed.
- Downloaded (first clean run, `downloaded=5 present=0 failed=0`, then `uploaded=5`):

| Ticker | Accession | Report date | File size |
|---|---|---|---|
| AAPL | 0000320193-25-000079 | 2025-09-27 | 1.5 MB |
| AMZN | 0001018724-26-000004 | 2025-12-31 | 2.0 MB |
| MSFT | 0001193125-26-323660 | 2026-06-30 | 8.6 MB |
| NVDA | 0001045810-26-000021 | 2026-01-25 | 2.0 MB |
| TSLA | 0001628280-26-003952 | 2025-12-31 | 2.4 MB |

- Second run: `download: skipped (all 5 tickers present)`, no `sec.gov` request in the job log, `uploaded=0 unchanged=5`.
- Change detection: appended a comment to the AAPL file → `uploaded=1 unchanged=4`. Deleting `data/filings/AAPL` → `downloaded=1 present=4 failed=0`.
- Blobs checked with the Azure CLI (`az storage blob list ... --include m`): 5 rows `AAPL/10-K/<accession>.htm` etc., each with `ticker`, URL-encoded `companyname` (`Apple%20Inc.`) and `contentsha256`.
- Unset secret: the job logs the "Set the SEC User-Agent" error, counts the missing ticker as failed, uploads what exists, exits 0.

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | `RecentFiling(..., DateOnly ReportDate, ...)` | `DateOnly? ReportDate` | The parser cannot log; the step applies the "fall back to FilingDate and warn" rule |
| 2 | `EdgarOptions.Tickers` defaults to the five tickers | Defaults to empty; the five tickers live in `appsettings.json` only | Configuration binding **appends** to a non-empty default list: the first run logged `all 10 tickers`. Seen in the job log |
| 3 | Explicit `Accept-Encoding: gzip, deflate` header | Omitted | `SocketsHttpHandler.AutomaticDecompression` already adds it (Learn #4) |
| 4 | `AddParameter("sec-user-agent", secret: true)` | `AddParameter("sec-user-agent", () => builder.Configuration["Parameters:sec-user-agent"] ?? "", secret: true)` | Verified: with the plain form and no value, `ingestion-init` never starts (Aspire waits for the value). Spec §6.6 asked for the optional form |
| 5 | `FilingMetadata` as in the spec | `DocumentId` has `[JsonIgnore]` | The computed property was being written into the sidecar JSON; the spec's example has no `documentId` |
| 6 | `aspire start/wait/logs` | `dotnet run --project src\SECFilingsAgenticRag.AppHost --no-build`; the job log was read from DCP's `%TEMP%\aspire-dcp*\*_out` files | Installed CLI 13.2.2 cannot drive the 13.6.4 AppHost (Phase 00 deviation 5). The CLI did start the AppHost once, and the job ran during that attempt |
| 7 | `aspire.config.json` | The CLI changed its stale `path` to `SECFilingsAgenticRag.AppHost.csproj` | Leftover from the rename; kept |
| 8 | `FiscalYear = ReportDate.Year` | As specified | Known simplification: fiscal years ending in early January and 52/53-week years can be off by one |
| 9 | AC1 "0 warnings" | Met; `EXTEXP0001` (experimental `RemoveAllResilienceHandlers`) is suppressed with a scoped pragma, not project-wide | Learn marks the API experimental |

SEC User-Agent used in this run is a placeholder (`SEC Filings Agent dev@example.com`) in AppHost user-secrets; SEC accepted it. Replace it with your own.

## Anything Phase 02 must know
- Blob name = `{TICKER}/{FormType}/{Accession}.htm`; container `filings`; content type `text/html`.
- Blob metadata keys (lower-case, `FilingBlobConventions`): `ticker, companyname (URL-encoded), cik, formtype, accession, filingdate, reportdate, fiscalyear, primarydocument, sourceurl, contentsha256`. `FromBlobMetadata` reverses `ToBlobMetadata`.
- `contentsha256` is the lower-case hex SHA-256 of the file bytes; use it as the up-to-date key for indexing.
- The job never calls indexing; a missing container is possible before the job has run (it creates it).
- The sidecar JSON is `FilingMetadataJson` (camelCase, indented, dates `yyyy-MM-dd`).

## Commands & operating notes
- **SEC User-Agent (personal data, user-secrets only):** `dotnet user-secrets set "Parameters:sec-user-agent" "Your Name you@example.com" --project src\SECFilingsAgenticRag.AppHost`. It must contain `@`.
- **Run:** `aspire start` / `aspire wait ingestion-init --status down --timeout 3600` / `aspire logs ingestion-init` once the CLI matches the AppHost (`aspire update`); otherwise `dotnet run --project src\SECFilingsAgenticRag.AppHost --no-build` and read `%TEMP%\aspire-dcp*\*_out` (grep `IngestionInit`). Note a stale `aspire-dcp*` folder from an earlier run can match; use the newest.
- **Add tickers / form / count:** edit `IngestionInit:Edgar` in `src/SECFilingsAgenticRag.IngestionInit/appsettings.json`, or set env `IngestionInit__Edgar__Tickers__0=...` (the list is replaced per index). Only tickers with fewer than `Count` complete pairs on disk are fetched.
- **Force a re-download:** delete the ticker's folder under `data\filings`, then restart. To also force a re-upload, delete the blob or change the file.
- **Hand-made filings:** drop `{TICKER}\{FormType}\{accession}.htm` plus `{accession}.metadata.json` (same shape as the generated sidecar) into `data\filings`; the upload step treats them like downloaded ones.
- **Check blobs:** `az storage blob list --connection-string "<Azurite dev string, spec §4>" -c filings --include m -o table` (blob port 10000 is fixed).
- **Reset Azurite state (for a clean first run):** stop the AppHost, `docker rm -f` the `storage-*` container, `docker volume rm secfilingsagenticrag.apphost-*-storage-data`, delete `data\filings`.
- **Gotchas:** config binding appends to non-empty default lists (deviation 2); an unset required Aspire parameter stops the resource from starting (deviation 4); the bash tool breaks on apostrophes in heredocs, use the file tools.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.resiliencehttpclientbuilderextensions.removeallresiliencehandlers | `RemoveAllResilienceHandlers` exists, is `[Experimental("EXTEXP0001")]` | Used on the `edgar` client so ServiceDefaults' standard handler does not retry against SEC; scoped pragma (deviation 9) |
| 2 | https://learn.microsoft.com/dotnet/core/resilience/http-resilience#remove-standard-resilience-handlers | Pattern for a named client under `ConfigureHttpClientDefaults` | `AddHttpClient<EdgarClient>("edgar").RemoveAllResilienceHandlers()` |
| 3 | https://learn.microsoft.com/dotnet/aspire/compatibility/9.4/azure-storage-apis-renamed | Storage client/hosting API renames (`AddAzureBlobServiceClient`, `AddBlobContainer`) | Hosting: `storage.AddBlobContainer("filings")` (unchanged). The page does not list the container client; the method name was confirmed by compiling against `Aspire.Azure.Storage.Blobs` 13.6.4 |
| 4 | https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.automaticdecompression | Setting `AutomaticDecompression` adds `Accept-Encoding` to every request | No explicit `Accept-Encoding` header (deviation 3) |
| 5 | Learn search for Aspire external parameters (no page surfaced for unset-secret behavior) | Behavior of an unset `AddParameter` | Tested instead: unset required parameter blocks the resource; moved to the value-getter form (deviation 4) |
| 6 | *external* SEC EDGAR (`company_tickers.json`, `submissions/CIK##########.json`, 10 req/s and User-Agent policy) | JSON shapes and rate policy as stated in the spec; confirmed by real responses | `EdgarJsonParser`, `SecThrottlingHandler`, `EdgarOptions.MaxRequestsPerSecond` |

RAG guide mapping: this phase is the **preparation phase** ("gathering representative documents": pull source documents into a pipeline). Chunking, enrichment and embeddings start in Phase 02.

## Suggested commit message
```
Phase 01: IngestionInit init job (offline-first EDGAR download + idempotent upload to Azurite with filing metadata), AppHost wiring
```
