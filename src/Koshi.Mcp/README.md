# Koshi MCP Server

> **Koshi** (講師, "instructor") is a Model Context Protocol server that brings
> retrieval, context engineering, persistent memory, and quality scoring to any
> MCP-compatible AI client. **100% offline. No API keys. No embeddings. No cloud.**

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg)](https://www.nuget.org/packages/Koshi.Mcp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/jsharma1105/Koshi/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
[![MCP](https://img.shields.io/badge/MCP-1.0-blue.svg)](https://modelcontextprotocol.io)

Koshi turns any MCP client (Claude Code, GitHub Copilot CLI, Cursor, Windsurf,
Microsoft Agency) into a context-engineering powerhouse. It indexes your
codebase with BM25, packs context windows for optimal cache reuse, stores
typed memories shared with your team via Git, and scores every AI interaction
so you can tune over time.

## Why Koshi

- 🔍 **Index code, docs, RFCs, ADRs** — BM25 retrieval inline from your AI client.
- 🧠 **Remember decisions across sessions** — typed memories with confidence and provenance.
- 👥 **Share decisions with the team** — point at a Git directory; teammates inherit on `git pull`.
- 📦 **Pack the prompt window** — token budgets and cache-optimised positioning.
- 📊 **Score every turn** — quality dashboard per team, with concrete tuning recommendations.

Full feature matrix vs `@mcp/memory`, `@mcp/filesystem`, and managed RAG:
[docs/comparison.md](https://github.com/jsharma1105/Koshi/blob/main/docs/comparison.md).

## Quick start

The shell installer downloads a verified native binary, registers Koshi with
every detected MCP client, installs the five sub-agent personas, and
optionally indexes the current directory:

```sh
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.sh | sh

# Windows (PowerShell)
irm https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.ps1 | iex
```

Prefer to install the tool yourself? Pick one path:

```sh
# Python users (no .NET install required)
pip install koshi

# .NET 10 SDK or runtime
dotnet tool install --global Koshi.Mcp
```

Other install paths (manual AOT binary, per-version pinning, air-gapped):
[docs/install.md](https://github.com/jsharma1105/Koshi/blob/main/docs/install.md).

After install, restart your MCP client once and try:

> _"Use Koshi to search the codebase for how authentication works."_
> _"Remember that we use ULIDs for all entity IDs (decision)."_
> _"What did we decide about authentication last sprint?"_

## Tools at a glance

24 tools across four pillars plus 2 diagnostics. Full per-tool reference:
[docs/tools.md](https://github.com/jsharma1105/Koshi/blob/main/docs/tools.md).

| Pillar | Count | Headline tools |
|---|:---:|---|
| 🔍 Retrieval | 5 | `koshi_index_directory`, `koshi_search`, `koshi_list_indexed` |
| 📦 Context | 3 | `koshi_compile_context`, `koshi_token_count`, `koshi_budget_plan` |
| 🧠 Memory | 9 | `koshi_remember`, `koshi_recall`, `koshi_capture_turn`, vault export/import/sync |
| 👥 Team & quality | 5 | `koshi_register_team`, `koshi_score_turn`, `koshi_team_dashboard` |
| 🩺 Diagnostics | 2 | `koshi_version`, `koshi_health` |

## Client setup

`koshi-mcp init` writes the `mcpServers.koshi` entry for Claude Code and
GitHub Copilot CLI automatically. For Cursor, Windsurf, Microsoft Agency,
and any other stdio MCP client, see the per-client snippets:
[docs/client-setup.md](https://github.com/jsharma1105/Koshi/blob/main/docs/client-setup.md).

The minimal config every client needs:

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": { "KOSHI_PROJECT_ROOT": "/absolute/path/to/your/project" }
    }
  }
}
```

## Configuration

Koshi is configured exclusively through environment variables. Since v0.6.0
every path env var derives a sensible default from `KOSHI_PROJECT_ROOT`, so
most users never need to set anything beyond the project root.

The three you are most likely to touch:

| Env var | Default | Purpose |
|---|---|---|
| `KOSHI_PROJECT_ROOT` | `cwd` | Base for every other path. **Set explicitly in Claude Desktop.** |
| `KOSHI_INDEX_PATH` | `<root>` | Directory that `koshi_search` auto-indexes on first call. |
| `KOSHI_MEMORY_VAULT` | _unset_ | Git-friendly Markdown-vault memory backend. |

All 11 env vars, vault flavours, and safety limits:
[docs/configuration.md](https://github.com/jsharma1105/Koshi/blob/main/docs/configuration.md).

## How it works

```
┌──────────────────────────────────────────────┐
│ MCP Client (Claude / Copilot CLI / Cursor …) │
└────────────────────┬─────────────────────────┘
                     │ stdio (JSON-RPC 2.0)
                     ▼
┌──────────────────────────────────────────────┐
│ koshi-mcp  (~40 MB self-contained binary)    │
│  Retrieval │ Context │ Memory │ Team         │
│      └──────────┬───────────────┘            │
│                 ▼                            │
│  Koshi.Core: BM25 + tokenizer + memory store │
└──────────────────────────────────────────────┘
```

- **Stdio MCP server** — no ports, no network, no cloud.
- **BM25 retrieval** — TF-IDF inverted index over chunked text. Same algorithm as Elasticsearch and Lucene; MRR 0.874 / Recall@5 0.914 on the included eval corpus.
- **Typed memory** — facts, decisions, patterns, preferences. Persisted to JSON or a Git-shared Markdown vault.

## Security & privacy

Koshi runs locally and **never** sends data to a third-party. By design:

- ✅ **No network calls** — fully offline.
- ✅ **No telemetry** — no analytics, no phone-home.
- ✅ **Stdio transport only** — no listening ports.
- ✅ **Logs to stderr only** — stdout is reserved for MCP JSON-RPC.

`koshi_index_directory` skips hidden directories (`.git`, `.ssh`, …) and
build output (`bin`, `node_modules`, …). It also skips secret filename
patterns (`.env*`, `id_rsa`, …), sensitive extensions (`.pem`, `.pfx`, …),
symlinks, oversize files (`maxFileSizeKb`, default 256 KB), and stops at
`maxFiles` (default 5,000) per operation.

⚠️ **Indexed content may be returned to your MCP client and the underlying
LLM.** Review the file list with `koshi_list_indexed` after indexing if your
project contains sensitive material.

## Troubleshooting

The three things to check first:

| Symptom | Fix |
|---|---|
| `command not found: koshi-mcp` | Ensure your tool prefix is on `PATH` (`~/.local/bin`, `~/.dotnet/tools`, or `%USERPROFILE%\.dotnet\tools`). |
| MCP client cannot connect | Run `koshi-agents doctor` — it prints the exact snippet to paste if anything is missing. |
| `❌ No documents indexed` from `koshi_search` | Either set `KOSHI_INDEX_PATH`, or call `koshi_index_directory(path="...")` first. |

Full troubleshooting guide (server, indexing, memory, Python wrapper errors):
[docs/troubleshooting.md](https://github.com/jsharma1105/Koshi/blob/main/docs/troubleshooting.md).

## Walkthroughs

Six worked examples — index + search, budget planning, remember + recall,
Git-shared vaults, turn-end auto-capture, team scoring:
[docs/walkthroughs.md](https://github.com/jsharma1105/Koshi/blob/main/docs/walkthroughs.md).

## Next steps

After install, ask your agent any project question. It will recall what it
knows and capture new decisions as it goes:

```sh
koshi-agents doctor   # verify every detected client sees the server
```

## Known issues

Hitting something unexpected? Search
[open bugs](https://github.com/jsharma1105/Koshi/issues?q=is%3Aissue+is%3Aopen+label%3Abug)
before assuming it's you.

## Develop & contribute

Build, test, release, project layout:
[docs/development.md](https://github.com/jsharma1105/Koshi/blob/main/docs/development.md)
and [CONTRIBUTING.md](https://github.com/jsharma1105/Koshi/blob/main/CONTRIBUTING.md).

## License

MIT — see [LICENSE](https://github.com/jsharma1105/Koshi/blob/main/LICENSE).
