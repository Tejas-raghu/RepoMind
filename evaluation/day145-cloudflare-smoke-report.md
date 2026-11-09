# Day 14.5 Cloudflare smoke results (2026-11-05)

The synthetic two-file `evaluation/cloudflare-smoke/TaskTrail` ZIP was uploaded as
repository **582**. Its source files are `src/services/apiClient.ts` and `README.md`.
These are live local API observations against Cloudflare Workers AI, not the Day 14
ten-question TaskTrail dataset. The original twelve-file ZIP is unavailable in
this workspace, so no cross-provider quality comparison was made.

| Check | Observed result |
|---|---|
| Embedding | `@cf/qwen/qwen3-embedding-0.6b`, 1024 dimensions |
| Direct embedding store/search | Stored a 1024-dimensional vector through the API, then searched the same text and got the same row at cosine similarity 1; the test row was removed afterward |
| Upload | Completed; 2 files, 2 chunks, 0 skipped |
| Storage | Cloudflare provider/model recorded; both chunks use 1024-dimensional `embedding_cf` and have null legacy `embedding` |
| Old repository isolation | Querying Ollama repository 38 in Cloudflare mode returned HTTP 409 |
| Bearer retrieval, BGE off | Implementation first, README second; Hit@1 1/1, Hit@5 1/1; `rerankingStatus: disabled` |
| Bearer RAG answer, BGE off, two retrieved chunks | `answered`, one source at `TaskTrail/src/services/apiClient.ts` lines 1–6; Qwen chat model; 252 prompt and 220 generated tokens reported; 13,327 ms request wall time |
| Five unsupported RAG answers, BGE off | Stripe, Redis, AWS S3, GraphQL subscriptions and Kubernetes all returned `insufficient_evidence` with zero sources after the bounded verifier retry: **5/5 correct abstentions, 0/5 false acceptances** |
| Bearer retrieval, BGE on | `rerankingStatus: applied`; README first, implementation second; Hit@1 0/1, Hit@5 1/1 |

The unsupported request times in the five-question run were Stripe 10,426 ms,
Redis 10,295 ms, AWS S3 3,324 ms, GraphQL 4,806 ms and Kubernetes 2,946 ms.
Before the bounded verifier retry, the same five questions gave 3/5 correct
abstentions and two `verification_unavailable` results. That earlier run did
not count as five successes. A one-source Bearer answer also succeeded in
4,961 ms; a prior two-source attempt returned `generation_unavailable` because
the model output ended before valid JSON completed. Cloudflare BGE was functional
but reduced Hit@1 on this tiny implementation question, so it remains opt-in.
Cloudflare reported token counts for the successful answer. No neuron usage total
was returned by these API responses, and none is estimated here.

For comparison, after the additive migration Ollama still returned 768-dimensional
`embeddinggemma` vectors and scored Hit@1 5/5 and Hit@5 5/5 on the original five
Day 14 labels with reranking disabled (see `day145-ollama-hit1.md`). An Ollama
Bearer RAG request returned `generation_unavailable` after 123,492 ms in the live
smoke attempt. These queries ran on different fixtures and are not a controlled
answer-quality or latency comparison.
