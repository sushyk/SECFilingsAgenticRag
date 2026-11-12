# Phase 06 notes: Retrieval quality (hybrid, rerank, multi-query) and evaluations

## What was built
- `src/SECFilingsAgenticRag.RetrievalPipeline`:
  - `RetrievalOptions` gains:
    - the nested `Rerank` (`Enabled`, `CandidatePool` 20, `Model?`), `MultiQuery` (`Enabled`, `Variants` 3, `Model?`) and `Hybrid` (`CandidatePool` 40, `RrfK` 60) options;
    - the `HybridStrategy` constant;
    - `Describe()`, which returns `Vector`, `Hybrid+Rerank`, `Hybrid+MultiQuery+Rerank` and so on.
  - `Fusion/RrfFusion.Fuse(rankings, k)`: `Σ 1/(k + rank)` over lists of records, deduplicated by `Key`. Ties go to the better single rank, then to first-seen order.
  - `Hybrid/PostgresHybridSearch : IVectorSearchable<FilingChunkRecord>, IKeywordHybridSearchable<FilingChunkRecord>`. It has no schema code of its own: the full-text index comes from the connector (see Common below).
  - `Rerank/RerankingVectorSearch` and `MultiQuery/MultiQueryVectorSearch`: decorators on the built-in interface that use MEAI structured output (`GetResponseAsync<Ranking>` / `<QueryVariants>`).
  - `StructuredOutput` (internal, shared by both decorators): tries schema mode, then `useJsonSchemaResponseFormat: false`, then returns `null` (the caller keeps the original order or the original query). It uses `ChatResponse<T>.TryGetResult`, so a bad reply is logged at Debug with its raw text.
  - `AddSecFilingsRetrieval` registers:
    - keyed `"Vector"` (the collection) and keyed `"Hybrid"`;
    - the unkeyed composition `keyed(Strategy) → [MultiQuery] → [Rerank]`. A dedicated chat client is created when `Rerank:Model` or `MultiQuery:Model` is set.
  - `FilingSearch` sets the span tag `rag.strategy` to `Describe()`.
- `src/SECFilingsAgenticRag.Common`:
  - `AI/ChatClientFactory.Create(services, configuration, model)`: the one Ollama chat-client pipeline. It uses a dedicated `HttpClient` with a 10-minute timeout, an optional `num_ctx` from `AI:Chat:ContextLength`, `UseLogging` and `UseOpenTelemetry`. `AddSecFilingsAI` registers its chat client through it.
  - `FilingChunkRecord.Text` is now `IsFullTextIndexed = true`, so the built-in `EnsureCollectionExistsAsync` (AgentServer startup) creates the GIN index that hybrid search uses.
  - `FilingChunkRecord.KeyStorageName` / `TextStorageName` / `DocumentIdStorageName`: the record attributes use these constants, and raw SQL refers to them instead of repeating `"key"`, `"content"` and `"documentid"`.
- `src/SECFilingsAgenticRag.AgentServer`:
  - `POST /api/search` reports and logs `strategy = Describe()`.
  - `FilingTools` uses the `documentid` constant.
  - `appsettings.json` sets `AI:Chat:ContextLength: 8192`.
- `evals/SECFilingsAgenticRag.Evals` (a console app on the generic host):
  - commands `dataset-helper`, `retrieval` and `answers`;
  - `datasets/golden.json` (25 questions);
  - `EvalOptions` / `JudgeOptions`, `Dataset/{GoldenDataset, Corpus}`, `Retrieval/{RetrievalMetrics, RetrievalMatrixRunner}`, `Answers/{RecordingVectorSearch, AnswerEvalRunner}`, `Hosting/EvalServiceFactory`, `Reporting/MarkdownReport`.
- AppHost: `evals-retrieval` and `evals-answers` project resources (`WithArgs`, `WithReference(db)`, `WithReference(ollama)`, `AI__Chat__Model`, `WithExplicitStart()`).
- Other files:
  - `dotnet-tools.json`: local tool manifest with `Microsoft.Extensions.AI.Evaluation.Console` 10.11.0;
  - `.gitignore`: `evals/results/*`, except `.gitkeep` and `sample-*.md`;
  - `evals/results/.gitkeep` and `evals/results/sample-retrieval.md`.

## Table and column names (real)
- Table: `public."sec_filing_chunks"` (`FilingChunkRecord.CollectionName`; Phase 03 deviation 1: there is no `FilingChunkSchema`).
- Columns used by raw SQL:
  - `"key"`, `"content"` and `"documentid"`, through the new `FilingChunkRecord.*StorageName` constants;
  - `"containsinstructionliketext"`, `"ticker"`, `"section"` and `"chunkindex"`, through `FilingChunkMetadataKeys`.
- Added by this phase: the GIN index `sec_filing_chunks_content_index … USING gin (to_tsvector('english'::regconfig, content))`. It is created **by the connector** from `IsFullTextIndexed = true` (verified in `pg_indexes`).

## Where the full-text index comes from, and why
- It comes from the built-in connector. `FilingChunkRecord.Text` has `IsFullTextIndexed = true`, and AgentServer's startup `EnsureCollectionExistsAsync()` issues `CREATE INDEX IF NOT EXISTS` on the existing table. That is the same path that added the btree indexes in Phase 03.
- It is an **expression** index, so rows indexed before this phase are covered with **no re-index**. The MEDI writer and `IngestionPipeline` are untouched (Open/Closed).
- The keyword SQL repeats the index expression `to_tsvector('english', "content")` inline in `WHERE`, which is what lets Postgres use it. `EXPLAIN` with `enable_seqscan = off` shows `Bitmap Index Scan on sec_filing_chunks_content_index`; at 824 rows the planner normally prefers a seq scan.
- **First version, replaced:** a hand-written lazy migration (a generated `search_tsv` column, its own GIN index and a `SemaphoreSlim`). It worked: 824 of 824 rows were backfilled and two starts ran idempotently. When you asked why `IsFullTextIndexed` wasn't used, I switched, because the built-in meets the same need. The migration code is deleted.
- The evals never call `EnsureCollectionExistsAsync`. If AgentServer has never started against a database, keyword search there is slower (it computes `to_tsvector` per row), but not wrong.

