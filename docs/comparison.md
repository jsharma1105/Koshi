# Comparison

How Koshi compares to adjacent MCP servers and managed RAG services.

| Feature | Koshi | [@mcp/memory](https://github.com/modelcontextprotocol/servers/tree/main/src/memory) | [@mcp/filesystem](https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem) | RAG SaaS |
|---|:---:|:---:|:---:|:---:|
| BM25 retrieval | ✅ | ❌ | ❌ | ✅ |
| Token budgeting & cache positioning | ✅ | ❌ | ❌ | ❌ |
| Typed memory (facts, decisions, patterns) | ✅ | ⚠️ graph only | ❌ | ⚠️ |
| Turn-end auto-capture of decisions | ✅ | ❌ | ❌ | ❌ |
| Git-shared memory (vault mode) | ✅ | ❌ | ❌ | ❌ |
| Per-team quality scoring | ✅ | ❌ | ❌ | ❌ |
| Works offline (no keys, no GPU) | ✅ | ✅ | ✅ | ❌ |
| MIT licensed, single binary | ✅ | ✅ | ✅ | ❌ |
| Setup time[^setup] | ~60 sec | ~60 sec | ~60 sec | hours |
| Cost | free | free | free | $$$ |

[^setup]: Covers the package install itself. Koshi's end-to-end
(install + register with your MCP client + install personas + index
your project) is one command via the shell installer; see
[`docs/install.md`](install.md).

## Where each shines

- **Koshi** — retrieval + memory + context engineering + team telemetry
  in one binary. Pick this when you want one server that handles every
  context-engineering pillar offline and shares decisions across the
  team via Git.
- **@mcp/memory** — graph-shaped general-purpose memory. Pick this when
  you want a knowledge graph rather than typed memories + BM25
  retrieval.
- **@mcp/filesystem** — raw file read/write/search primitives. Pair with
  Koshi if you also need file-system tools that are not part of Koshi's
  retrieval pipeline.
- **RAG SaaS** (Pinecone Assistant, AWS KB, etc.) — managed embeddings
  with their own UI. Pick this when you want hosted infrastructure and
  embedding-quality retrieval, and are happy paying per-query.

## Why no embeddings?

Embeddings need either an API key (cost, latency, privacy) or a local
GPU (deployment friction). BM25 ships in the binary, runs offline, and
on the included evaluation corpus achieves
**MRR 0.874 / Recall@5 0.914** — competitive with vector retrieval for
code and documentation use-cases. See
[`docs/tuning-results.md`](tuning-results.md) for the harness and
methodology.
