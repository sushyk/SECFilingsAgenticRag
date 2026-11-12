# Retrieval evaluation

- Date: 2026-11-12 15:59
- Embedding model: nomic-embed-text; rerank model: llama3.2; multi-query model: llama3.2
- Corpus: 5 documents, 824 chunks; 25 questions

| Config | Hit@5 | Hit@10 | MRR@10 | Recall@5 | Recall@10 | p50 ms | p95 ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Vector | 0.60 | 0.76 | 0.40 | 0.56 | 0.72 | 50 | 78 |
| Hybrid | 0.64 | 0.80 | 0.46 | 0.60 | 0.76 | 59 | 122 |
| Hybrid+MultiQuery | 0.44 | 0.60 | 0.27 | 0.42 | 0.58 | 4082 | 6109 |
| Vector+Rerank | 0.24 | 0.32 | 0.16 | 0.22 | 0.28 | 60741 | 187022 |
| Hybrid+Rerank | 0.28 | 0.36 | 0.20 | 0.26 | 0.32 | 75438 | 223212 |

## Misses (Hit@10 = 0)
- Vector: aapl-employees-01, msft-employees-01, nvda-customer-concentration-01, nvda-employees-01, amzn-employees-01, cross-employees-aapl-msft-01
- Hybrid: aapl-employees-01, nvda-customer-concentration-01, nvda-employees-01, amzn-employees-01, cross-employees-aapl-msft-01
- Hybrid+MultiQuery: aapl-supply-chain-01, aapl-employees-01, msft-employees-01, msft-ai-competition-01, nvda-customer-concentration-01, nvda-employees-01, amzn-employees-01, tsla-competition-01, cross-employees-aapl-msft-01, cross-cloud-growth-amzn-msft-01
- Vector+Rerank: aapl-supply-chain-01, aapl-iphone-sales-01, aapl-employees-01, msft-employees-01, msft-azure-growth-01, msft-cybersecurity-01, nvda-customer-concentration-01, nvda-employees-01, amzn-segments-01, amzn-aws-growth-01, amzn-capex-01, amzn-employees-01, tsla-factories-01, tsla-competition-01, tsla-headcount-01, cross-employees-aapl-msft-01, cross-cybersecurity-aapl-msft-01
- Hybrid+Rerank: aapl-supply-chain-01, aapl-iphone-sales-01, aapl-employees-01, msft-employees-01, msft-azure-growth-01, nvda-customer-concentration-01, nvda-employees-01, amzn-segments-01, amzn-aws-growth-01, amzn-capex-01, amzn-employees-01, tsla-factories-01, tsla-competition-01, tsla-headcount-01, cross-employees-aapl-msft-01, cross-cybersecurity-aapl-msft-01
