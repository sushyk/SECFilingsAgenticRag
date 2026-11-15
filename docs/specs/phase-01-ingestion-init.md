# Phase 01 — IngestionInit: EDGAR download + blob staging

> Depends on: Phase 00 | Produces: `src/SECFilingsAgenticRag.IngestionInit` (init job: EDGAR download + blob upload, then exit), AppHost wiring for `ingestion-init` + `sec-user-agent` parameter | Est. size: M

## 0. Instructions for the implementing agent

- **Follow the rules in `CLAUDE.md`.** Read it before you write any code. Its rules apply to every line you write and override the style of the code sketches in this spec:
  - write the simplest code that meets the requirement, with no unnecessary abstractions or verbosity;
  - make the code self-explanatory, and write comments only to explain *why* (a business or design decision, extra context), never *what* the code does.
  - The comments in this spec's sketches are explanations for you. Do not copy them into the source.
  - **Do not create or edit `CLAUDE.md`.** The user maintains it by hand.
  - Read it if it has content; it may be empty.
  - Anything this spec asks you to *document* (commands, env vars, gotchas, how-to sections) goes into your `docs/notes/phase-NN-notes.md`, under a `## Commands & operating notes` heading.
  - Later phases read all the notes files.
- **Microsoft Learn is the source of truth for facts.**
  - **What it covers:** how Microsoft SDKs and APIs behave (Microsoft.Extensions.AI, VectorData, DataIngestion, Agent Framework, Aspire, Azure Functions, Azure SDKs), plus RAG concepts, terminology and phase order.
  - **What it overrides:** this spec, `docs/02`, blog posts and your own prior knowledge. "Microsoft Learn" means the current docs on learn.microsoft.com, including the Azure Architecture Center RAG guide: https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide
  - **On a conflict:** follow Learn, keep the spec's intent, and record the conflict (spec text → Learn URL → what you did) in the notes evidence table (§11.0).
  - **What stays as specified:** project-specific choices (names, contracts, config keys, resource names), unless Learn shows they're technically impossible.
  - **Non-Microsoft technology** (pgvector, SEC EDGAR, PdfPig): use that project's official docs and mark them *external*.
- **Built-in first.** Use the SDK abstractions directly. Do not add a custom interface, wrapper, base class or framework when a built-in type covers the need. Every custom type you add must say, in a one-line code comment *and* in the notes, which built-in it couldn't use and why. The full audit is the "Custom types and why no built-in fits" table in docs/02. "Built-in" includes:
  - **Microsoft.Extensions.AI:** `IChatClient`, `IEmbeddingGenerator` and their middleware (`UseFunctionInvocation`, `UseDistributedCache`, `UseOpenTelemetry`, `UseLogging`, `ConfigureOptions`, `Use(...)`), plus structured output via `GetResponseAsync<T>`.
  - **Microsoft.Extensions.VectorData:** `VectorStoreCollection`, `IVectorSearchable<T>`, `IKeywordHybridSearchable<T>`, `VectorSearchOptions<T>.Filter`, `GetAsync(filter, top)`.
  - **Microsoft.Extensions.DataIngestion:** readers, chunkers, enrichers, `IngestionPipeline`, `VectorStoreWriter`.
  - **Microsoft.ML.Tokenizers.**
  - **Microsoft Agent Framework:** agents, middleware, context providers, hosting, `AgentSessionStore`, AG-UI.
  - **Aspire and ASP.NET Core built-ins:** health checks, rate limiting, ProblemDetails.
- **Authority.** This spec plus [`docs/01-architecture-and-design.md`](../01-architecture-and-design.md) are authoritative. If code already on disk conflicts with a contract here, **the code on disk wins**: keep it and record the deviation in `docs/notes/phase-01-notes.md`.
- **Read first:** `docs/notes/phase-00-notes.md` and `CLAUDE.md`, for deviations and conventions from Phase 00.
- **Verify unfamiliar or preview APIs** before coding against them.
  - Use the Microsoft Learn MCP tools if available: `microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`.
  - Otherwise use the package XML docs or compiler errors.
  - Pay particular attention to the Aspire 13 hosting and client APIs and to Azure.Storage.Blobs.
