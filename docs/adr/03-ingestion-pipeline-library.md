# ADR 03: Ingestion pipeline library

- **Status:** Accepted
- **Date:** 2026-10-29

## Context

RAG answers are only as good as the index behind them. Before any question can be answered, each 10-K filing will go through an ingestion pipeline:

1. **Read** the filing and convert its HTML to a structured document.
2. **Chunk** it into passages small enough to embed and to fit in a prompt.
3. **Process** each chunk: clean it, flag text that looks like a prompt injection, and attach the filing's metadata for citations.
4. **Embed** each chunk and **write** it to the vector store.

The question is which library to build this pipeline on, or whether to build it ourselves. The library must meet these requirements:

- **C# and .NET, in process.** The pipeline must run inside the .NET indexing host, not as a separate service in another language.
- **Official vendor backing and documentation.** The library must be maintained by a vendor that commits to it, and be documented on Microsoft Learn. Learn is the project's authoritative reference for SDK behavior, so a library documented there can be checked against one trusted source.
- **Composes with the stack already chosen.** Embeddings come from Microsoft.Extensions.AI (`IEmbeddingGenerator`), and the index is written through Microsoft.Extensions.VectorData. The pipeline must use those abstractions, not bring a parallel set of its own.
- **Every stage is replaceable.** We need a custom reader for EDGAR's inline-XBRL HTML and, later, a reader for PDFs. We also need structure-aware chunking that keeps "Item 1A. Risk Factors" text together, and our own chunk processors for cleaning and metadata.
- **Token-aware chunk sizes.** Chunk limits are measured in tokens, not characters.
- **Re-runnable.** Re-indexing a filing must replace its chunks, not duplicate them.
- **Local-first.** It must run on a laptop with no hosted service.

## Considered options

