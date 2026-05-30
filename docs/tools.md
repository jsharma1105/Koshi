# MCP tools reference

Koshi exposes **24 tools** plus **2 diagnostics**, grouped into four
pillars. Every tool name starts with `koshi_` so they namespace cleanly
in a multi-server MCP setup.

| Pillar | Tools |
|---|---|
| 🔍 [Retrieval](#-retrieval-5) | 5 |
| 📦 [Context engineering](#-context-engineering-3) | 3 |
| 🧠 [Memory](#-memory-9) | 9 |
| 👥 [Team & quality](#-team--quality-5) | 5 |
| 🩺 [Diagnostics](#-diagnostics-2) | 2 |

---

## 🔍 Retrieval (5)

BM25 keyword search over your codebase or any text corpus.

| Tool | Purpose |
|---|---|
| `koshi_index_directory` | Recursively chunk and index files from a directory. Excludes secrets, build output, hidden directories. |
| `koshi_index` | Index a JSON array of in-memory documents. |
| `koshi_search` | BM25 keyword search over the indexed corpus. Auto-indexes `KOSHI_INDEX_PATH` on first call. |
| `koshi_list_indexed` | List indexed documents with chunk counts and token totals. |
| `koshi_clear_index` | Reset the index without restarting the server. |

---

## 📦 Context engineering (3)

Pack the prompt window inside a token budget; measure tokens before you
call any LLM.

| Tool | Purpose |
|---|---|
| `koshi_compile_context` | Pack system prompt + retrieval + memory + team context into a token budget using one of four positioning strategies. |
| `koshi_token_count` | Count GPT-4 (`cl100k`) tokens for any text. The encoding is configurable via `KOSHI_TOKENIZER_MODEL`. |
| `koshi_budget_plan` | Plan a token-budget allocation across roles and show cache-prefix savings. |

---

## 🧠 Memory (9)

Typed memories (Fact, Decision, Pattern, Preference) with persistence,
vault export/import, and end-of-turn auto-capture.

| Tool | Purpose |
|---|---|
| `koshi_remember` | Store a typed memory with confidence and source. |
| `koshi_recall` | Recall memories by topic; ranked by keyword + recency + confidence. |
| `koshi_memory_stats` | Counts by type, top subjects, average confidence, persistence status, backend kind, unmanaged-note count (vault). |
| `koshi_forget` | Remove all memories matching a subject. |
| `koshi_clear_memories` | Delete every stored memory (requires `confirm=true`). |
| `koshi_memory_export_to_vault` | Bulk-export the memory store to a Markdown vault directory. See [vault mode](vault-mode.md). |
| `koshi_memory_import_from_vault` | Import memories from a Markdown vault. Modes: `merge` / `overlay` / `replace`. |
| `koshi_memory_sync_vault` | Force a fresh re-scan of the active vault (no-op for the JSON backend). |
| `koshi_capture_turn` | **Turn-end auto-capture for decisions** (v0.8.0). See [auto-capture](auto-capture.md). |

### `koshi_capture_turn` deep-dive

Pass a 1–3 paragraph turn summary plus optional `linked_pr` /
`linked_commits`. The server runs a deterministic pattern-based
extractor (no LLM, no network) that pulls decision-shape sentences and
persists each as a `Decision` memory with a provenance footer. Set
`auto_promote=false` to preview candidates without saving.

Paste [`docs/copilot-instructions-snippet.md`](copilot-instructions-snippet.md)
into your repo's `.github/copilot-instructions.md` so the agent calls
this tool reliably at end of every non-trivial turn.

---

## 👥 Team & quality (5)

Score AI interactions per team and surface trends.

| Tool | Purpose |
|---|---|
| `koshi_register_team` | Register a team with a custom token budget, retrieval `topK`, and quality target. |
| `koshi_score_turn` | Score one AI interaction across retrieval, efficiency, cache, latency, and user signals. |
| `koshi_team_dashboard` | Render a per-team dashboard with trends and recommendations. |
| `koshi_analyze_feedback` | Analyse trends and produce concrete config-tuning suggestions. |
| `koshi_list_teams` | List all registered teams and their average scores. |

---

## 🩺 Diagnostics (2)

| Tool | Purpose |
|---|---|
| `koshi_version` | Server version, .NET runtime, OS, process and start time. |
| `koshi_health` | Indexed corpus size, memory store status, env-var configuration, uptime, working set. |

---

## Calling tools from each client

These names are the **MCP tool names** the server advertises. Each
client surfaces them slightly differently:

| Client | How tools appear |
|---|---|
| Claude Code | `mcp__koshi__<tool>` in the allow-list and tool palette. |
| GitHub Copilot CLI | `koshi/<tool>` in the command palette. |
| Cursor / Windsurf | `koshi.<tool>` in the agent's tool list. |
| Python wrapper | `Client.<tool>` as snake-case methods. See [`python/README.md`](https://github.com/jsharma1105/Koshi/blob/main/python/README.md). |

## Next steps

- [Walkthroughs](walkthroughs.md) — common usage patterns.
- [Configuration](configuration.md) — env vars that change tool behaviour.
- [Vault mode](vault-mode.md) — share memories across the team via Git.