- **Central package management (CPM).**
  - Versions go in `Directory.Packages.props`.
  - Pick the latest stable version, and a preview only where no stable version exists.
  - Find versions with `dotnet package search <id> --exact-match --prerelease`.
  - **After every `dotnet new` scaffold**: strip each `Version="..."` from the `.csproj`, add the `<PackageVersion>` to `Directory.Packages.props`, and delete `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` (they come from `Directory.Build.props`). Otherwise you get **NU1008**.
  - Any `Aspire.*` package you add uses the **same** version as the existing Aspire packages and the AppHost SDK attribute.
- **Platform.** Windows 11 with **PowerShell 7.3+ (`pwsh`)**.
- **Agent-executable checks.** Use `aspire start` (background), `aspire wait <res>`, `aspire describe`, `aspire logs <res>`, `aspire resource <res> <cmd>` and `aspire stop`. The dashboard is an optional human check only.
- Match the existing code style. Don't change Phase 00 public contracts unless unavoidable, and document it if you do.
- **Do NOT `git commit`.** The user commits.
- Ask nothing. Decide and document.
- **SEC data policy.** Every request to `sec.gov` / `data.sec.gov` sends a `User-Agent` of the form `"<Name> <email>"` and stays at **≤ 10 requests per second**.

## 1. Goal and why

RAG needs documents. This phase builds **one** program, the **IngestionInit** init job, which Aspire starts once per `aspire start`. It gets real 10-K filings onto disk and into blob storage in two plain steps that `Program.cs` calls in sequence (no step framework):

1. **`DownloadFilingsStep`**. For each configured ticker, find the most recent 10-K on SEC EDGAR, and download the main HTML document plus a sidecar `*.metadata.json`. The sidecar holds the company name, fiscal year, filing date and source URL; those later become citation info. **Skip** the step when the files are already on disk, so demos work offline.
2. **`UploadFilingsStep`**. Upload the local files to the Azurite blob container `filings`, with the metadata attached to each blob. Only files that changed are uploaded.

Then it exits. It does **not** start indexing: the user triggers the indexing Function themselves (Phase 02: the `reindex` command or `scripts/Invoke-Indexing.ps1`).

**Why upload at all, instead of mounting the folder into Azurite?** Azurite stores blobs in its own internal format (`__blobstorage__` plus JSON metadata files). A bind-mounted folder of `.htm` files does **not** appear as blobs, so files must go through the Blob SDK.

**Why an init job that *exits*?** This is the Kubernetes *init container* pattern: the job runs once to completion and prepares the inputs, and only then is indexing requested. It is **not** a sidecar, because it doesn't keep running next to another service, and it isn't a database "init job" either: it acquires and stages **source documents**, which Microsoft's RAG guide describes as "documents are pushed or pulled into a data pipeline". Because it finishes, `aspire wait ingestion-init --status down` tells you when the documents are in place. With only two steps there is no step framework (built-in first: no custom interface for two calls). A later step is one more line in `Program.cs`.

**Why one app, not a separate downloader tool?** Downloading and uploading are just two steps of seeding. One program means one project, one configuration, and nothing to run by hand. Offline demos still work, because the download step skips filings that are already on disk.

## 2. Concepts in 60 seconds

- **SEC EDGAR**: the SEC's public filing system. A 10-K is the annual report (business overview, risk factors, MD&A, financial statements). In Microsoft's RAG guide this is the **preparation phase**: gathering representative documents.
- **CIK**: the SEC's company ID, zero-padded to 10 digits in API URLs (`0000320193` = Apple).
- **Accession number**: the unique ID of one filing (`0000320193-25-000079`).
- **Blob metadata**: small key/value strings stored with a blob. We use it to carry `FilingMetadata` plus a content hash, so later steps need no separate database.
- **Idempotency**: running a step twice gives the same result as running it once. Existing files aren't re-downloaded, and unchanged files aren't re-uploaded (nor, in Phase 02, re-indexed).

## 3. Environment and prerequisites

| Tool | Check |
|---|---|
| .NET SDK 10.0.4xx | `dotnet --list-sdks` |
| Docker Desktop running (Azurite runs in Docker) | `docker info --format '{{.ServerVersion}}'` |
| Aspire CLI 13.x | `aspire --version` |
| Internet access to `www.sec.gov` / `data.sec.gov` (first run only) | `(Invoke-WebRequest https://www.sec.gov/files/company_tickers.json -Headers @{ 'User-Agent' = 'Your Name you@example.com' } -SkipHeaderValidation -Method Get).StatusCode` → 200. `-SkipHeaderValidation` is required because PowerShell rejects `"Name email"` as a User-Agent product token. |
| SEC User-Agent configured (your name and email; personal data, so use user-secrets) | `dotnet user-secrets set "Parameters:sec-user-agent" "Your Name you@example.com" --project src\SECFilingsAgenticRag.AppHost` |
| Ollama | not needed this phase |
| Optional: Azure Storage Explorer, to browse Azurite blobs | – |