## How hybrid search works
1. **Vector side:** the collection's own `SearchAsync(text, pool, { Filter })`, so the keyed query generator adds the `search_query: ` prefix and MEVD translates the filter.
2. **Keyword side:** `WHERE to_tsvector('english', "content") @@ CAST(replace(plainto_tsquery('english', @query)::text, '&', '|') AS tsquery)`, ordered by `ts_rank_cd`. The question's stemmed terms, without stop words, are **OR**ed (see "Why a custom hybrid search"). It returns **keys only**, in rank order, with no `LIMIT`.
3. **Filtering in the database:** the ranked keys are loaded in batches of `pool`, best first, through the built-in `collection.GetAsync(r => batch.Contains(r.Key) && filter, …)`. The connector translates both the key list and the caller's filter into SQL over the indexed columns, as captured in the Npgsql log: `WHERE ("key" = ANY ($1) AND ("ticker" = ANY ($2) AND (NOT "containsinstructionliketext")))`. It also maps the rows. Loading stops once `pool` filtered hits exist, and each batch is put back into rank order. The two expressions are combined with `FilingFilters.And` (now `internal`), which rebinds the parameter. Flagged chunks are excluded by the filter that `FilingFilters` builds, on both sides.
4. **Fusion:** RRF over the two lists, keeping `top`. Scores are about 0.016-0.033, never comparable to cosine.

`HybridSearchAsync(searchValue, keywords, …)` uses `string.Join(' ', keywords)` for the keyword side.

**Cost (measured):** with OR semantics most chunks match a natural-language question, and `ts_rank_cd` re-tokenizes every matching chunk, because the connector's index is an expression index with no stored tsvector. The keyword query takes **≈500-600 ms** on 824 chunks, and a hybrid search ≈0.8-1.4 s end to end. The replaced first version, with a stored generated column, took ≈60 ms. It grows with the corpus. Options if it matters: bring back a stored tsvector column (ranking reads it), or cap the number of ranked matches. That decision is open.

## Structured output: which mode works (measured)
Tested with Ollama 0.40.16 on CPU, using the real prompts.

| Model | Rerank (`Ranking`) | Multi-query (`QueryVariants`) |
|---|---|---|
| `llama3.2` | **Schema mode works**: `Ranking parsed (JSON schema: True)` on every logged call | **Schema mode works** |
| `qwen3:4b` (repo default) | **Neither mode works.** Its reasoning goes to Ollama's `thinking` field and uses up the whole output budget, so `content` is empty. That happened at `MaxOutputTokens` 200, and again at 1000 in both schema and prompt mode. One call took 434 s and fell back to the original order, as designed | not measured (same reasoning behaviour) |

**Decision:** keep `qwen3:4b` as the chat default, and recommend `Retrieval__Rerank__Model=llama3.2` (and `Retrieval__MultiQuery__Model=llama3.2`). That is the purpose of the optional model overrides. All eval runs below used them. `MaxOutputTokens` is 1000, not the spec's 200 (deviation 6).

## Ollama silently truncated long prompts (found here; affects Phases 04-05)
- The first `+Rerank` run lost hits that plain Vector had found. Ollama's `server.log` showed `msg="truncating input prompt" limit=2050 prompt=4132 keep=4`: Ollama keeps the last ~2k tokens and drops the start. The start is where the system instructions and the question are, so the model ranked passages without knowing the question.
- The fix is in `ChatClientFactory`: a built-in `ConfigureOptions` middleware adds OllamaSharp's `OllamaOption.NumCtx` from `AI:Chat:ContextLength` (8192 in the AgentServer and evals `appsettings.json`). It applies to every Ollama chat client: agents, reranker, rewriter and an Ollama judge. After the fix, the log shows `n_ctx_slot = 8192, task.n_tokens = 4132` and no truncation.
- **Implication for earlier phases:** standard-rag's Sources block (5 excerpts, ~2.5k tokens per Phase 04) and agentic-rag's growing tool transcript were over that default window, so their system prompts were probably truncated too. Phase 04/05 answers were not re-measured. Re-run their CLI checks when convenient; Phase 07's token budget should use the same `ContextLength`.

## Reranker quality with a 3B local model (measured)
- Even with the full prompt, `llama3.2` reranks poorly. On `aapl-iphone-sales-01` the vector candidates had the answer at rank 2, and the model returned `[14, 13, 18, 17, 20, 15, 12, 11, 10, 9, …]`: a near-reversed list with a strong recency bias toward the last passages.
- The 700-character truncation (per spec) also hides evidence that sits later in a chunk ("higher net sales of Pro models" is beyond character 700 in that chunk).
- The decorator does exactly what the model says (verified with a scratch probe that printed the candidate positions), so this is a model limitation, not a mapping bug.
- Learn's retrieval guide says to benchmark rerankers against your own queries before adopting one, and this is the evidence: **leave `Rerank:Enabled=false` with local 3-4B models**.
- Future work: a cross-encoder ONNX reranker, or a pointwise scoring prompt; see "Known limitations".

## Why a custom hybrid search (the spec's premise was wrong)
- The spec says the PgVector connector doesn't implement `IKeywordHybridSearchable`. **It does.** Learn's Postgres connector page lists "HybridSearch supported? Yes, requires a string data property with `IsFullTextIndexed = true`" (a GIN index on `to_tsvector`). The installed `CommunityToolkit.VectorData.PgVector` 1.0.2 has `PostgresCollection.HybridSearchAsync`.
- I checked its SQL by capturing the command: one statement that does vector `RANK()` plus `ts_rank_cd(to_tsvector('english', "content"), plainto_tsquery('english', $keywords))`, with the filter translated into SQL and RRF computed in SQL.
- `plainto_tsquery` **ANDs every term**, and so did this phase's first version (`websearch_to_tsquery`). With the golden questions passed as keywords, only **4 of 25** matched any chunk at all, so the keyword side was nearly inert: AND-hybrid's gain over Vector came from those 4.
- **Decision (yours, when asked):** keep `PostgresHybridSearch` and OR the terms. "No built-in fits" now has a concrete reason: the built-in can only AND keywords.
- Measured after the change: Hybrid ties AND-hybrid (Hit@5 0.64 and MRR 0.46 in both; Hit@10 0.76 vs 0.80, which is one question; see Results).
- Alternative left open: the built-in `HybridSearchAsync` plus **real keywords**, for example an optional `keywords` argument on agentic-rag's `search_filings` tool for exact terms like "Rule 10b5-1". That would delete the keyword SQL, at the cost of agent-side keyword quality.