| Option | Why it is rejected |
|---|---|
| Python frameworks (LangChain, LlamaIndex, Haystack, Unstructured) | No .NET SDK. Using one would mean a separate Python service behind an HTTP boundary, with its own configuration, telemetry and deployment |
| Azure AI Search indexers with integrated vectorization | A managed cloud service with no local emulator, so it breaks the local-first requirement. It also writes to its own index rather than the vector store we choose |
| Kernel Memory | A Microsoft research project, but not an officially supported Microsoft offering; its README now marks it an archived research project. It has no 1.0 release, and it brings its own storage and memory abstractions instead of Microsoft.Extensions.VectorData |
| Semantic Kernel's `TextChunker` plus our own pipeline | Only a text splitter, and an experimental API. Microsoft Agent Framework supersedes Semantic Kernel ([ADR 01](01-agent-framework-sdk.md)), and we would still write the reader, processor and writer orchestration ourselves |
| LangChain .NET | A community port with no vendor backing and no Microsoft Learn documentation. It ships only prerelease versions, and its document and vector-store abstractions sit alongside Microsoft.Extensions.AI and VectorData instead of using them |
| Smaller community packages (for example Rag.NET, SemanticChunker.NET, DocumentChunker, RAGSharp) | Mostly single-maintainer projects with little adoption and uncertain upkeep. Most cover only chunking, not a whole pipeline |
| Commercial document-chunking libraries (for example Syncfusion's) | A commercial license for what is only the chunking step |
| Our own pipeline on Microsoft.Extensions.AI, VectorData and Microsoft.ML.Tokenizers | Possible, but we would re-implement the reader, chunker, processor and writer orchestration and the re-ingestion handling that a Microsoft library already provides |
| **Microsoft.Extensions.DataIngestion** | **Chosen** |

Most RAG ingestion tooling is Python-first. In .NET, Microsoft.Extensions.DataIngestion is the only option that has vendor backing and Learn documentation, builds on Microsoft.Extensions.AI and VectorData, and covers the whole pipeline.

## Decision

Build the indexing pipeline on **Microsoft.Extensions.DataIngestion**, using its building blocks directly:

| Stage | Building block | Planned use |
|---|---|---|
| Orchestration | `IngestionPipeline<T>` | Runs each filing through the reader, document processors, chunker, chunk processors and writer, and reports a per-document result |
| Read | `IngestionDocumentReader` | A custom reader for EDGAR HTML that prepares the HTML, converts it to Markdown and hands it to the Markdig-based `MarkdownReader`. A PDF reader is added later as another reader |
| Chunk | `HeaderChunker` with `IngestionChunkerOptions` | Splits on Markdown headings, so each chunk stays inside one Item and records it as its section. Token limits use a Microsoft.ML.Tokenizers tokenizer |
| Process | `IngestionChunkProcessor<T>` | Custom processors that clean each chunk, flag possible prompt injection and attach the filing's metadata |
| Embed and write | `VectorStoreWriter<T>` | Writes chunks to the Microsoft.Extensions.VectorData store, whose configured `IEmbeddingGenerator` embeds them. A document's earlier chunks are replaced when it is ingested again |

The library also ships LLM enrichers (`KeywordEnricher`, `SummaryEnricher`, `ClassificationEnricher`, `SentimentEnricher`, `ImageAlternativeTextEnricher`). None are needed for the first version, but they are the planned route for adding keywords or summaries to chunks later, with no change to the pipeline's shape.

## Consequences

- **Less plumbing to own.** The project writes only what is specific to SEC filings: HTML preparation, chunk cleaning and filing metadata. Orchestration, chunking, embedding and writing come from the library.
- **One set of abstractions.** The pipeline embeds through `IEmbeddingGenerator` and writes through VectorData, the same abstractions the query side uses. Swapping the embedding model or the vector database doesn't touch the pipeline.
- **Every stage is replaceable.** New formats are new readers, and new chunk treatments are new processors. Third-party chunkers that implement `IngestionChunker<T>` can be plugged in if the built-in one falls short.
- **The library is in preview.** APIs can change between releases, so every change verifies the APIs it uses against Microsoft Learn before writing code, and package upgrades are deliberate.
- **The pipeline reads files, not streams.** Filings are stored as blobs, so the indexing host stages them to a temporary folder before processing.
- **Documents carry no free-form metadata.** `IngestionDocument` has an identifier and sections only, so a final chunk processor attaches the filing metadata that citations need.
- **No built-in reader handles EDGAR HTML.** The MarkItDown readers need an external MarkItDown tool, and no reader removes inline-XBRL markup or promotes "Item" headings. A custom reader does this before handing Markdown to `MarkdownReader`.
- **We revisit this decision** if the library stalls in preview or is abandoned, or if we need something it can't express, such as a streaming source or a chunking strategy no `IngestionChunker<T>` can provide.

## References

- [Data ingestion in .NET](https://learn.microsoft.com/dotnet/ai/conceptual/data-ingestion): the pipeline's readers, processors, chunkers, enrichers and writers
- [Microsoft.Extensions.DataIngestion API reference](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dataingestion): `IngestionPipeline<T>`, `HeaderChunker`, `VectorStoreWriter<T>`, `IngestionDocument` and the enrichers
- [Introducing data ingestion building blocks (preview)](https://devblogs.microsoft.com/dotnet/introducing-data-ingestion-building-blocks-preview/): the library's announcement and preview status
- [Microsoft.Extensions.DataIngestion on NuGet](https://www.nuget.org/packages/Microsoft.Extensions.DataIngestion): preview-only releases
- [Microsoft.ML.Tokenizers](https://learn.microsoft.com/dotnet/api/microsoft.ml.tokenizers): token counting for chunk limits
- [Azure AI Search integrated vectorization](https://learn.microsoft.com/azure/search/vector-search-integrated-vectorization): the managed indexer option
- [Kernel Memory repository](https://github.com/microsoft/kernel-memory) and [package](https://www.nuget.org/packages/Microsoft.KernelMemory): "not an officially supported Microsoft offering", archived research project, no 1.0 release
- [Semantic Kernel `TextChunker`](https://learn.microsoft.com/dotnet/api/microsoft.semantickernel.text.textchunker): a text splitter only; its source marks it `[Experimental("SKEXP0050")]`
- [LangChain .NET repository](https://github.com/tryAGI/LangChain) and [package](https://www.nuget.org/packages/LangChain): a community port with prerelease-only versions
- [Rag.NET](https://github.com/Narendersingh007/Rag.NET), [SemanticChunker.NET](https://www.nuget.org/packages/SemanticChunker.NET), [DocumentChunker](https://www.nuget.org/packages/documentchunker) and [RAGSharp](https://www.nuget.org/packages/RAGSharp/): smaller community packages
- [Syncfusion .NET Document Chunking Library](https://www.syncfusion.com/blogs/post/dotnet-document-chunking-library): a commercial chunking library
- [PinkRooster.Extensions.DataIngestion](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion): a third-party chunker built on the library's `IngestionChunker<T>`
- [LangChain](https://docs.langchain.com/oss/python/langchain/overview), [LlamaIndex](https://developers.llamaindex.ai/python/framework/), [Haystack](https://haystack.deepset.ai/) and [Unstructured](https://docs.unstructured.io/): the Python-first alternatives