## 4. Repo state at start

```
SECFilingsAgent\
├─ SECFilingsAgenticRag.slnx, global.json, Directory.Build.props, Directory.Packages.props, CLAUDE.md, .gitignore (ignores data/filings/)
├─ docs\ (01-architecture-and-design, 02-microsoft-guidance-alignment, specs\)
├─ src\
│  ├─ SECFilingsAgenticRag.AppHost\        (postgres+secfilings, storage+filings, ollama conn string, model parameters)
│  ├─ SECFilingsAgenticRag.ServiceDefaults\
│  ├─ SECFilingsAgenticRag.Common\
│  └─ SECFilingsAgenticRag.IngestionPipeline\       (empty classlib)
```

Phase 00 contracts you will use (copied from `SECFilingsAgenticRag.Common`):

```csharp
namespace SECFilingsAgenticRag.Common.Filings;

public sealed record FilingMetadata(
    string Ticker,            // "AAPL" (upper-case)
    string CompanyName,       // "Apple Inc."
    string Cik,               // "0000320193" (10 digits, zero-padded)
    string FormType,          // "10-K"
    string AccessionNumber,   // "0000320193-25-000079"
    DateOnly FilingDate,      // date filed with the SEC
    DateOnly ReportDate,      // period of report (fiscal year end)
    int FiscalYear,           // ReportDate.Year
    string PrimaryDocument,   // "aapl-20250927.htm"
    string SourceUrl)         // https://www.sec.gov/Archives/edgar/data/320193/000032019325000079/aapl-20250927.htm
{
    public string DocumentId => $"{Ticker}/{FormType}/{AccessionNumber}";
}

public static class FilingBlobConventions
{
    public static string BlobName(FilingMetadata m, string extension) => $"{m.DocumentId}{extension}"; // "AAPL/10-K/0000320193-25-000079.htm"
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        Accession = "accession", FilingDate = "filingdate", ReportDate = "reportdate", FiscalYear = "fiscalyear",
        PrimaryDocument = "primarydocument", SourceUrl = "sourceurl",
        ContentSha256 = "contentsha256";     // hex SHA-256 of the file bytes, computed by the IngestionInit job
    public static IDictionary<string, string> ToBlobMetadata(FilingMetadata m, string contentSha256);
    public static FilingMetadata FromBlobMetadata(IDictionary<string, string> metadata);
}
```

AppHost resources that already exist (from `src/SECFilingsAgenticRag.AppHost/AppHost.cs`):

```csharp
var storage = builder.AddAzureStorage("storage").RunAsEmulator(e => e.WithDataVolume().WithBlobPort(10000)); // fixed blob port
var filings = storage.AddBlobContainer("filings");
// also: postgres/"secfilings", AddConnectionString("ollama"), parameters chat-model / embedding-model / embedding-dimensions,
// and the helper AIModelResourceExtensions.WithAIModels(chatModel, embeddingModel, embeddingDimensions) (not needed by the IngestionInit job)
```

The well-known Azurite dev connection string for CLI checks. It's public, not a secret, and the AppHost fixes the blob port at 10000:

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;
```

## 5. Scope

**In**
- `src/SECFilingsAgenticRag.IngestionInit`:
  - `DownloadFilingsStep` with the EDGAR client in `src/SECFilingsAgenticRag.IngestionInit/Edgar/`;
  - `UploadFilingsStep`;
  - a `Program.cs` that calls them in order and returns the exit code.
- AppHost wiring, including the `sec-user-agent` parameter.
- A first real run that downloads AAPL, MSFT, NVDA, AMZN and TSLA, so `data/filings` exists locally (it is git-ignored).

**Out**
- Calling the indexing Function. The init job never does this; the user does, from Phase 02 on.
- HTML parsing and chunking (Phase 02).
- 10-Q and other forms. `IngestionInit:Edgar:FormType` is configurable, but only 10-K is tested.
- PDF.

## 6. Design

### 6.1 File layout on disk (written by the download step, read by the upload step)

```
data\filings\
  AAPL\10-K\0000320193-25-000079.htm
  AAPL\10-K\0000320193-25-000079.metadata.json
  MSFT\10-K\...
