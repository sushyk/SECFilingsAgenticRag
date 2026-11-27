# ADR 02: Document preparation and storage

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

RAG needs source documents. Ours are 10-K annual reports from SEC EDGAR, the SEC's public filing system. Before anything can be indexed, the filings have to be fetched from EDGAR and kept in one organized place that the indexing pipeline reads from.

Two decisions follow: **how filings get from EDGAR into the project**, and **which technology holds them**.

The constraints are:

- **SEC fair-access policy.** Every request to `sec.gov` must send a `User-Agent` that identifies the caller by name and email, and stay at or below 10 requests per second. EDGAR is an external dependency; indexing shouldn't fail because EDGAR is slow or unreachable.
- **Few, rarely changing documents.** A handful of companies, one 10-K each per year. Fetching doesn't need to be continuous.
- **Offline demos.** After the first run, the project must work without reaching EDGAR.
- **Citations need metadata.** Company, form type, fiscal year, filing date and source URL must travel with each document so answers can cite it.
- **The same design locally and in production.** Local development runs on a laptop. Production should use the same code with managed authentication, not keys or connection-string secrets.
- **Re-runs are idempotent.** Running acquisition again must not duplicate documents or re-process unchanged ones.

## Considered options

### Acquisition

| Option | Why it is rejected |
|---|---|
| Fetch from EDGAR inside the ingestion pipeline | Couples ingestion to an external API and its rate limit. A failed download fails ingestion, and offline demos are impossible |
| A script run by hand | Not part of the orchestrated environment. Easy to forget, and every developer runs it differently |
| A long-running service that polls EDGAR on a schedule | Over-built for documents that change once a year. A process that never exits gives no clear "documents are ready" signal |
| **An init job that downloads, uploads and exits** | **Chosen** |

### Source document store

| Option | Why it is rejected |
|---|---|
| Local file system only | Has no production equivalent: a cloud indexing host can't read a developer's disk. No per-file metadata, no managed-identity access |
| PostgreSQL (raw HTML in a table) | Mixes raw source documents with the derived search index. The index should be rebuildable from the source, so the two belong in separate stores |
| Amazon S3 / Google Cloud Storage | Capable object stores, but they offer nothing Azure Blob Storage lacks for this project, and they sit outside the ecosystem the rest of the project leans on |
| **Azure Blob Storage** | **Chosen** |

## Decision

### Acquire filings with an init job

Write one console program, **IngestionInit**, that runs as an **init job**: it runs to completion each time the system starts and then exits, like a Kubernetes init container. It does two steps in sequence:

1. **Download.** For each configured ticker, find the latest 10-K on EDGAR and save the main HTML document and a `*.metadata.json` sidecar with the filing's metadata. Skip any filing that is already on disk, so later runs work offline. Every request sends the configured SEC `User-Agent`, and a throttling handler keeps the job under 10 requests per second.
2. **Upload.** Upload each filing to the `filings` blob container, with its metadata and a SHA-256 content hash stored as blob metadata. Upload only files whose hash changed.

The job doesn't trigger indexing. Indexing is a separate, explicit step that reads from the container.

### Store filings in Azure Blob Storage

Azure Blob Storage is the system of record for source documents:

- **Naming.** Blob names follow `{Ticker}/{FormType}/{AccessionNumber}{ext}`, for example `AAPL/10-K/0000320193-25-000079.htm`. The layout on disk is the same, so the local path and the blob name are the same string.
- **Metadata.** Filing metadata lives as blob metadata, so there is no separate catalog database to keep in sync.
- **Local development.** Use **Azurite**, the Azure Storage emulator, run as a local container.
- **Production.** Use an Azure Storage account accessed with **managed identity**. The Azure SDK's `BlobServiceClient` works the same against both; only the connection configuration changes.

Azure Blob Storage is chosen for these reasons:

- **Team experience.** We know the Azure Storage client SDKs well, from prior work on them.
- **Managed identity.** Production access needs no stored secrets: Microsoft Entra ID authenticates the service, and Azure RBAC grants it access to the container.
- **Ecosystem fit.** The project is .NET and leans toward Azure for hosting. The Azure Storage SDK for .NET is first-class, and Azurite provides a local emulator.

## Consequences

- **Ingestion never calls EDGAR.** EDGAR outages and rate limits affect only the init job, and indexing works on whatever is in the container.
- **There is a clear "ready" signal.** The job exits when the documents are staged, so the orchestrator and scripts can wait for it to finish.
- **Re-runs are cheap.** Files already on disk aren't downloaded again, and unchanged files aren't uploaded again. The content hash also lets indexing skip unchanged documents.
- **The index is derived data.** It can be dropped and rebuilt at any time from the container.
- **Local and production use the same code.** Moving from Azurite to a real storage account is a configuration change, plus a role assignment for the managed identity.
- **Adding a document type is a new file, not new plumbing.** Other formats (such as PDF) and hand-made files that follow the naming and sidecar convention are uploaded like downloaded ones.
- **There's one more container to run locally.** Development needs Docker running for Azurite.
- **Blob metadata has limits.** Values must be ASCII strings and the total size is capped, so metadata stays small: identifiers, dates and URLs, not document content.
- **Uploads go through the SDK.** Azurite keeps blobs in its own internal format, so a mounted folder of files doesn't appear as blobs.
- **The SEC `User-Agent` is personal data** (a name and an email). It is configured as a secret parameter, never committed.
- **We're tied to Azure Storage.** Moving to another object store would mean changing the upload step and the indexing reader. Document handling and the naming convention would carry over.

## References

- [Accessing EDGAR data](https://www.sec.gov/os/accessing-edgar-data): the SEC's requirement that automated requests declare a `User-Agent`
- [SEC developer resources](https://www.sec.gov/about/developer-resources): the fair-access limit of 10 requests per second
- [EDGAR application programming interfaces](https://www.sec.gov/search-filings/edgar-application-programming-interfaces): the submissions API that lists a company's filings
- [Azure Blob Storage client library for .NET](https://learn.microsoft.com/azure/storage/blobs/storage-quickstart-blobs-dotnet): `BlobServiceClient`, used against both Azurite and Azure
- [Azurite emulator](https://learn.microsoft.com/azure/storage/common/storage-use-azurite): local Azure Storage emulation
- [Authorize access to blobs with Microsoft Entra ID](https://learn.microsoft.com/azure/storage/blobs/authorize-access-azure-active-directory): identity-based access and Azure RBAC roles
- [Managed identities for Azure resources](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview): authentication without stored secrets
- [Blob properties and metadata](https://learn.microsoft.com/rest/api/storageservices/setting-and-retrieving-properties-and-metadata-for-blob-resources): metadata naming, ASCII values and size limits
