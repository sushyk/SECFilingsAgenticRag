# ADR 04: Vector database

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

The ingestion pipeline ([ADR 03](03-ingestion-pipeline-library.md)) turns each filing into chunks with embeddings. The query side then finds the chunks closest to a question. The vector database stores those chunks and runs that search. Every RAG answer depends on it, so the choice needs to be made before the pipeline is written.

The database must meet these requirements:

- **A Microsoft.Extensions.VectorData provider.** The pipeline writes through VectorData, and the query side will search through `IVectorSearchable<T>`. The database needs a maintained provider, so neither side carries provider-specific code.
- **Metadata filters.** Searches will be narrowed by ticker, form type and fiscal year before ranking.
- **Hybrid search.** Filings are full of exact terms ("Rule 10b5-1", product names) that keyword search finds better than vectors. The database should support keyword search next to vector search.
- **Relational data alongside vectors.** The application will also store relational data, such as conversation sessions. One database for both means one system to run, back up and query.
- **Local-first, with a production path.** It must run in a container on a laptop, and have a managed equivalent for production.
- **Familiar tooling.** It should be debuggable with tools the team already knows (SQL, `psql`, standard database clients), not a new query language or console.

## Considered options

| Option | Why it is rejected |
|---|---|
| Qdrant | A capable dedicated vector database, with a VectorData provider and hybrid search. But it isn't a relational database, so sessions would need a second database. It also brings its own API, query model and console |
| Weaviate | Also a dedicated vector database with a VectorData provider. It has the same drawbacks as Qdrant: no relational store, plus its own API and tooling |
| Pinecone | A managed cloud service, so the default local setup would depend on a hosted account. Learn's list of .NET VectorData providers has no Pinecone provider |
| Other dedicated or search engines (Milvus, Chroma, Redis, Elasticsearch) | Each would be a second system next to the relational database, with its own operations and tooling, and they offer nothing this project needs that PostgreSQL lacks |
| Azure AI Search | A managed cloud service with no local emulator, so it breaks the local-first requirement |
| SQLite | Embedded and simple, but a single-file database isn't a production shape for a service with several hosts writing and reading |
| In-memory store | Useful for tests, but nothing survives a restart |
| **PostgreSQL with pgvector** | **Chosen** |

Dedicated vector databases are built for very large vector workloads. This project indexes a few thousand chunks, so it gains nothing from that specialization, and it would pay for a second system and a second set of tools.

## Decision

Use **PostgreSQL with the pgvector extension** as the vector database, through the .NET Community Toolkit's Microsoft.Extensions.VectorData provider (`CommunityToolkit.VectorData.PgVector`):

| Need | How PostgreSQL meets it |
|---|---|
| Vector search | pgvector stores the embeddings and runs approximate nearest-neighbor search (HNSW index, cosine distance) |
| Metadata filters | The provider translates VectorData filter expressions (`==`, `&&`, `Contains` and others) into SQL `WHERE` clauses on the chunk's metadata columns |
| Hybrid search | The provider implements `IKeywordHybridSearchable<T>` over PostgreSQL full-text search, for a text property marked `IsFullTextIndexed` |
| Relational data | Sessions and other application tables live in the same database as the chunks |
| Local development | The `pgvector/pgvector` container image, which bundles PostgreSQL with the extension |
| Production | A managed PostgreSQL service with the pgvector extension, such as Azure Database for PostgreSQL |

PostgreSQL is chosen for these reasons:

- **It is a relational database first.** Vectors are one more column type, so chunks, metadata and application data share one system and one query language.
- **The ecosystem is mature.** PostgreSQL has decades of operational practice, and pgvector is widely deployed. Managed offerings exist on every major cloud.
- **Team experience.** We know PostgreSQL well, and its tools work unchanged for inspecting the index.

## Consequences

- **One database to run.** Local development adds one container, and production adds one managed service, for both the index and the application's relational data.
- **Retrieval code stays portable.** Writing and searching go through VectorData, so moving to Qdrant or another provider would change registration and schema setup, not the pipeline or the query side.
- **Provider-specific behavior still needs testing.** Filters, hybrid search and distance functions differ between providers. A move would need those re-validated.
- **The vector index is ours to tune.** HNSW settings and the choice of distance function are PostgreSQL configuration, not managed for us. At a few thousand chunks the defaults are enough.
- **Embedding dimensions are fixed in the schema.** Changing the embedding model to one with a different dimension means recreating the table and re-indexing.
- **The built-in hybrid search is fixed in shape.** It uses PostgreSQL's full-text parsing as the provider configures it. If that ranks poorly for filing language, we can write our own keyword SQL behind the same `IKeywordHybridSearchable<T>` contract.
- **We revisit this decision** if the index grows to millions of vectors and search latency suffers, or if the application needs vector features PostgreSQL doesn't provide.

## References

- [Vector databases for .NET AI apps](https://learn.microsoft.com/dotnet/ai/vector-stores/overview): the VectorData abstractions and provider model, and relational databases that support vector search
- [Vector search with VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/vector-search): `IVectorSearchable<T>`, filters and `IKeywordHybridSearchable<T>`
- [Out-of-the-box connectors](https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/): the .NET providers and their maintainers, including Postgres, Qdrant and Weaviate, and no .NET Pinecone provider
- [Postgres connector](https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/postgres-connector): supported filter clauses and hybrid search with `IsFullTextIndexed`
- [CommunityToolkit.VectorData.PgVector on NuGet](https://www.nuget.org/packages/CommunityToolkit.VectorData.PgVector): the provider package
- [pgvector](https://github.com/pgvector/pgvector): vector types, HNSW indexes and distance functions
- [PostgreSQL full-text search](https://www.postgresql.org/docs/current/textsearch.html): the keyword side of hybrid search
- [pgvector container image](https://hub.docker.com/r/pgvector/pgvector): PostgreSQL with pgvector preinstalled, for local development
- [Qdrant](https://qdrant.tech/documentation/), [Weaviate](https://docs.weaviate.io/weaviate) and [Pinecone](https://docs.pinecone.io/guides/get-started/overview): the rejected dedicated vector databases