```

The path is `{TICKER}/{FormType}/{AccessionNumber}{ext}`. That makes the relative path exactly equal to `FilingBlobConventions.BlobName(metadata, ".htm")`, with `/` separators.

`*.metadata.json` is `FilingMetadata` serialized with `System.Text.Json`, camelCase and indented. `DateOnly` uses the default `"yyyy-MM-dd"` format. Example (values illustrative):

```json
{
  "ticker": "AAPL",
  "companyName": "Apple Inc.",
  "cik": "0000320193",
  "formType": "10-K",
  "accessionNumber": "0000320193-25-000079",
  "filingDate": "2025-10-31",
  "reportDate": "2025-09-27",
  "fiscalYear": 2025,
  "primaryDocument": "aapl-20250927.htm",
  "sourceUrl": "https://www.sec.gov/Archives/edgar/data/320193/000032019325000079/aapl-20250927.htm"
}
```

You can also drop in hand-made files in this layout, such as a PDF in Phase 10. The upload step treats them like downloaded ones.

Put a shared `FilingMetadataJson` helper (`JsonSerializerOptions` plus `Serialize` and `Deserialize`) in **`SECFilingsAgenticRag.Common/Filings/FilingMetadataJson.cs`**. Both IngestionInit steps use it, and so does anything that writes sidecars by hand.

### 6.2 IngestionInit core (`src/SECFilingsAgenticRag.IngestionInit`, console, one-shot; references Common + ServiceDefaults)

Packages:
- `Aspire.Azure.Storage.Blobs`, the Aspire client integration, which also pulls in `Azure.Storage.Blobs`;
- `Microsoft.Extensions.Http`.

```csharp
namespace SECFilingsAgenticRag.IngestionInit;

public sealed class IngestionInitOptions               // section "IngestionInit"
{
    public const string SectionName = "IngestionInit";
    [Required] public string DataPath { get; set; } = null!;   // absolute path to data/filings (set by AppHost)
    public EdgarOptions Edgar { get; set; } = new();
}

public sealed class EdgarOptions                 // section "IngestionInit:Edgar"
{
    public bool Enabled { get; set; } = true;
    public List<string> Tickers { get; set; } = ["AAPL", "MSFT", "NVDA", "AMZN", "TSLA"];
    public string FormType { get; set; } = "10-K";             // exact match; "10-K/A" amendments excluded
    [Range(1, 10)] public int Count { get; set; } = 1;          // most recent N filings per ticker
    public string? UserAgent { get; set; }                     // "<Name> <email>"; REQUIRED when a download is actually needed
    [Range(1, 10)] public int MaxRequestsPerSecond { get; set; } = 8;   // SEC limit is 10
}

public sealed record DownloadSummary(bool Skipped, int Downloaded, int AlreadyPresent, int Failed);
public sealed record UploadSummary(int Scanned, int Uploaded, int Unchanged, int Invalid);

// Custom (no built-in fits): EDGAR download — no SDK for SEC EDGAR exists.
public sealed class DownloadFilingsStep { public Task<DownloadSummary> RunAsync(CancellationToken ct); }

// Custom (no built-in fits): pairs local .htm + .metadata.json and uploads changed files via the Blob SDK (BlobContainerClient is the built-in it uses).
public sealed class UploadFilingsStep { public Task<UploadSummary> RunAsync(CancellationToken ct); }
```

There is **no** step interface, runner or shared context object: two sequential calls don't need a framework. (Indexing is not a step either: the user calls the indexing Function.)

`Program.cs`. The IngestionInit job must **exit**, so don't use `host.RunAsync()` with a hosted service:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddAzureBlobContainerClient("filings");          // Aspire client integration; verify the name for Aspire 13
builder.Services.AddOptions<IngestionInitOptions>().BindConfiguration(IngestionInitOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddEdgarClient();                       // §6.3: named HttpClient "edgar" with SecThrottlingHandler
builder.Services.AddSingleton<DownloadFilingsStep>();
builder.Services.AddSingleton<UploadFilingsStep>();
using var host = builder.Build();
await host.StartAsync();                                  // starts telemetry exporters
var log = host.Services.GetRequiredService<ILogger<Program>>();
var exitCode = 0;
try
{
    await host.Services.GetRequiredService<DownloadFilingsStep>().RunAsync(CancellationToken.None);  // never throws for EDGAR problems (§6.4)
    await host.Services.GetRequiredService<UploadFilingsStep>().RunAsync(CancellationToken.None);
}
catch (Exception ex) { log.LogError(ex, "IngestionInit failed"); exitCode = 1; }  // only the job's own failures (e.g. blob upload)
await host.StopAsync();                                   // flush telemetry
return exitCode;
```

