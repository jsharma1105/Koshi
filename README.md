# Koshi

> Context engineering toolkit for AI agents. Build it, measure it, ship it.

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg?label=Koshi.Mcp&color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Koshi.Mcp.svg?color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![NuGet](https://img.shields.io/nuget/v/Koshi.Agents.svg?label=Koshi.Agents&color=512BD4)](https://www.nuget.org/packages/Koshi.Agents/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Koshi.Agents.svg?color=512BD4)](https://www.nuget.org/packages/Koshi.Agents/)
[![PyPI](https://img.shields.io/pypi/v/koshi.svg?label=koshi%20(PyPI)&color=3776AB)](https://pypi.org/project/koshi/)
[![PyPI Downloads](https://img.shields.io/pypi/dm/koshi.svg?color=3776AB)](https://pypi.org/project/koshi/)
[![Build](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml)
[![CodeQL](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-3776AB.svg)](https://www.python.org/)
[![MCP](https://img.shields.io/badge/MCP-1.0-blue.svg)](https://modelcontextprotocol.io)

## What Koshi is, in three sentences

Koshi (講師, "instructor") is a **context-engineering toolkit for AI agents**, shipped as a Model Context Protocol (MCP) server. It bundles four pillars — **retrieval**, **memory**, **context-packing**, and **team telemetry** — into a single .NET 10 binary with **24 MCP tools** and **zero cloud dependencies**. Install it as a `dotnet tool`, point Claude Code or GitHub Copilot CLI at it, and stop re-implementing the same agent plumbing in every project.

## The four pillars

| Pillar | Tools | What it solves |
|---|---|---|
| 🔎 **Retrieval** | `koshi_index`, `koshi_index_directory`, `koshi_search`, `koshi_list_indexed`, `koshi_clear_index` | BM25 over a persistent token-aware index. Index code/docs once, search forever. |
| 🧠 **Memory** | `koshi_remember`, `koshi_recall`, `koshi_memory_stats`, `koshi_forget`, `koshi_clear_memories`, `koshi_capture_turn`, `koshi_memory_export_to_vault`, `koshi_memory_import_from_vault`, `koshi_memory_sync_vault` | Durable facts, decisions, patterns that survive across sessions — optionally as one Markdown file per memory in a Git-friendly **vault** ([docs](docs/vault-mode.md)). `koshi_capture_turn` lets the agent auto-extract decisions from a turn summary ([snippet](docs/copilot-instructions-snippet.md)). |
| 📦 **Context** | `koshi_compile_context`, `koshi_token_count`, `koshi_budget_plan` | Token-budgeted prompt assembly. Give it a budget; it ranks, dedupes, trims. |
| 📊 **Telemetry** | `koshi_register_team`, `koshi_score_turn`, `koshi_team_dashboard`, `koshi_analyze_feedback`, `koshi_list_teams` | Score agent turns. See where your team is bleeding tokens or accuracy. |

Plus 2 diagnostics tools (`koshi_version`, `koshi_health`). **24 total.**

The vault backend supports four file-layout flavors via `KOSHI_VAULT_FLAVOR=obsidian|foam|logseq|dendron` so your existing notes app keeps working unchanged.

## 60-second install

Pick the path that matches your stack:

### Python — `pip install koshi`

```bash
pip install koshi
python -c "from koshi import Client
with Client() as k:
    print(k.version())"
```

No `.NET install` required. First call auto-downloads a native AOT binary
(~15 MB) into your user cache, verified by SHA-256 against hashes baked into
the wheel. See [`python/README.md`](python/README.md) for the full API.

### .NET — `dotnet tool install`

```bash
# 1. Install the MCP server
dotnet tool install --global Koshi.Mcp

# 2. Tell Claude Code / Copilot CLI about it (one-time JSON edit)
#    See AGENTS.md for the exact snippet per client.

# 3. (Optional) Install the 5 sub-agent personas
dotnet tool install --global Koshi.Agents
koshi-agents install --client both
```

### No runtime — download the AOT binary

Every [GitHub release](https://github.com/jsharma1105/Koshi/releases) ships
self-contained single-file `koshi-mcp` binaries for `linux-x64`, `linux-arm64`,
`osx-arm64`, `win-x64`, and `win-arm64`, plus matching `.sha256` sidecars and
a `manifest.json`. Run them on a clean machine without any .NET runtime
installed. (Intel Macs: use `dotnet tool install --global Koshi.Mcp`.)

```bash
# Linux x64 example — adapt RID for your platform
curl -L -o koshi-mcp \
  https://github.com/jsharma1105/Koshi/releases/latest/download/koshi-mcp-linux-x64
curl -L -o koshi-mcp.sha256 \
  https://github.com/jsharma1105/Koshi/releases/latest/download/koshi-mcp-linux-x64.sha256
sha256sum -c <(awk '{print $1"  koshi-mcp"}' koshi-mcp.sha256)
chmod +x koshi-mcp
./koshi-mcp --version  # prints "koshi-mcp X.Y.Z+<sha>" and exits
./koshi-mcp            # speaks MCP over stdio
```

### Project-root defaults (since v0.6.0)

Koshi writes its memory and index under `<project>/.koshi/` by default — the
project root is whatever directory the MCP server was launched from. Open
Copilot CLI in your project (say `~/myrepo` on macOS/Linux or
`C:\src\myrepo` on Windows), install Koshi, and your data lives at
`<project>/.koshi/memory.json` automatically. No env vars required for the
common case.

Override any path with the matching env var (see the [MCP server
reference](src/Koshi.Mcp/README.md#configuration) for the full table).
Relative env values resolve against `KOSHI_PROJECT_ROOT`. Run `koshi_health`
to see the resolved value + source for every path.

A `<root>/.koshi/.gitignore` is auto-created the first time Koshi writes
state under the default directory, so your memories and index don't get
committed by accident. Delete or edit it if you intentionally want to
track Koshi state in Git — we'll never overwrite it.

> ⚠️ **Claude Desktop:** launches MCP servers with cwd=`%USERPROFILE%`,
> not your project. Set `KOSHI_PROJECT_ROOT` explicitly in your
> `claude_desktop_config.json`. Copilot CLI and Cline launch servers with
> cwd=your project, so the defaults Just Work there.

## Why you'd use this instead of writing it yourself

- **It's done.** 24 tools, 359 unit tests, .NET 10, MIT. CodeQL clean (0 alerts).
- **It's local.** No API keys, no cloud, no telemetry phoning home. Same wire format and on-disk format whether you reach it via `pip`, `dotnet tool`, or the raw AOT binary.
- **It's friction-free.** `pip install koshi` works from a fresh Python 3.10 environment — no .NET install — and auto-fetches a native AOT binary verified against SHA-256 hashes baked into the wheel.
- **It composes.** The sub-agent personas know which tools each persona is allowed to call — so your agents don't accidentally clobber memory while searching.
- **It's measurable.** The telemetry pillar isn't an afterthought — it's a first-class set of tools.

[**→ Get started in AGENTS.md**](AGENTS.md) · [**→ Python quickstart**](docs/python-quickstart.md)

---

## Skip the regression: auto-capture decisions, share them across platforms

The single biggest reason teammates repeat the same regression is that the
*reasoning* behind a fix never leaves the original PR description. Koshi's
**`koshi_capture_turn`** tool (added in v0.8.0) closes that loop — the agent
itself stores the decision the moment it's made, and Git distributes it to
every other developer on every client.

### 1. The agent stores memory by itself

At the end of any non-trivial turn (a regression fix, an architectural choice,
a tricky workaround), the agent calls **one** tool:

```jsonc
koshi_capture_turn(
  summary: "Decision: switch the cache layer from in-memory to Redis because
            the in-process cache lost coherency across the 3 API replicas
            during the 2026-05-21 incident.",
  linked_pr: 123,
  linked_commits: ["a1b2c3d"]
)
```

Koshi runs a deterministic, pattern-based extractor over the summary
(no LLM in the loop — AOT-friendly, no network, no hidden cost), picks out
the decision-shape sentences, and persists each one as a `Decision` memory
with a provenance footer (`PR #123`, `commits a1b2c3d`, capture timestamp).
Questions, chitchat, negations, and hypothetical sentences are filtered out
so the store stays clean.

Paste the 30-line snippet at [`docs/copilot-instructions-snippet.md`](docs/copilot-instructions-snippet.md)
into your repo's `.github/copilot-instructions.md` (or `AGENTS.md`) and any
MCP-aware coding agent will call it reliably at the end of meaningful turns.

### 2. Share across **every** platform from one binary

Koshi is a single MCP server with **24 tools** — the *same* wire format and
on-disk format whether you reach it via `pip`, `dotnet tool`, or a raw AOT
binary. Configure it once per client; the memories your agent captures are
visible to all of them.

| Platform | One-line setup | Config file |
|---|---|---|
| **GitHub Copilot CLI** | `copilot mcp add koshi koshi-mcp --env KOSHI_MEMORY_VAULT=./team-memories` | `~/.copilot/mcp_config.json` |
| **Claude Code / Desktop** | edit JSON: `{ "mcpServers": { "koshi": { "command": "koshi-mcp" } } }` | `claude_desktop_config.json` / `.claude/settings.json` |
| **Cursor / Windsurf** | drop the same JSON into the workspace `mcp.json` | `.cursor/mcp.json` |
| **Microsoft Agency CLI** | `agency mcp local --command koshi-mcp` | `plugin.json` (shipped) |
| **Python host** | `pip install koshi` → `from koshi import Client` | none |

Full snippets per client live in [`src/Koshi.Mcp/README.md#client-setup`](src/Koshi.Mcp/README.md#client-setup).

### 3. Distribute the memories with Git, not Slack

Point `KOSHI_MEMORY_VAULT` at a directory and Koshi writes **one Markdown
file per memory** under a flavor-specific layout (Obsidian / Foam / Logseq /
Dendron — pick yours with `KOSHI_VAULT_FLAVOR`). Commit the directory and
your teammates inherit the same captured decisions on `git pull`:

```bash
export KOSHI_MEMORY_VAULT=./team-memories
# the agent captures a decision...
git add team-memories/ && git commit -m "chore(memory): cache-layer decision"
git push
```

A teammate who clones the repo and sets the same env var sees the memory
the next time they ask the agent _"why is the cache layer Redis here?"_ —
no Slack archaeology, no rerunning the regression to learn the answer.
External edits, deletes, and `git pull`s are picked up automatically via a
filesystem watcher; see [`docs/vault-mode.md`](docs/vault-mode.md) for the
full format spec and migration guide.

### Why this beats "just write it in the PR description"

| Problem with PR-description-only memory | How `koshi_capture_turn` + vault fixes it |
|---|---|
| Future devs don't read every old PR | The next agent session **recalls** the decision automatically on a relevant query |
| Slack threads expire / are siloed | One Markdown file per decision, in Git, searchable forever |
| Each client re-implements memory | One MCP server, every client (Copilot CLI, Claude, Cursor, Windsurf, Agency, Python) reads the same store |
| Easy to forget to write it down | The agent does it at turn-end as part of "done" |

---

## How it's shipped

- 📦 [`Koshi.Mcp`](src/Koshi.Mcp/) — A **Model Context Protocol** server you install
  with `dotnet tool install -g Koshi.Mcp`. Plugs into Claude Code, Copilot CLI,
  Cursor, Windsurf, and every other MCP-compatible client. Also distributed as
  Native AOT single-file binaries on every GitHub release (no .NET runtime
  required).
- 🐍 [`koshi`](python/) — **Python client** on PyPI. `pip install koshi` and
  talk to the same engine over JSON-RPC from Python; the binary downloads
  itself on first use.
- 🛠️ [`Koshi.Core`](src/Koshi.Core/) — The underlying library you can embed in
  your own .NET agents/services.
- 🤖 [`Koshi.Agents`](src/Koshi.Agents/) — Five Koshi-aware sub-agent personas
  (architect, retriever, memory-keeper, context-packer, quality-scorer) installable
  into Claude Code and GitHub Copilot CLI with one command.

➡️ **Most people want the MCP server. See [`src/Koshi.Mcp/README.md`](src/Koshi.Mcp/README.md).**

## Repository layout

```
src/
├── Koshi.Core/                     The engine: retrieval, context, memory, telemetry
├── Koshi.Mcp/                      MCP server (NuGet: Koshi.Mcp; AOT binaries on GH releases)
└── Koshi.Agents/                   Sub-agent personas installer (NuGet: Koshi.Agents)
python/
└── src/koshi/                      Python client (PyPI: koshi)
tests/
├── Koshi.Core.Tests/               359 xUnit unit tests
└── Koshi.Mcp.SmokeTest/            End-to-end JSON-RPC smoke harness
scripts/
└── inject-manifest.py              Release-time hash injector for the Python wheel
```

## Build everything

```bash
dotnet restore
dotnet build -c Release
dotnet test  --no-build
```

For Python-package development, see [`CONTRIBUTING.md`](CONTRIBUTING.md#python-package-development).

## Documentation

- [**Python quickstart**](docs/python-quickstart.md) — pip install, first 20 lines of code, all 24 tools indexed.
- [**MCP server reference**](src/Koshi.Mcp/README.md) — every tool, every argument, every client snippet.
- [**Vault mode**](docs/vault-mode.md) — share memories across teams via a Git-backed Markdown vault (`KOSHI_MEMORY_VAULT`).
- [**Sub-agent personas**](AGENTS.md) — librarian, memory-keeper, context-packer, quality-coach, orchestrator.
- [**Security policy**](SECURITY.md) — what's in scope, what's not, how to report.
- [**Changelog**](CHANGELOG.md) — every release, every change.
- [**Engineering notes**](docs/context-memory-harness-engineering.md) — the design of memory + retrieval + context + telemetry.

## License

[MIT](LICENSE)
