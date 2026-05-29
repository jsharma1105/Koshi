# Koshi MCP Server

> **Koshi** (講師, "instructor") is a Model Context Protocol server that brings
> retrieval, context engineering, persistent memory, and quality scoring to any
> MCP-compatible AI client. **100% offline. No API keys. No embeddings. No cloud.**

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg)](https://www.nuget.org/packages/Koshi.Mcp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
[![MCP](https://img.shields.io/badge/MCP-1.0-blue.svg)](https://modelcontextprotocol.io)

Koshi turns any MCP client (Claude Code, GitHub Copilot CLI, Cursor, Windsurf, …)
into a context-engineering powerhouse. It chunks and indexes your codebase or
documentation with BM25, packs context windows for optimal cache reuse, stores
persistent facts and decisions, and scores the quality of every AI interaction.

---

## Table of Contents

- [Why Koshi?](#why-koshi)
- [Quick Start (60 seconds)](#quick-start-60-seconds)
- [Client Setup](#client-setup)
  - [Claude Desktop / Claude Code](#claude-desktop--claude-code)
  - [GitHub Copilot CLI](#github-copilot-cli)
  - [Cursor / Windsurf](#cursor--windsurf)
  - [Generic MCP client](#generic-mcp-client)
- [Available Tools (24)](#available-tools-24)
- [Configuration](#configuration)
- [Usage Walkthroughs](#usage-walkthroughs)
- [How It Works](#how-it-works)
- [Security & Privacy](#security--privacy)
- [Comparison](#comparison)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [License](#license)

---

## Why Koshi?

Most MCP servers do one thing: a connector for a database, a wrapper around an
API, a single-purpose tool. **Koshi is a complete context-engineering toolkit**
that combines four traditionally-separate capabilities:

| Capability | Koshi | MCP Memory | AWS KB Retrieval | RAG SaaS |
|------------|:-----:|:----------:|:----------------:|:--------:|
| **BM25 retrieval over your codebase** | ✅ | ❌ | ❌ | ✅ |
| **Token-budget-aware context packing** | ✅ | ❌ | ❌ | ❌ |
| **Cache-optimized prompt positioning** | ✅ | ❌ | ❌ | ❌ |
| **Typed memory (facts, decisions, patterns)** | ✅ | ⚠️ graph only | ❌ | ⚠️ |
| **Per-team quality scoring & feedback** | ✅ | ❌ | ❌ | ❌ |
| **Works fully offline (no API keys)** | ✅ | ✅ | ❌ | ❌ |
| **Single binary, MIT licensed** | ✅ | ✅ | ❌ | ❌ |

Koshi is a learning + production tool. Use it to:

- 📚 **Index docs, RFCs, ADRs, source code** — search them inline from your AI client
- 📝 **Remember decisions and conventions** across sessions — never re-explain context
- 🎯 **Pack context windows optimally** — measure tokens, plan budgets, exploit caching
- 📊 **Track quality per team** — score interactions, surface trends, get config tuning suggestions

---

## Quick Start (60 seconds)

Pick one of three install paths:

### Option A — Python users (`pip install koshi`)

```bash
pip install koshi
python -c "from koshi import Client
with Client() as k:
    print(k.version())"
```

No `.NET install` required. The first call auto-downloads a native AOT
binary (~15 MB) into your user cache, verified by SHA-256 against hashes
baked into the wheel at release time. See [the PyPI page](https://pypi.org/project/koshi/)
for the full Python API.

### Option B — .NET tool (`dotnet tool install`)

**Prerequisites**: [.NET 10 SDK or runtime](https://dot.net/download) (Windows, macOS, Linux).

```bash
dotnet tool install --global Koshi.Mcp
```

This installs the `koshi-mcp` command globally. Verify with:

```bash
koshi-mcp --version    # should match the installed package version
```

### Option C — No-runtime download (Native AOT single-file binary)

Every GitHub release ships self-contained `koshi-mcp` binaries (no .NET
runtime required) for `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`,
and `win-arm64`, each with a `.sha256` sidecar. See the
[releases page](https://github.com/jsharma1105/Koshi/releases).

### Add to your MCP client

Add this to your client's MCP config (see [Client Setup](#client-setup) for paths):

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_INDEX_PATH": "/absolute/path/to/your/project",
        "KOSHI_MEMORY_FILE": "/absolute/path/to/koshi-memory.json"
      }
    }
  }
}
```

Restart your MCP client. You can now ask the AI things like:

> _"Use Koshi to search the codebase for how authentication works."_
>
> _"Remember that we use ULIDs for all entity IDs (decision)."_
>
> _"What did we decide about authentication last sprint?"_

---

## Client Setup

### Claude Desktop / Claude Code

Edit `claude_desktop_config.json` (or `.claude/settings.json`):

| OS | Path |
|---|---|
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| Linux | `~/.config/Claude/claude_desktop_config.json` |

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_INDEX_PATH": "/Users/you/code/my-project",
        "KOSHI_MEMORY_FILE": "/Users/you/.koshi/memory.json"
      }
    }
  }
}
```

### GitHub Copilot CLI

```bash
copilot mcp add koshi koshi-mcp \
  --env KOSHI_INDEX_PATH=/path/to/project \
  --env KOSHI_MEMORY_FILE=/path/to/memory.json
```

Or add manually to `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_INDEX_PATH": "/path/to/project"
      }
    }
  }
}
```

### Cursor / Windsurf

Add to `.cursor/mcp.json` (or workspace settings):

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_INDEX_PATH": "${workspaceFolder}"
      }
    }
  }
}
```

### Generic MCP client

Any MCP client that supports stdio transport works:

```bash
# The server reads JSON-RPC on stdin and writes responses on stdout.
# Logs go to stderr exclusively — stdout is reserved for the protocol.
# Pass `--version` or `--help` for one-shot informational output.
koshi-mcp
```

### Microsoft Agency CLI

[Agency](https://aka.ms/agency) wraps Copilot CLI and Claude Code with marketplace plugins. Koshi ships with a `plugin.json` manifest at the repo root, so you can:

**Option A — Use Koshi as a one-shot MCP proxy** (simplest, no install):

```bash
agency mcp local --command koshi-mcp \
  --env-var KOSHI_INDEX_PATH=C:\path\to\project \
  --env-var KOSHI_MEMORY_FILE=C:\Users\you\.koshi\memory.json
```

**Option B — Install as a persistent Agency plugin** (from a fork/marketplace):

```bash
# Once Koshi is published to a marketplace repo:
agency plugin install gh:owner/koshi@main
agency copilot --plugin koshi
```

Agency reads both `plugin.json` and `.claude-plugin/plugin.json` — Koshi ships both.

---

## Available Tools (24)

### 🔍 Retrieval (5)

| Tool | Purpose |
|------|---------|
| `koshi_index_directory` | Recursively chunk and index files from a directory (BM25). Excludes secrets, build output, hidden dirs. |
| `koshi_index` | Index a JSON array of in-memory documents. |
| `koshi_search` | BM25 keyword search over the indexed corpus. Auto-indexes `KOSHI_INDEX_PATH` on first call. |
| `koshi_list_indexed` | List indexed documents with chunk counts and token totals. |
| `koshi_clear_index` | Reset the index without restarting the server. |

### 📦 Context Engineering (3)

| Tool | Purpose |
|------|---------|
| `koshi_compile_context` | Pack system prompt + retrieval + memory + team context into a token budget using one of four positioning strategies. |
| `koshi_token_count` | Count GPT-4 (cl100k) tokens for any text. |
| `koshi_budget_plan` | Plan a token budget allocation across roles and show cache-prefix savings. |

### 🧠 Memory (9)

| Tool | Purpose |
|------|---------|
| `koshi_remember` | Store a typed memory (Fact / Decision / Pattern / Preference) with confidence and source. |
| `koshi_recall` | Recall memories by topic, ranked by keyword match + recency + confidence. |
| `koshi_memory_stats` | Counts by type, top subjects, average confidence, persistence status, backend kind, unmanaged-note count (vault). |
| `koshi_forget` | Remove all memories matching a subject. |
| `koshi_clear_memories` | Delete every stored memory (requires `confirm=true`). |
| `koshi_memory_export_to_vault` | Bulk-export the current memory store to a Markdown vault directory. See [Vault Mode](../../docs/vault-mode.md). |
| `koshi_memory_import_from_vault` | Import memories from a Markdown vault. Modes: `merge` / `overlay` / `replace`. |
| `koshi_memory_sync_vault` | Force a fresh re-scan of the active vault (no-op for the JSON backend). |
| `koshi_capture_turn` | **Turn-end auto-capture for decisions** (v0.8.0). Pass a 1-3 paragraph turn summary plus optional `linked_pr` / `linked_commits`; the server runs a pattern-based extractor (no LLM) to pull out decision-shape sentences and persists each as a `Decision` memory with provenance. Set `auto_promote=false` to preview candidates without saving. Paste [`docs/copilot-instructions-snippet.md`](../../docs/copilot-instructions-snippet.md) into your repo's `.github/copilot-instructions.md` so the agent calls this reliably. |

### 👥 Team & Quality (5)

| Tool | Purpose |
|------|---------|
| `koshi_register_team` | Register a team with a custom token budget, retrieval `topK`, and quality target. |
| `koshi_score_turn` | Score one AI interaction across retrieval, efficiency, cache, latency, and user signals. |
| `koshi_team_dashboard` | Render a per-team dashboard with trends and recommendations. |
| `koshi_analyze_feedback` | Analyse trends and produce concrete config-tuning suggestions. |
| `koshi_list_teams` | List all registered teams and their average scores. |

### 🩺 Diagnostics (2)

| Tool | Purpose |
|------|---------|
| `koshi_version` | Server version, .NET runtime, OS, process and start time. |
| `koshi_health` | Indexed corpus size, memory store status, env-var configuration, uptime, working set. |

---

## Configuration

Koshi is configured exclusively through **environment variables** (no config files, no flags) — easy to set in any MCP client config.

Since v0.6.0 every path env var **derives a sensible default from the project root**, so most users never need to set anything. If you launch `koshi-mcp` from `~/myrepo` (or `C:\src\myrepo` on Windows), your memory and index land at `<project>/.koshi/memory.json` and `<project>/.koshi/index.json` automatically.

> ⚠️ **Claude Desktop caveat:** Claude Desktop typically launches MCP servers with cwd=`%USERPROFILE%` / `$HOME`, not your project. Set `KOSHI_PROJECT_ROOT` explicitly in your `claude_desktop_config.json` (e.g. `"KOSHI_PROJECT_ROOT": "/Users/you/myrepo"` on macOS, `"/home/you/myrepo"` on Linux, or `"C:/Users/you/myrepo"` on Windows). Copilot CLI and Cline launch servers with cwd=your project, so defaults Just Work there.

| Env var | Default | Purpose |
|---------|---------|---------|
| `KOSHI_PROJECT_ROOT` | `Environment.CurrentDirectory` | Base for all derived paths below. Relative env values resolve against this. |
| `KOSHI_INDEX_PATH` | `<root>` | Absolute path that `koshi_search` will auto-index on first use. **Auto-index is opt-in** — only set this when you want the first `koshi_search` call to scan the directory automatically. When unset, `koshi_search` requires an explicit `koshi_index_directory(path)` first. |
| `KOSHI_INDEX_FILE` | `<root>/.koshi/index.json` | Path to a JSON file used to persist the BM25 retrieval index across server restarts. On startup the snapshot is auto-loaded and validated against the live filesystem (`relpath + size + mtime` fingerprint); stale snapshots are discarded and a re-index runs. Atomic writes, schema-versioned. |
| `KOSHI_MEMORY_FILE` | `<root>/.koshi/memory.json` | Path to a JSON file used to persist memories across server restarts. Atomic writes, schema-versioned. **Ignored when `KOSHI_MEMORY_VAULT` is also set** (a one-line stderr warning is emitted when *both* are explicitly set). |
| `KOSHI_MEMORY_VAULT` | _(unset — vault stays opt-in)_ | Path to a directory used to persist memories as **one Markdown file per memory** under a flavor-specific layout (see `KOSHI_VAULT_FLAVOR`). Git-friendly. External edits/deletes are picked up automatically via a file-system watcher (see `KOSHI_VAULT_WATCH`). Relative paths resolve against `KOSHI_PROJECT_ROOT`. See [docs/vault-mode.md](../../docs/vault-mode.md) for the full format spec and migration guide. |
| `KOSHI_VAULT_FLAVOR` | `obsidian` | Selects the file layout used when `KOSHI_MEMORY_VAULT` is set. Valid values: `obsidian` (default — `<vault>/koshi/<type>/<slug>--<id>.md`); `foam` (identical to Obsidian); `logseq` (`<vault>/pages/koshi-<type>-<slug>--<id>.md`, flat); `dendron` (`<vault>/koshi.<type>.<slug>--<id>.md`, dot-namespaced at vault root). The wire format (YAML frontmatter) is identical across flavors; only filename + placement differs. Unrecognized values log a stderr warning and fall back to `obsidian`. |
| `KOSHI_VAULT_WATCH` | `on` | When `KOSHI_MEMORY_VAULT` is set, controls whether Koshi attaches a `FileSystemWatcher` to the vault. With the watcher (default), reloads only happen when external changes are observed — near-zero steady-state cost. Set to `off` / `false` / `0` on network mounts, container bind mounts, or any FS where inotify-style events are unreliable; Koshi will fall back to reload-on-every-call. |
| `KOSHI_TOKENIZER_MODEL` | `gpt-4` | Selects the encoding used by both `RetrievalTools` (chunking) and `ContextTools` (token counts). Accepts any model name supported by [SharpToken](https://github.com/dmitry-brazhenko/SharpToken) — e.g. `gpt-4` / `gpt-3.5-turbo` → `cl100k_base`; `gpt-4o` / `gpt-4o-mini` → `o200k_base`. Read once per access (changes mid-process take effect on the next tokenizer call). |
| `KOSHI_CHUNK_MAX_TOKENS` | `512` | Default chunk size (in tokens) used by `koshi_index` and `koshi_index_directory` when the per-call `maxTokens` argument is unset. Clamped to `[64, 2048]`. |
| `KOSHI_CHUNK_OVERLAP_TOKENS` | `50` | Default overlap (in tokens) between consecutive chunks. Clamped to `[0, 256]` AND `< maxTokens / 2`. |
| `KOSHI_BM25_STEMMING` | `on` | When set to `off` / `false` / `0`, disables the built-in English stemmer that backs `koshi_search` / `koshi_recall`. Useful if your corpus is dominated by exact-match codes or identifiers. |

Absolute env values are used as-is; relative env values resolve against `KOSHI_PROJECT_ROOT`; empty/whitespace values are treated as unset. Run `koshi_health` to see exactly which value is in effect for each path and whether it came from `[env]` or `[default]`.

**Default safety limits** (hardcoded; tweakable per-call where applicable):

- Max indexed chunks: **50,000**
- Max stored memories: **1,000**
- Max file size for `koshi_index_directory`: **256 KB** (override per call)
- Max files scanned: **5,000** (override per call)
- `topK` clamped to **1–50** (search) and **1–25** (recall)

---

## Usage Walkthroughs

### 1. Index a project and search it

In your AI client, just ask:

> _"Index this project and find the authentication code."_

The agent will call:

```
koshi_index_directory(path="/path/to/project")  # or use KOSHI_INDEX_PATH
koshi_search(query="authentication", topK=5)
```

You get the top 5 matching chunks with file paths, scores, and content previews.

### 2. Plan a context budget before calling an LLM

> _"Plan a 16K-token budget for a system prompt of 800 tokens and a 1,200-token team context."_

```
koshi_budget_plan(totalBudget=16384, systemPrompt="...", teamContext="...")
```

Output explains fixed costs, remaining budget, suggested splits, and cache savings.

### 3. Remember decisions across sessions

> _"Remember that we settled on Postgres for the auth service (Decision, source: arch-meeting-2026-04)."_

```
koshi_remember(
  type="Decision",
  subject="auth-service-database",
  content="Use Postgres 16 with row-level security for the auth service.",
  source="arch-meeting-2026-04",
  confidence=0.95
)
```

Later, in any session:

> _"What database did we decide on for auth?"_

```
koshi_recall(query="auth database", type="Decision")
```

### 3a. Share memories across the team via a Git-backed vault (v0.6.0+)

Want your teammates to inherit the same memories you've stored? Point
Koshi at a directory and commit it to Git:

```bash
mkdir -p ~/teams/our-memories
export KOSHI_MEMORY_VAULT=~/teams/our-memories

# (optional) check it into a shared repo
cd ~/teams/our-memories
git init && git add koshi/ && git commit -m "seed memories"
```

Every `koshi_remember` from now on writes one Markdown file:

```
~/teams/our-memories/
└── koshi/
    └── decisions/
        └── auth-service-database--mem-000001.md
```

…with human-readable YAML frontmatter:

```yaml
---
koshi:
  id: mem-000001
  type: Decision
  scope: { user: "*", workspace: default, thread: null }
  source: arch-meeting-2026-04
  confidence: 0.95
  # ...
---
# auth-service-database

Use Postgres 16 with row-level security for the auth service.
```

External edits, deletes, and `git pull`s are picked up on the next tool
call without a server restart. Hand-written notes that lack a `koshi.id`
in their frontmatter are reported as "unmanaged notes" in
`koshi_memory_stats` but never overwritten. See
[docs/vault-mode.md](../../docs/vault-mode.md) for the full format spec,
migration guide, and FAQ.

### 3b. Auto-capture decisions at turn end (v0.8.0+) — "skip the regression"

The hardest memory to keep is the one no one remembered to write down.
`koshi_capture_turn` is the tool the agent calls at the end of any
non-trivial turn so the *reasoning* behind the change is durable before
anyone closes the tab.

> _"We just landed a fix — capture the decision."_

```jsonc
koshi_capture_turn(
  summary: "Decision: switch the cache layer from in-memory to Redis
            because the per-replica cache lost coherency under load.
            Fixed by upgrading StackExchange.Redis to 2.8.x.",
  linked_pr: 123,
  linked_commits: ["a1b2c3d", "e4f5g6h"],
  auto_promote: true        // false = preview candidates without saving
)
```

What happens:

1. A deterministic pattern extractor (no LLM, no network) pulls
   decision-shape sentences out of the summary: explicit `Decision: …`
   markers, `X over Y because Z` comparisons, `we chose / decided /
   picked / went with`, and `fixed by / resolved by` resolution markers.
2. Questions, negations, hypotheticals, and short fragments are filtered
   out so chitchat never lands in the store.
3. Each surviving sentence becomes a `Decision` memory under the active
   backend (JSON file or vault) with a provenance footer carrying the
   PR number, commit SHAs, and capture timestamp.
4. Subject-exact-match dedupe within scope means two agents capturing
   the same decision in the same workspace produce one memory, not two.

Pair this with `KOSHI_MEMORY_VAULT` and Git, and the next developer (or
the next agent session) recalls the decision the instant they ask about
the cache layer — they don't get to repeat the incident.

To make agents call it reliably, paste the 30-line snippet at
[`docs/copilot-instructions-snippet.md`](../../docs/copilot-instructions-snippet.md)
into your repo's `.github/copilot-instructions.md` (or your team's
equivalent agent-steering doc). Without that hint, agents tend to forget
to call the tool; with it, capture-on-turn-end becomes part of "done".

### 4. Track team quality

```
koshi_register_team(teamId="platform-eng", name="Platform Engineering",
                    tokenBudget=16384, qualityTarget=0.75)

# After each AI interaction:
koshi_score_turn(teamId="platform-eng",
                 retrievedChunks=5, budgetUtilization=0.78,
                 cacheRatio=0.62, latencyMs=2400, userRating=4)

# Periodically:
koshi_team_dashboard(teamId="platform-eng")
koshi_analyze_feedback(teamId="platform-eng")
```

The feedback loop surfaces concrete config tweaks (e.g. _"increase topK from 5 → 7,
weakest dimension is retrieval"_).

---

## How It Works

```
┌────────────────────────────────────────────────────────────┐
│       MCP Client  (Claude Code, Copilot CLI, Cursor, ...)  │
└──────────────────────────┬─────────────────────────────────┘
                           │ stdio (JSON-RPC 2.0)
                           ▼
┌────────────────────────────────────────────────────────────┐
│       koshi-mcp  (this binary, ~40 MB self-contained)      │
│                                                            │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────┐    │
│  │  Retrieval   │  │   Context    │  │     Memory     │    │
│  │  Tools (5)   │  │  Tools (3)   │  │   Tools (9)    │    │
│  └──────┬───────┘  └──────┬───────┘  └────────┬───────┘    │
│         │                 │                    │           │
│         ▼                 ▼                    ▼           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                   Koshi.Core                        │   │
│  │  • FixedSizeChunker   (512-token windows, 50 over)  │   │
│  │  • KeywordRetriever   (BM25 inverted index)         │   │
│  │  • TokenCounter       (GPT-4 cl100k tokenizer)      │   │
│  │  • ContextCompiler    (4 positioning strategies)    │   │
│  │  • QualityScorer      (5-dimension composite score) │   │
│  └─────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────┘
```

**Two-stage indexing pipeline:**

1. **Chunking** — `FixedSizeChunker` splits text into 512-token windows with 50-token overlap, using GPT-4's `cl100k` tokenizer for accurate counts.
2. **BM25 indexing** — `KeywordRetriever` builds an inverted index with TF-IDF weighting (the same algorithm used by Elasticsearch / Lucene).

**Why no embeddings?** Embeddings need either an API key (cost, latency, privacy)
or a local GPU (deployment friction). BM25 ships in the binary, runs offline,
and on the included evaluation corpus achieves **MRR 0.874 / Recall@5 0.914** —
competitive with vector retrieval for code and documentation use-cases.

---

## Security & Privacy

Koshi runs locally and **never** sends data to a third-party. By design:

- ✅ **No network calls** — fully offline
- ✅ **No telemetry** — no analytics, no phone-home
- ✅ **Stdio transport only** — no listening ports
- ✅ **Logs to stderr only** — stdout is reserved for MCP JSON-RPC

**Disk-indexing safety guards** (in `koshi_index_directory`):

- Hidden directories skipped (`.git`, `.aws`, `.azure`, `.ssh`, `.gnupg`, …)
- Build output skipped (`bin`, `obj`, `node_modules`, `dist`, `target`, `.next`, …)
- Secret patterns skipped (`.env*`, `secrets.*`, `credentials.*`, `id_rsa`, …)
- Sensitive extensions skipped (`.pem`, `.key`, `.pfx`, `.p12`, `.crt`, `.keystore`, …)
- Symlinks/reparse points not followed
- Files larger than `maxFileSizeKb` (default 256 KB) skipped
- Hard cap of `maxFiles` (default 5,000) per index operation

⚠️ **Indexed content may be returned to your MCP client and the underlying LLM.**
Review the file list with `koshi_list_indexed` after indexing if your project
contains sensitive material.

---

## Comparison

| Feature | Koshi | [@mcp/memory](https://github.com/modelcontextprotocol/servers/tree/main/src/memory) | [@mcp/filesystem](https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem) | RAG SaaS |
|---|:---:|:---:|:---:|:---:|
| BM25 retrieval | ✅ | ❌ | ❌ | ✅ |
| Token budgeting & cache positioning | ✅ | ❌ | ❌ | ❌ |
| Typed memory (facts, decisions, patterns) | ✅ | ⚠️ graph only | ❌ | ⚠️ |
| Per-team quality scoring | ✅ | ❌ | ❌ | ❌ |
| Works offline (no keys) | ✅ | ✅ | ✅ | ❌ |
| MIT licensed, single binary | ✅ | ✅ | ✅ | ❌ |
| Setup time | ~60 sec | ~60 sec | ~60 sec | hours |
| Cost | free | free | free | $$$ |

---

## Troubleshooting

| Symptom | Fix |
|---------|------|
| `command not found: koshi-mcp` | Make sure your `dotnet tools` directory is on `PATH`. On macOS/Linux: `export PATH="$PATH:$HOME/.dotnet/tools"`. On Windows it's `%USERPROFILE%\.dotnet\tools`. |
| `❌ No documents indexed` from `koshi_search` | Either set `KOSHI_INDEX_PATH` in your client config, or call `koshi_index_directory(path="...")` first. |
| `❌ No supported, readable files found in: ...` | The path is empty, contains only excluded files (binaries, build output, hidden dirs), or no files match the pattern. Check with `koshi_list_indexed` and try a wider `pattern`. |
| MCP client can't connect | Check that `koshi-mcp` runs on its own (`koshi-mcp` then send a JSON-RPC line). Logs appear on stderr; nothing should print on stdout until a request arrives. |
| Memories disappear on restart | Set `KOSHI_MEMORY_FILE` to an absolute path. The file is created automatically. |
| Index re-built on every restart (slow) | Set `KOSHI_INDEX_FILE` to an absolute path. The snapshot auto-loads on first search; if the source directory has changed since it was saved, the fingerprint check discards it and a fresh re-index runs. |
| Vulnerability warning during install | The MCP package itself is clean. Some sibling demo projects in the source repo pull in older transitive packages — these never reach `koshi-mcp`. |

Use `koshi_health` from any MCP client to quickly inspect runtime configuration.

---

## Development

```bash
git clone https://github.com/jsharma1105/Koshi
cd koshi

# Build
dotnet restore
dotnet build src/Koshi.Mcp/Koshi.Mcp.csproj -c Release

# Run unit tests
dotnet test --no-build

# Pack the NuGet tool
dotnet pack src/Koshi.Mcp/Koshi.Mcp.csproj -c Release -o nupkg

# Smoke test the MCP protocol end-to-end
dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release

# Install your local build over the published one
dotnet tool install --global --add-source ./nupkg Koshi.Mcp
```

### Project layout

```
src/
├── Koshi.Core/        # The retrieval/context/memory/quality engine
├── Koshi.Mcp/         # ← This MCP server (publishes to NuGet)
└── Koshi.Agents/      # Sub-agent persona installer (publishes to NuGet)
tests/
├── Koshi.Core.Tests/      # 359 xUnit tests
└── Koshi.Mcp.SmokeTest/   # End-to-end JSON-RPC smoke harness
```

### Releasing

Releases are automated by [`.github/workflows/release.yml`](../../.github/workflows/release.yml). To cut a new version:

1. Bump `<Version>` in both `src/Koshi.Mcp/Koshi.Mcp.csproj` and `src/Koshi.Agents/Koshi.Agents.csproj`.
2. Update `version` in `plugin.json` and `.claude-plugin/plugin.json` to match.
3. Add a new section at the top of [`CHANGELOG.md`](../../CHANGELOG.md).
4. Open a PR titled `release vX.Y.Z`. Merge once CI is green.
5. Tag `main` with `vX.Y.Z` and push the tag:
   ```bash
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```
6. The release workflow builds, tests, packs both packages, pushes to NuGet, and creates a GitHub release with auto-generated notes.

To pack locally for testing without publishing:

```bash
dotnet pack src/Koshi.Mcp -c Release -o nupkg -p:Version=X.Y.Z
dotnet tool install --global --add-source ./nupkg Koshi.Mcp --version X.Y.Z
```

---

## License

[MIT](LICENSE) © Koshi Contributors. See [CHANGELOG.md](../../CHANGELOG.md) for release notes.