The IngestionInit job needs **no AI configuration**: it doesn't call `AddSecFilingsAI`. It uses `Microsoft.NET.Sdk` (not Web), so add `<None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />` to the csproj.

`appsettings.json`:

```json
{ "IngestionInit": { "Edgar": { "Enabled": true, "Tickers": ["AAPL","MSFT","NVDA","AMZN","TSLA"], "FormType": "10-K", "Count": 1, "MaxRequestsPerSecond": 8 } } }
```

There's no `DataPath` and no `UserAgent` in it; the AppHost injects both.

### 6.3 EDGAR client (`src/SECFilingsAgenticRag.IngestionInit/Edgar/`, namespace `SECFilingsAgenticRag.IngestionInit.Edgar`)

```csharp
public sealed record CompanyTicker(string Ticker, string Cik10, string Title);
public sealed record RecentFiling(string Form, string AccessionNumber, DateOnly FilingDate, DateOnly ReportDate, string PrimaryDocument);

public static class EdgarUrls
{
    public static readonly Uri CompanyTickers = new("https://www.sec.gov/files/company_tickers.json");
    public static Uri Submissions(string cik10) => new($"https://data.sec.gov/submissions/CIK{cik10}.json");
    public static Uri Document(string cik10, string accessionNumber, string primaryDocument);
    // => https://www.sec.gov/Archives/edgar/data/{cik without leading zeros}/{accession without dashes}/{primaryDocument}
    public static string PadCik(long cik) => cik.ToString("D10");
}

public static class EdgarJsonParser
{
    public static IReadOnlyDictionary<string, CompanyTicker> ParseCompanyTickers(Stream json);   // key = upper-case ticker
    public static (string CompanyName, IReadOnlyList<RecentFiling> Filings) ParseSubmissions(Stream json);
}

public sealed class EdgarClient   // typed client over the named HttpClient "edgar"; all network I/O lives here
{
    public EdgarClient(HttpClient http);
    public Task<IReadOnlyDictionary<string, CompanyTicker>> GetCompanyTickersAsync(CancellationToken ct);
    public Task<(string CompanyName, IReadOnlyList<RecentFiling> Filings)> GetSubmissionsAsync(string cik10, CancellationToken ct);
    public Task DownloadToFileAsync(Uri url, string path, CancellationToken ct);   // stream to temp file, then File.Move (atomic)
}

/// DelegatingHandler enforcing a minimum interval between requests (1 / MaxRequestsPerSecond; 8 => 125 ms),
/// plus retry with backoff on 429 / 503 (max 3 attempts; honour Retry-After if present).
public sealed class SecThrottlingHandler : DelegatingHandler
{
    public SecThrottlingHandler(TimeSpan minInterval, TimeProvider? timeProvider = null);
}

public static class EdgarServiceCollectionExtensions
{
    /// Named HttpClient "edgar" + EdgarClient:
    /// - primary handler SocketsHttpHandler { AutomaticDecompression = GZip | Deflate }, then SecThrottlingHandler;
    /// - DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.Edgar.UserAgent) — "Name email@x" always
    ///   fails ProductInfoHeaderValue validation, so never use UserAgent.ParseAdd;
    /// - Accept-Encoding: gzip, deflate; Timeout = 2 min;
    /// - RemoveAllResilienceHandlers(): ServiceDefaults' standard handler would retry against SEC and break the
    ///   rate limit; SecThrottlingHandler owns retries (verify the method; else exclude "edgar" in ServiceDefaults).
    public static IServiceCollection AddEdgarClient(this IServiceCollection services);
}
```