## Results (acceptance criteria, §9)
**How they were run.**
- Machine: CPU-only, native Ollama 0.40.16, the persistent Postgres container (824 chunks from Phase 02).
- AgentServer checks ran outside Aspire with `dotnet run` and `ConnectionStrings__*` set by hand (spec fallback, see deviation 18). Evals ran with `dotnet run --project evals/SECFilingsAgenticRag.Evals`, with `ConnectionStrings__secfilings` set as an environment variable.
- `Retrieval__Rerank__Model=llama3.2` and `Retrieval__MultiQuery__Model=llama3.2` were set for every run. For the answers run, `AI__Chat__Model=llama3.2`, `Evals__Judge__Provider=Ollama` and `Evals__Judge__Model=llama3.2` were also set, because there is no cloud key.

| # | Check | Result |
|---|---|---|
| 1 | `dotnet build SECFilingsAgenticRag.slnx` | **Pass**: 0 errors; 4 warnings, all the pre-existing `NU1903` on `Aspire.Hosting` / `.AppHost` 13.6.4 (Phase 04) |
| 2 | `dataset-helper --ticker AAPL` | **Pass**: one `## AAPL/10-K/0000320193-25-000079` block listing its 24 sections with chunk counts (e.g. `Item 1A. Risk Factors (24 chunks)`, `Item 8 … (40 chunks)`) and 2 snippets each |
| 3 | `retrieval` (default configs plus `Hybrid+MultiQuery`) | **Pass**: table printed and `evals/results/<ts>-retrieval.md` written; matrix below; `Describe()` asserted per variant |
| 4 | `answers --agent standard-rag,agentic-rag --limit 5` | **Pass (mechanics); scores indicative only**: per-mode averages, per-question rows and a `Tool use` section; `evals/results/20261112-174101-answers.md` plus the disk store. Most Relevance/Groundedness values are `n/a`; see "Answer evals" |
| 5 | `dotnet aieval report --path evals/results/store --output evals/results/report.html` | **Pass**: `report.html` (1.1 MB) for execution `answers-20261112-155944` |
| 6 | `Retrieval:Strategy=Hybrid` → `/api/search` reports `Hybrid`; `Rerank:Enabled=true` → `…+Rerank` | **Pass**, configuration only: `strategy=Hybrid` for `{"query":"Item 1A risk factors","top":3}`; `Hybrid+Rerank` and `Hybrid+MultiQuery+Rerank` with the corresponding env vars. Run outside Aspire (deviation 18) |
| 7 | Judge override via env; header shows the judge | **Pass**: with `Evals__Judge__Provider=Ollama` / `Evals__Judge__Model=llama3.2` (appsettings says OpenAICompatible / its cloud default), the header reads `judge: Ollama llama3.2` |
| 7 | Judge model missing → startup failure | **Pass**: `Evals__Judge__Model=""` → `OptionsValidationException: DataAnnotation validation failed for 'JudgeOptions' members: 'Model' with the error: 'The Model field is required.'` (an empty value is treated like a removed key) |
| 8 | `golden.json` ≥ 20 questions, all 5 tickers, every `documentId` exists | **Pass**: 25 questions (21 single-company, 4 cross-company), AAPL/AMZN/MSFT/NVDA/TSLA. The loader validates document ids against `SELECT DISTINCT documentid` and stops with the list of unknown ids. All 29 expectations also matched at least one chunk in a SQL check |
| 9 | Hybrid on a database indexed before this phase, no re-index | **Pass**: AgentServer startup (`EnsureCollectionExistsAsync`) created `sec_filing_chunks_content_index` (GIN on `to_tsvector('english', content)`, an expression index over the existing Phase 02 rows); hybrid search then answered with `strategy=Hybrid`. The replaced first version also passed (generated column, 824/824 rows backfilled) |
| — | Step 5 rare exact term | **Pass**: `trading plan to sell up to 64,059 shares` puts the AMZN `Item 9B` chunk (the only one that contains it) at rank 1 with `tickers=["AMZN"]` (final code, filter in SQL). Unfiltered it ranks 1st with AND terms and 2nd with OR terms, behind MSFT's trading-plan chunk |
| — | Step 4 schema step twice | **Pass**: the index comes from `CREATE INDEX IF NOT EXISTS` in the built-in `EnsureCollectionExistsAsync`, which runs on every AgentServer start. The first version's migration also ran twice without error |
| — | Model-name grep (`qwen\|nomic\|llama\d\|llama:\|gpt-`, Phase 00 deviation 4 pattern) | No hits in this phase's code. Remaining hits are pre-existing: `EmbeddingModelConstants.cs` (Phase 00) and an `AgentHostingExtensions.cs` comment (Phase 05) |

### Retrieval matrix (final run, OR-hybrid)
Not re-run: you stopped the evaluation after the OR change and the switch to database-side filtering. The handoff is in "How to run the evaluation (handoff)" below. A partial run with OR terms but the in-memory filter (Vector, Hybrid, Hybrid+MultiQuery only) gave Vector 0.60 / 0.76 / 0.40, Hybrid 0.64 / 0.76 / 0.46 and Hybrid+MultiQuery 0.52 / 0.64 / 0.30 (Hit@5 / Hit@10 / MRR@10). `evals/results/sample-retrieval.md` is the full AND-version run below, so replace it after your own run.