EDGAR JSON shapes (verified formats; parse only what's needed):

`company_tickers.json`, an object whose values are companies:

```json
{ "0": { "cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc." },
  "1": { "cik_str": 789019, "ticker": "MSFT", "title": "MICROSOFT CORP" } }
```

`submissions/CIK0000320193.json` (trimmed). `filings.recent` is **parallel arrays**: index *i* across all arrays is one filing, newest first.

```json
{
  "cik": "320193",
  "name": "Apple Inc.",
  "tickers": ["AAPL"],
  "filings": {
    "recent": {
      "accessionNumber": ["0000320193-25-000079", "0000320193-25-000073"],
      "filingDate":      ["2025-10-31",           "2025-08-01"],
      "reportDate":      ["2025-09-27",           "2025-06-28"],
      "form":            ["10-K",                 "10-Q"],
      "primaryDocument": ["aapl-20250927.htm",    "aapl-20250628.htm"]
    },
    "files": []
  }
}
```

Mapping into `FilingMetadata`:

| Field | Value |
|---|---|
| `Ticker` | the requested ticker, upper-case |
| `CompanyName` | the submissions `name` |
| `Cik` | the 10-digit padded CIK |
| `FormType` | the form |
| `AccessionNumber` | as given |
| `FilingDate`, `ReportDate` | parsed as `DateOnly`. If `reportDate` is empty for a 10-K, fall back to `FilingDate` and log a warning. |
| `FiscalYear` | `ReportDate.Year`. This is a **known simplification** for companies whose fiscal year ends in early January, and for 52/53-week years; document it in the notes. |
| `PrimaryDocument` | as given |
| `SourceUrl` | `EdgarUrls.Document(...)` |

### 6.4 `DownloadFilingsStep`

1. If `Edgar.Enabled == false`: log "download disabled" and return `(Skipped: true, …)`.
2. **Offline-first check.** For each ticker, count the complete pairs (`.htm` + `.metadata.json`) under `DataPath/<TICKER>/<FormType>/`. If **every** ticker already has ≥ `Count`, log `IngestionInit download: skipped (all 5 tickers present)` and return `Skipped = true`. **No network is touched.**
3. Otherwise a download is needed. If `UserAgent` is empty or has no `@`, log a clear error: "Set the SEC User-Agent: `dotnet user-secrets set Parameters:sec-user-agent "Your Name you@example.com" --project src/SECFilingsAgenticRag.AppHost`". Count the missing tickers as Failed and continue (see 5).
4. Fetch `company_tickers.json` once. For each **missing** ticker:
   - fetch its submissions;
   - take the newest `Count` filings with an **exact** `FormType` match (excluding `10-K/A`);
   - write `.htm` (streamed) plus `.metadata.json` via `FilingMetadataJson`;
   - skip filings whose pair already exists.

   One ticker failing (unknown ticker, HTTP error, no network) is logged and counted as Failed, and the run continues.
5. **Never fail the whole init job because of EDGAR.** Log `IngestionInit download: downloaded=5 present=0 failed=0`. If something failed but files exist, log a warning and continue. If nothing exists at all, log a warning; the upload step then uploads nothing, and the init job still exits 0, so the API starts with an empty index. The step never throws for EDGAR problems. Exit code 1 is reserved for the job's own failures, such as a blob upload error.

### 6.5 `UploadFilingsStep`

1. If `DataPath` doesn't exist or contains no `*.htm`, log a **warning**: "No filings found; set the SEC user agent and restart so the download step can fetch them". Return `(0,0,0,0)`. Don't fail.
2. `await container.CreateIfNotExistsAsync()`.
3. For each `*.htm`, recursively:
   - Find the sidecar `<same name>.metadata.json`. If it's missing or unparsable, count it as Invalid, log, and continue.
   - Compute the SHA-256 of the file (`SHA256.HashDataAsync(stream)`, lower-case hex).
   - Set `blobName = FilingBlobConventions.BlobName(metadata, ".htm")`.
   - Get the existing blob's properties:
     - `GetPropertiesAsync()` **throws** `RequestFailedException` with `Status == 404` when the blob doesn't exist, so catch that (or use `ExistsAsync()`) and upload;
     - if `Metadata[contentsha256]` equals the hash (case-insensitive key lookup), count it as Unchanged;
     - otherwise upload.
   - Upload with `blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = new() { ContentType = "text/html" }, Metadata = FilingBlobConventions.ToBlobMetadata(metadata, hash) })`. This overwrites.
4. Log the summary: `IngestionInit upload: scanned=5 uploaded=5 unchanged=0 invalid=0`.

### 6.6 AppHost wiring (add to `AppHost.cs`)

```csharp
var dataPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data", "filings"));
var secUserAgent = builder.AddParameter("sec-user-agent", secret: true);   // personal data → AppHost user-secrets

var ingestionInit = builder.AddProject<Projects.SECFilingsAgenticRag_IngestionInit>("ingestion-init")
    .WithReference(filings)
    .WaitFor(filings)          // verify blob-container resources support WaitFor in Aspire 13, else WaitFor(storage)
    .WithEnvironment("IngestionInit__DataPath", dataPath)
    .WithEnvironment("IngestionInit__Edgar__UserAgent", secUserAgent);
// Nothing waits on this job; indexing is requested by the user (Phase 02).
```

- Set the value **once**: `dotnet user-secrets set "Parameters:sec-user-agent" "Your Name you@example.com" --project src\SECFilingsAgenticRag.AppHost`.
- If the parameter is unset, verify how Aspire behaves (it may prompt in the dashboard, or fail the resource). Use the parameter's optional/default form so a missing value becomes an empty string, and the download step logs the instruction instead. Record what you did.
- The `ingestion-init` resource shows as **Finished** (exit code 0) once it's done. `Projects.SECFilingsAgenticRag_IngestionInit` is generated after `dotnet add src\SECFilingsAgenticRag.AppHost reference src\SECFilingsAgenticRag.IngestionInit`.

## 7. Implementation steps

1. **Add `FilingMetadataJson`** to Common (§6.1). Check: `dotnet build`. Check 2 in §9 verifies the round trip on the real sidecars.
2. **Create the IngestionInit job:** `dotnet new console -o src\SECFilingsAgenticRag.IngestionInit -n SECFilingsAgenticRag.IngestionInit`.
   - Apply the CPM rule (§0).
   - Reference Common and ServiceDefaults.
   - Add `appsettings.json` (§6.2) with `CopyToOutputDirectory`.
   - Implement §6.2, then the EDGAR client (§6.3), `DownloadFilingsStep` (§6.4) and `UploadFilingsStep` (§6.5).

   Check: `dotnet build` with no NU1008.
3. **Wire the AppHost** (§6.6) and set the user-secret. If you're an agent without the user's identity, use a clearly-labeled placeholder (e.g. `"SEC Filings Agent dev@example.com"`) and note it; the user can replace it. Check:
   ```powershell
   aspire start
   aspire wait ingestion-init --status down --timeout 3600
   aspire logs ingestion-init | Select-String 'IngestionInit download:|IngestionInit upload:'
   ```
   Expect `downloaded=5` and `uploaded=5`. Then run `aspire stop`.
4. **Run again.** `aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire logs ingestion-init | Select-String 'IngestionInit download:|IngestionInit upload:'`. Expect `download: skipped` and `uploaded=0 unchanged=5`. Then run `aspire stop`.
6. Write `docs/notes/phase-01-notes.md`, including the `## Commands & operating notes` section: how to set the SEC user-agent secret, how to add tickers, how to force a re-download (delete the ticker's folder).

## 8. Tests

No automated tests in this phase. All unit and integration tests are deferred to [Phase 09](phase-09-tests.md), which specifies this phase's `IngestionInit.Tests` (offline EDGAR fixtures, Testcontainers Azurite). This phase is verified by `dotnet build` and the run-time checks in §9.

## 9. Acceptance criteria

1. **Build.**
   ```powershell
   dotnet build SECFilingsAgenticRag.slnx
   ```
   0 errors, 0 warnings.
2. **First run downloads and uploads** (the user-secret is set):
   ```powershell
   aspire start
   aspire wait ingestion-init --status down --timeout 3600
   aspire describe ingestion-init            # state Finished / exit code 0
   aspire logs ingestion-init | Select-String 'IngestionInit download:|IngestionInit upload:'
   Get-ChildItem data\filings -Recurse -Filter *.htm | Select-Object FullName, Length
   Get-Content data\filings\AAPL\10-K\*.metadata.json
   ```
   Expect:
   - `downloaded=5`, then `uploaded=5`;
   - 5 `.htm` files of 1–15 MB each, each with a sibling `.metadata.json`;
   - `fiscalYear` matches `reportDate`.

   Leave the AppHost running for check 3.
3. **Blobs are visible.** Use the fixed blob port 10000 and the §4 connection string:
   - With the Azure CLI:
     ```powershell
     $cs = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;'
     az storage blob list --connection-string $cs -c filings --include m --query "[].{name:name, ticker:metadata.ticker}" -o table
     ```
     Expect 5 rows named `AAPL/10-K/<accession>.htm`, and so on.
   - Without the Azure CLI, Phase 02 verifies the metadata at run time: the Function reads every blob's metadata with `FromBlobMetadata` and reports a missing key as `Failed`. Record which method you used.
4. **Offline and idempotent second run.**
   ```powershell
   aspire stop; aspire start; aspire wait ingestion-init --status down --timeout 3600
   aspire logs ingestion-init | Select-String 'IngestionInit download:|IngestionInit upload:'
   ```
   Expect `download: skipped` (no SEC request; confirm no `sec.gov` lines in the logs) and `uploaded=0 unchanged=5`.
5. **Changes are detected.**
   ```powershell
   aspire stop
   $f = Get-ChildItem data\filings\AAPL\10-K\*.htm | Select-Object -First 1
   Add-Content $f.FullName '<!-- init job change test -->'
   aspire start; aspire wait ingestion-init --status down --timeout 3600; aspire logs ingestion-init | Select-String 'IngestionInit upload:'
   aspire stop
   ```
   Expect `uploaded=1 unchanged=4`. Afterwards, restore the file: delete `data\filings\AAPL` and restart, so the download step re-fetches it.

## 10. Gotchas

- **The User-Agent must bypass .NET header validation** (`TryAddWithoutValidation`). In PowerShell, pass `-SkipHeaderValidation`.
- **It's personal data.** Keep the SEC User-Agent (your name and email) in AppHost user-secrets, never in a committed file.
- **The download is offline-first.** It skips when the files exist, so demos never depend on sec.gov. To refresh a filing, delete that ticker's folder.
- **Don't let ServiceDefaults' resilience handler retry SEC calls.** It breaks the rate limit. `SecThrottlingHandler` owns retries.
- **Azurite port 10000** is fixed by the AppHost. If another Azurite (Visual Studio's, or a standalone one) holds it, `storage` fails to start.
- **The `filings` container may not exist yet** when other components run. The init job creates it, and Phase 02 treats a missing container as empty.
- **HTTP 403 from SEC** means the User-Agent is missing or malformed. Stay ≤ 10 req/s, or you get temporarily blocked (HTTP 429 or 403).
- `www.sec.gov` serves `company_tickers.json` and the Archives; **`data.sec.gov`** serves submissions. Both need the User-Agent.
- **Exact form match.** `"10-K/A"` is an amendment, often partial, so exclude it.
- **Parallel arrays** in `filings.recent`: never sort one array independently.
- **10-K HTML is large** (inline XBRL, MBs). Stream it to disk.
- **Blob metadata values must be ASCII.** That's why `CompanyName` is URL-encoded by `FilingBlobConventions`.
- **The init job must exit.** Use `StartAsync`/`StopAsync` around the runner, never `RunAsync`. Otherwise `aspire wait ingestion-init --status down` never returns.
- **`builder.AppHostDirectory`** is `src/SECFilingsAgenticRag.AppHost`, so the repo root is `..\..`.
- **`data/filings` is git-ignored.** Never commit SEC documents. The IngestionInit job's download step is the reproducible source.

## 11. Definition of done and handoff

### 11.0 Microsoft Learn evidence log (required)

Your `docs/notes/phase-NN-notes.md` must contain this section. It is the project's audit trail of how official documentation shaped the code, and the user cites it in interviews.

```markdown
## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/... | e.g. exact HeaderChunker options | e.g. MaxTokensPerChunk=512; OverlapTokens ignored → 0 (see §…) |
```

- Record **every** Microsoft Learn page or code sample you used, whether found with `microsoft_docs_search`, `microsoft_code_sample_search` or `microsoft_docs_fetch`. Include the page you verified each preview API against.
- Record where the docs and reality **disagreed** (API renamed, sample outdated) and what you did about it.
- Map design choices to the relevant phase of Microsoft's RAG guide (preparation, chunking, enrichment, embeddings, retrieval, LLM evaluation, agentic RAG) where one applies. See `docs/02-microsoft-guidance-alignment.md`.
- If a fact came from non-Microsoft docs (SEC EDGAR, pgvector), list it too and mark it *external*.

- `dotnet build` is clean (0 errors, 0 warnings).
- `docs/notes/phase-01-notes.md` covers:
  - package versions;
  - the exact Aspire client method used for the blob container;
  - the Azurite workaround, if any;
  - the tickers and accession numbers actually downloaded, and whether the second run skipped the download;
  - deviations;
  - notes for Phase 02 (blob names and the `contentsha256` metadata it relies on; the job never calls indexing).
- the `## Commands & operating notes` section of your phase notes covers setting the `sec-user-agent` user-secret, adding tickers, and forcing a re-download.
- Suggested commit message:
  ```
  Phase 01: IngestionInit init job (offline-first EDGAR download + idempotent upload to Azurite with filing metadata), AppHost wiring
  ```