Earlier run, with the first version (AND keyword side, before the OR change; all other code identical):

| Config | Hit@5 | Hit@10 | MRR@10 | Recall@5 | Recall@10 | p50 ms | p95 ms |
|---|---|---|---|---|---|---|---|
| Vector | 0.60 | 0.76 | 0.40 | 0.56 | 0.72 | 50 | 78 |
| Hybrid (AND) | 0.64 | 0.80 | 0.46 | 0.60 | 0.76 | 59 | 122 |
| Hybrid+MultiQuery | 0.44 | 0.60 | 0.27 | 0.42 | 0.58 | 4082 | 6109 |
| Vector+Rerank | 0.24 | 0.32 | 0.16 | 0.22 | 0.28 | 60741 | 187022 |
| Hybrid+Rerank | 0.28 | 0.36 | 0.20 | 0.26 | 0.32 | 75438 | 223212 |

**Reading it:**
- **Hybrid ≥ Vector.** MRR goes from 0.40 to 0.46 (relevant chunks move up). Hit@10 differs by at most one question.
- **Multi-query hurts with `llama3.2` as the rewriter.** Its "search-style" variants drift: with 3 rewrites plus the original, the original question's hits are outvoted in RRF.
- **Rerank hurts badly and costs about 1 minute per query:** a near-reversed ranking from a 3B model (see above).
- **The consistent misses are headcount questions** (`*-employees-01`, `cross-employees-*`). The number sits in one Human Capital paragraph inside a long `Item 1. Business` chunk, which neither embeddings nor full-text rank highly for "how many employees". The fix belongs to chunking or enrichment (Phase 14: section summaries or keywords), not ranking.
- With 25 questions, ±0.04 is one question (spec §10): only the MRR gain and the rerank and multi-query losses are clearly beyond noise.

### Answer evals (`--limit 5`, `llama3.2` agents, `llama3.2` judge)
| Agent | Relevance | Groundedness | Completeness | Avg tool calls | Cited % | Avg s |
|---|---|---|---|---|---|---|
| standard-rag | n/a | 4.00 | 3.40 | 0.00 | 100 | 77 |
| agentic-rag | 4.00 | 4.00 | 3.33 | 1.00 | 100 | 79 |

**Tool use (agentic-rag): 4 of 5 answers pass every check.**

| Check | Pass rate | Failing |
|---|---|---|
| `tool_called_check` | 5/5 | |
| `tool_call_args_match` | 5/5 | |
| `citations_faithful` | 4/5 | `aapl-employees-01`: cited a tag the tools never returned |

- **Why most judge scores are `n/a`:** the result store's diagnostics read `Failed to parse numeric score for 'Relevance' from the following text: Score`. `llama3.2` writes `Score: 5` instead of the evaluators' expected output format, so MEAI records no value. That isn't a code defect. Learn's evaluator pages say these prompts are tuned for GPT-4o and "can be especially poor when a smaller / local model is used". Averages only cover the scores that parsed, so treat them as indicative.
- **To get real numbers:** set the OpenAICompatible judge's key (see "Commands") and re-run. The judge cache makes a re-run of unchanged answers free.

## How to run the evaluation (handoff)
Not run to completion in this phase (your call). Everything below is ready to use. Run from the repo root in PowerShell 7.3+, with Docker Desktop, native Ollama (`ollama pull llama3.2`) and an indexed corpus.

1. **Build first.** Don't build while an eval, AppHost or AgentServer runs: the DLLs are locked.
   ```powershell
   dotnet build SECFilingsAgenticRag.slnx
   ```
2. **Make sure the full-text index exists.** It is created by AgentServer's startup `EnsureCollectionExistsAsync`. Start the API once, or check:
   ```powershell
   $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
   "SELECT indexname FROM pg_indexes WHERE indexname = 'sec_filing_chunks_content_index';" | docker exec -i $pg sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d secfilings -A -t'
   ```
3. **Connection string:** see "Commands & operating notes". Use user-secrets, or `$env:ConnectionStrings__secfilings`.
4. **Retrieval matrix.** The first two configurations take seconds; each `+Rerank` takes about 30 minutes on CPU. `qwen3:4b` can't rerank, so point the reranker at `llama3.2`.
   ```powershell
   $env:Retrieval__Rerank__Model = "llama3.2"; $env:Retrieval__MultiQuery__Model = "llama3.2"
   dotnet run --project evals/SECFilingsAgenticRag.Evals -- retrieval --configs "Vector,Hybrid,Hybrid+MultiQuery,Vector+Rerank,Hybrid+Rerank"
   Copy-Item (Get-ChildItem evals/results/*-retrieval.md | Sort-Object LastWriteTime | Select-Object -Last 1) evals/results/sample-retrieval.md
   ```
   Then paste the table into "Retrieval matrix" above. Expect Hybrid ≈ Vector on Hit@k with a better MRR, and `+Rerank` far worse with 3B models.
5. **Answer quality and tool use.** A cloud judge gives real scores; a local judge mostly gives `n/a` (it can't produce the score format).
   ```powershell
   dotnet user-secrets --project evals/SECFilingsAgenticRag.Evals set "Evals:Judge:ApiKey" "<token>"   # OpenAICompatible judge (default)
   $env:AI__Chat__Model = "llama3.2"                                                                     # agents under test; qwen3:4b takes ~10x longer
   dotnet run --project evals/SECFilingsAgenticRag.Evals -- answers --agent standard-rag,agentic-rag --limit 5
   dotnet aieval report --path evals/results/store --output evals/results/report.html --open
   ```
   To use a local judge instead: `$env:Evals__Judge__Provider = "Ollama"; $env:Evals__Judge__Model = "llama3.2"`.
6. **Remaining acceptance checks:**
   - Override check: `$env:Evals__Judge__Model = "<other>"; … answers --limit 1`. The report header must show that model.
   - Strategy check: `Retrieval__Strategy=Hybrid` / `Retrieval__Rerank__Enabled=true` on the API, then `/api/search`'s `strategy` (command under "Commands").
   - Under Aspire: `aspire resource evals-retrieval start`, then `aspire logs evals-retrieval`. Not yet exercised.
7. **Record the results** in this file: the matrix, the answer table, and the "Reading it" bullets if they change.

## Package versions added
| Package | Version | Why this one |
|---|---|---|
| `Microsoft.Extensions.AI.Evaluation.Quality` | 10.11.0 | 11.0.0 is the latest stable, but `Microsoft.Agents.AI` 1.28 and the repo pin `Microsoft.Extensions.AI` 10.11.0; 11.x would move MEAI under every project |
| `Microsoft.Extensions.AI.Evaluation.Reporting` | 10.11.0 | same |
| `Microsoft.Extensions.AI.OpenAI` | 10.11.0 | OpenAICompatible judge; matches MEAI |
| tool `Microsoft.Extensions.AI.Evaluation.Console` (`dotnet aieval`) | 10.11.0 | must match the reporting library's store format |

`Microsoft.Extensions.AI.Evaluation` itself comes transitively, and so do `Npgsql`/`Pgvector` (through Common). `Microsoft.Extensions.Hosting` (10.0.14) was already in CPM. No diagnostics are suppressed.

## Custom types and why no built-in fits
| Type | Built-in considered | Why custom |
|---|---|---|
| `PostgresHybridSearch` | `PostgresCollection.HybridSearchAsync` (`IKeywordHybridSearchable`, **exists**) | The built-in ANDs keywords (`plainto_tsquery`), which matched 4/25 golden questions; this one ORs them (section above). The vector side **uses** the built-in collection search, and keyword hydration **uses** the built-in `GetAsync(keys)` |
| `RrfFusion` | — | MEVD exposes no fusion helper; shared by hybrid and multi-query |
| `RerankingVectorSearch`, `MultiQueryVectorSearch` | MEAI `GetResponseAsync<T>` (**used**) | MEVD has no rerank or query-rewrite decorators |
| `StructuredOutput` (internal) | `ChatResponse<T>.TryGetResult` (**used**) | Schema-then-prompt fallback shared by both decorators |
| `ChatClientFactory` | `AddChatClient` / `AddKeyedChatClient` (**used** for the judge registration) | One Ollama pipeline for several models (rerank, rewrite, judge) without a second container registration per model |
| `RelevanceRule` → `Expectation.IsSatisfiedBy`, `RetrievalMetrics`, `RetrievalMatrixRunner`, `MarkdownReport` | MEAI.Evaluation, MAF evaluation | Neither ships Hit@k, MRR, Recall@k or a configuration-matrix report |
| `RecordingVectorSearch` (evals only) | — | Nothing built in captures what was retrieved, which the Groundedness context needs |
| `GoldenDataset` + records, `Corpus` | MEVD `GetAsync(filter, top)` | Dataset shape is ours; DISTINCT / GROUP BY need SQL |
| `EvalOptions`, `JudgeOptions` | Options pattern (**used**) | Our configuration |
| *(not added)* `FilingChunkTableInfo`, `HybridSchemaInitializer`, reranker/rewriter interfaces, JSON helper, custom cache | storage-name constants; `IsFullTextIndexed` + `EnsureCollectionExistsAsync`; decorators; `GetResponseAsync<T>`; the reporting library's response cache | Built-ins cover them |

## Deviations from the spec
| # | Spec | What I did | Why |
|---|------|-----------|-----|
| 1 | `FilingChunkSchema`, `EmbeddingKeys`, `ChatModelOptions`, `AI:Embedding`, `WithAIModels`, table `filing_chunks_nomic_embed_text_768` | `FilingChunkRecord.CollectionName` (`sec_filing_chunks`), `EmbeddingModelConstants`, `AI:Chat:Model` read directly, `.WithEnvironment("AI__Chat__Model", chatModel)` | Code on disk wins (Phase 00 simplification; Phase 02/03 deviation 1) |
| 2 | "PgVector connector doesn't implement `IKeywordHybridSearchable`" | It does (Learn + package). Custom hybrid kept, keyword side ORed; justification changed | Learn wins on the fact; the design choice was yours (section above) |
| 3 | `FilingChunkTableInfo` (names from the collection definition), `HybridSchemaInitializer` with a generated `search_tsv` column | `FilingChunkRecord.*StorageName` constants used by the record's attributes and the SQL; **no migration**: `IsFullTextIndexed = true` on `Text`, so the connector creates a GIN expression index | The record maps with attributes, so the constants are the single source of the names. The connector already owns full-text indexing (Learn row 1), and an expression index needs no backfill |
| 4 | Keyword query returns full scalar rows and maps them | Returns keys; records loaded through the built-in `collection.GetAsync(filter)` | No hand-written row mapping; the filter runs in SQL |
| 5 | Keyword SQL with `@tickers/@forms/@years` parameters; filter compiled in memory over an over-fetch window | Neither: the keyword SQL ranks keys, and the connector applies the caller's filter in SQL while loading them (`GetAsync(keys.Contains && filter)`) | The filter is a LINQ expression that only the connector can translate; this keeps every filter in the database without parsing the expression or duplicating `FilingFilters`. You asked for database-side filtering |
| 6 | Rerank `MaxOutputTokens = 200` | 1000 | Measured: reasoning models used all 200 on thinking and returned empty content |
| 7 | `RrfFusion.Fuse(IEnumerable<IReadOnlyList<Guid>>) → (Guid, double)` | `Fuse(IEnumerable<IReadOnlyList<FilingChunkRecord>>) → VectorSearchResult` | Both callers needed "keep first-seen record per key" around it; same math, ties and determinism |
| 8 | Optional `UseDistributedCache` on the decorators | Not added | Optional; every eval variant gets a fresh provider and the prompts differ per configuration, so the hit rate would be about 0 |
| 9 | — | New optional `AI:Chat:ContextLength` (8192) applied as Ollama `num_ctx` | Ollama silently truncated the rerank prompt (section above) |
| 10 | `ChatClientFactory.Create(ChatModelOptions, ollamaEndpoint, HttpClient?)` | `Create(IServiceProvider, IConfiguration, model)` | No `ChatModelOptions` on disk; Common is Ollama-only. The OpenAICompatible judge is built in the evals (`JudgeOptions.CreateClient`), its only consumer |
| 11 | `Evals:Judge:Endpoint` for every provider | OpenAICompatible only; an Ollama judge uses `ConnectionStrings:ollama` | appsettings carries the cloud endpoint, so switching the provider to Ollama would otherwise also need an endpoint override |
| 12 | `ResultsPath` "relative to the project dir" | Content root = project directory (baked in with `<AssemblyMetadata Include="ProjectDirectory">`); both paths are made absolute in `PostConfigure` | `dotnet run --project` from the repo root, and Aspire, start in different working directories |
| 13 | Latest stable eval packages | 10.11.0 | Version alignment (Package versions) |
| 14 | Tool-use eval: `agent.EvaluateAsync(queries, …)` | The **pre-existing responses** overload `EvaluateAsync(responses, queries, evaluator, …, expectedToolCalls)` | Same built-in, without running agentic-rag twice (2 min per answer on CPU) |
| 15 | Two tool checks | Plus `citations_faithful` (`FunctionEvaluator.Create`) | Phase 05 handoff: small models invent cite tags; it caught one |
| 16 | `expectedToolCalls.arguments` as JSON values | Strings only (`Dictionary<string, string>`) | Measured with a scratch probe: `ToolCallArgsMatch` never matches a `JsonElement` expected value against the parsed arguments; .NET strings match |
| 17 | Matrix: 4 defaults, `Hybrid+MultiQuery+Rerank` opt-in | Ran the 4 defaults plus `Hybrid+MultiQuery`; `Hybrid+MultiQuery+Rerank` exercised once through `/api/search` (42 s) | Isolates multi-query; another rerank configuration is about 40 more CPU minutes with an already-measured verdict |
| 18 | Step 9 / AC under Aspire (`aspire start`, `aspire resource evals-retrieval start`) | AgentServer checks outside Aspire; evals with `dotnet run`; AppHost resources compile (`WithExplicitStart` exists in 13.6.4) but weren't started | Spec's own fallback path; an AppHost start rebuilds the solution while evals hold DLLs. **To verify:** see "Commands" |
| 19 | `.config/dotnet-tools.json` | `dotnet-tools.json` at the repo root | SDK 10's `dotnet new tool-manifest` default location; `dotnet aieval` works from the root |
| 20 | Cloud judge recommended | Local `llama3.2` judge | No API key on this machine; scores labelled indicative |
| 21 | Nested option ranges | No DataAnnotations on `Rerank`/`MultiQuery`/`Hybrid` | `ValidateDataAnnotations` doesn't recurse into nested objects; every bad value degrades safely (`Math.Max(pool, top)`, 0 variants = the original query only) |
| 22 | `ChatClientFactory` refactor "no behaviour change" | One change: the missing-`ConnectionStrings:ollama` error now fires when the first Ollama client is resolved, not at registration | The endpoint is read inside the factory, which all clients share |

## Anything Phase 07 must know
- Set `AI:Chat:ContextLength` on every host that runs an Ollama chat model; size Phase 07's token budget (`Agent:MaxInputTokens`) against it.
- `/api/search`'s `strategy` and the `rag.strategy` span tag are now `Describe()`.
- MEAI spans from the reranker and rewriter clients come from `ChatClientFactory` (`UseOpenTelemetry`); register `Experimental.Microsoft.Extensions.AI` with the tracing setup.
- With local models keep `Retrieval:Strategy=Hybrid` at most; leave `Rerank` and `MultiQuery` off unless a stronger model is configured through `Retrieval:*:Model`.

## Known limitations
- The keyword side ranks every OR-match (≈0.5 s on 824 chunks, see "How hybrid search works"); a large corpus needs a stored tsvector column or a cap.
- The listwise LLM reranker needs a strong model. Future work: a cross-encoder ONNX reranker (`Microsoft.ML.OnnxRuntime` with a `bge-reranker`-class model) or pointwise scoring; truncating passages to 700 characters hides evidence that sits late in a chunk.
- Headcount-style facts buried in long chunks are missed by every strategy (chunking/enrichment, Phase 14).
- The local judge can't produce parseable Relevance/Groundedness scores; use the cloud judge for real numbers.
- Phase 04/05 behaviour under the larger `num_ctx` was not re-measured.
- The cleaning-variant comparison (`Vector@filing_chunks_lc`) was optional and not run: the embedding model and normalization are code constants on disk (Phase 00), so a lowercase variant needs a code change, not a configuration prefix.

## Commands & operating notes
- **Connection string outside Aspire** (Aspire generates the port and password):
  ```powershell
  $pg = docker ps --filter "name=postgres" --format "{{.Names}}" | Select-Object -First 1
  $port = (docker port $pg 5432) -replace '.*:', ''
  $pw = docker exec $pg sh -c 'printf %s "$POSTGRES_PASSWORD"'
  dotnet user-secrets --project evals/SECFilingsAgenticRag.Evals set "ConnectionStrings:secfilings" "Host=127.0.0.1;Port=$port;Username=postgres;Password=$pw;Database=secfilings"
  ```
  An environment variable `ConnectionStrings__secfilings` works too. Refresh it when the container is recreated.
- **How to run evals** (from the repo root; `dotnet build SECFilingsAgenticRag.slnx` first):
  ```powershell
  $env:Retrieval__Rerank__Model = "llama3.2"; $env:Retrieval__MultiQuery__Model = "llama3.2"   # qwen3:4b can't rerank
  dotnet run --project evals/SECFilingsAgenticRag.Evals -- dataset-helper --ticker AAPL
  dotnet run --project evals/SECFilingsAgenticRag.Evals -- retrieval                                   # Vector,Hybrid,Vector+Rerank,Hybrid+Rerank
  dotnet run --project evals/SECFilingsAgenticRag.Evals -- retrieval --k 3,5,10 --configs "Vector,Hybrid,Hybrid+MultiQuery"
  dotnet run --project evals/SECFilingsAgenticRag.Evals -- answers --agent standard-rag,agentic-rag --limit 5
  dotnet aieval report --path evals/results/store --output evals/results/report.html --open
  ```
  - Config names are `Strategy` plus `+MultiQuery` / `+Rerank`; a name that `Describe()` doesn't reproduce fails the run.
  - Reports go to `evals/results/<yyyyMMdd-HHmmss>-{retrieval,answers}.md` (git-ignored, except `sample-*.md`).
  - Timing on CPU: Vector and Hybrid take seconds; each `+Rerank` configuration takes about 30 minutes for 25 questions; `answers --limit 5` for two agents took about 100 minutes with `llama3.2` agents and judge (1-2 minutes per answer plus judging); expect far longer with `qwen3:4b`.
- **Judge configuration** (environment variables override `appsettings.json`):
  - `Evals__Judge__Provider` = `OpenAICompatible` (default) | `Ollama`;
  - `Evals__Judge__Model` (required);
  - `Evals__Judge__Endpoint` (OpenAICompatible only, default GitHub Models `https://models.github.ai/inference`);
  - `Evals__Judge__ApiKey`;
  - `Evals__Judge__TimeoutSeconds` (300).

  Store the key only in user-secrets:
  ```powershell
  dotnet user-secrets --project evals/SECFilingsAgenticRag.Evals set "Evals:Judge:ApiKey" "<token>"
  ```
  A local judge: `$env:Evals__Judge__Provider = "Ollama"; $env:Evals__Judge__Model = "llama3.2"`. It prints the "Local 4B judges are unreliable" warning. The judge's responses are cached in `evals/results/store` (built-in, 14-day TTL).
- **Agents under test:** `AI__Chat__Model` (e.g. `llama3.2`) changes the in-process agents' model; the report header records it.
- **Under Aspire:** the resources `evals-retrieval` and `evals-answers` use explicit start. Pass the rerank and judge overrides as AppHost environment variables or evals user-secrets.
  ```powershell
  $aspire = "$env:USERPROFILE\.nuget\packages\aspire.cli.win-x64\13.6.4\tools\net10.0\win-x64\aspire.exe"
  & $aspire start --apphost src/SECFilingsAgenticRag.AppHost; & $aspire wait api
  & $aspire resource evals-retrieval start; & $aspire logs evals-retrieval
  ```
- **Retrieval configuration** (environment variables override): `Retrieval__Strategy` = `Vector` | `Hybrid`, `Retrieval__Rerank__Enabled`, `Retrieval__Rerank__CandidatePool` (20), `Retrieval__Rerank__Model`, `Retrieval__MultiQuery__Enabled`, `Retrieval__MultiQuery__Variants` (3), `Retrieval__MultiQuery__Model`, `Retrieval__Hybrid__CandidatePool` (40), `Retrieval__Hybrid__RrfK` (60), `AI__Chat__ContextLength` (8192).
- **Check the strategy:**
  ```powershell
  (Invoke-RestMethod -Method Post http://localhost:5180/api/search -ContentType 'application/json' -Body '{"query":"Item 1A risk factors","top":3}').strategy
  ```
- **Gotchas:**
  - `Retrieval:Rerank:Enabled=true` with `qwen3:4b` takes about 7 minutes per search and silently keeps the original order. Set `Retrieval:Rerank:Model`.
  - Look for `truncating input prompt` in `%LOCALAPPDATA%\Ollama\server.log` when answers or rankings look as if the model never saw the question.
  - RRF scores are about 0.016-0.033; don't threshold them like cosine scores.
  - Rerank scores are synthetic (`1 - i/n`).
  - To see raw structured-output replies, set `Logging__LogLevel__SECFilingsAgenticRag=Debug`.
  - A database that ran this phase's first version has an unused `search_tsv` column: `ALTER TABLE sec_filing_chunks DROP COLUMN search_tsv;` (this also drops its index).
  - Keep the keyword SQL's `to_tsvector('english', "content")` identical to the connector's index expression (its default language is english), or Postgres stops using the index.
  - Don't `dotnet build` while an eval or AppHost runs (locked DLLs); use `--artifacts-path` for a compile check.

## Microsoft Learn sources consulted
| # | URL | What I checked | Decision / outcome in code |
|---|-----|----------------|----------------------------|
| 1 | https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/postgres-connector | Feature table: "HybridSearch supported? Yes, requires … `IsFullTextIndexed = true`"; GIN on `to_tsvector` | **Changed a decision** (deviation 2): the spec's premise was wrong; custom hybrid kept only because the built-in ANDs terms (confirmed from its SQL) |
| 2 | https://learn.microsoft.com/dotnet/ai/vector-stores/vector-search#hybrid-search | `IKeywordHybridSearchable` usage, `HybridSearchOptions` (`Filter`, `AdditionalProperty`) | `HybridSearchAsync` implemented with the same option semantics (filter honoured) |
| 3 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-information-retrieval | Full-text, hybrid, manual multiple queries; RRF formula with c ≈ 60 as a cheap first stage before a model reranker; reranking adds latency; "validate scores… models output unexpected values"; benchmark rerankers against your queries | `RrfFusion` (k = 60); reranker drops unknown and duplicate ids and falls back to the original order; rerank kept configurable and benchmarked (RAG guide **information retrieval**, 5.2/5.3) |
| 4 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide | Phase order: 5.3 evaluate searches, 6.x language-model end-to-end evaluation, documentation and aggregation | Retrieval matrix (5.3) and answer evals (6.1/6.3); results documented and aggregated |
| 5 | https://learn.microsoft.com/azure/architecture/ai-ml/guide/rag/rag-llm-evaluation-phase | Groundedness, completeness, relevance definitions; nondeterminism (use ranges); agentic dimensions (tool selection, calls per request, latency) | Relevance/Groundedness/Completeness, avg tool calls, latency, tool checks; "indicative" labelling |
| 6 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.chatclientstructuredoutputextensions.getresponseasync | `useJsonSchemaResponseFormat` default true; "might cause an error if the model does not support it" | `StructuredOutput`: schema, then prompt mode; measured per model (section above) |
| 7 | https://learn.microsoft.com/agent-framework/agents/evaluation | `agent.EvaluateAsync`, `LocalEvaluator`, `EvalChecks`, `FunctionEvaluator.Create`, expected tool calls, pre-existing responses overload, MEAI evaluators via `CompositeEvaluator` + `chatConfiguration` | Tool use through `LocalEvaluator(ToolCalledCheck, ToolCallArgsMatch, citations_faithful)` on the **pre-existing responses** (deviation 14). Quality stays on `ReportingConfiguration` because `EvaluateAsync` has no per-item Groundedness context and no disk cache |
| 8 | Package XML docs `Microsoft.Agents.AI` 1.28.0 (`EvalChecks.ToolCallArgsMatch`, `ExpectedToolCall(string, IReadOnlyDictionary<string,object>)`, `AgentEvaluationResults.Items`) | The .NET args-match check exists (spec asked to verify); subset matching; null args = don't check | Used directly, no fallback evaluator; a scratch probe showed string-only expected values (deviation 16) and `BooleanMetric` per check name |
| 9 | https://learn.microsoft.com/dotnet/ai/evaluation/evaluate-with-reporting | `DiskBasedReportingConfiguration`, `CreateScenarioRunAsync`, scenario names with `.` for hierarchy, execution name, `dotnet tool install … Microsoft.Extensions.AI.Evaluation.Console`, `aieval report --path --output` | Store under `evals/results/store`, scenarios `{agent}.{id}`, execution `answers-{timestamp}`; tool manifest; HTML report generated |
| 10 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.evaluation.reporting.storage.diskbasedreportingconfiguration.create | Exact parameters (`storageRootPath`, `evaluators`, `chatConfiguration`, `enableResponseCaching`, `executionName`); cache keys not stable across library versions | Named arguments as in the spec; tool pinned to the library version |
| 11 | https://learn.microsoft.com/dotnet/ai/evaluation/libraries#cached-responses | Response caching and the `aieval` tool | Judge cached; agents not |
| 12 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.evaluation.quality.groundednessevaluatorcontext and https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.evaluation.quality.completenessevaluatorcontext.-ctor | `GroundednessEvaluatorContext(groundingContext)`, `CompletenessEvaluatorContext(string groundTruth)` | Recorded chunk texts and `referenceAnswer` |
| 13 | https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.evaluation.quality.groundednessevaluator and https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.evaluation.quality.completenessevaluator | Prompts tuned for GPT-4o; "can be especially poor when a smaller / local model is used" | Ollama-judge warning; `n/a` scores explained |
| 14 | https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/hybrid-search | Hybrid = vector + keyword in parallel, `IKeywordHybridSearchable` only on supporting connectors | Same as row 1 |
| 15 | Package XML docs `CommunityToolkit.VectorData.PgVector` 1.0.2 + captured SQL (Npgsql parameter logging) | `HybridSearchAsync`, `BuildHybridSearchCommand` (RRF), `WithFullTextSearchLanguage`, `plainto_tsquery` | Evidence for row 1's decision |
| 16 | Package XML docs `Microsoft.Extensions.VectorData.Abstractions` 10.11.0 | `IKeywordHybridSearchable.HybridSearchAsync(TInput, ICollection<string>, int, HybridSearchOptions?, ct)`, `GetService` | Signatures implemented |
| 17 | Package XML docs OllamaSharp 5.5.0 (`ChatOptionsExtensions.AddOllamaOption`, `AbstractionMapper.TryAddOllamaOption`) | How to pass `num_ctx` through MEAI `ChatOptions` | `ConfigureOptions(o => o.AddOllamaOption(OllamaOption.NumCtx, …))` (deviation 9) |
| 18 | *external* Ollama 0.40.16 `server.log` (`truncating input prompt limit=2050 prompt=4132`) and `/api/chat` with `format` (schema) | Silent prompt truncation; per-model structured output | `AI:Chat:ContextLength`; mode table |
| 19 | *external* PostgreSQL full-text search (`plainto_tsquery`, `websearch_to_tsquery`, `ts_rank_cd`, expression indexes, `EXPLAIN`) | AND semantics of both parsers; an expression index is used only when the query repeats its exact expression | OR via `replace(…, '&', '|')`; inline `to_tsvector('english', "content")` in `WHERE` |

**Docs vs reality:**
- The spec contradicts Learn on PgVector hybrid support (row 1).
- The Learn MAF page shows only `new ExpectedToolCall("get_weather")`; the 1.28 package also takes arguments, but matches only .NET-typed values.

## Suggested commit message
```
feat(retrieval): hybrid search, LLM reranking, multi-query + evaluation harness

- PostgresHybridSearch (IVectorSearchable + IKeywordHybridSearchable): built-in vector search + Postgres FTS
  (OR-ed terms, ts_rank_cd) fused with RRF; GIN index from IsFullTextIndexed on FilingChunkRecord.Text, no re-index
- RerankingVectorSearch and MultiQueryVectorSearch decorators (MEAI structured output, schema -> prompt fallback)
- config-only composition: Retrieval:Strategy, Retrieval:Rerank, Retrieval:MultiQuery; /api/search reports Describe()
- Common: ChatClientFactory (model overrides) + AI:Chat:ContextLength (Ollama num_ctx; fixes silent prompt truncation)
- SECFilingsAgenticRag.Evals: golden dataset (25 q), Hit@k/MRR/Recall@k matrix, M.E.AI.Evaluation answer quality
  with disk cache + aieval report, MAF agent.EvaluateAsync tool-use and citation-faithfulness checks
- AppHost: evals-retrieval / evals-answers (explicit start)
```
