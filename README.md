# Koshi

> Context engineering toolkit for AI agents. Build it, measure it, ship it.

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg?label=Koshi.Mcp&color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Koshi.Mcp.svg?color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![PyPI](https://img.shields.io/pypi/v/koshi.svg?label=koshi%20(PyPI)&color=3776AB)](https://pypi.org/project/koshi/)
[![PyPI Downloads](https://img.shields.io/pypi/dm/koshi.svg?color=3776AB)](https://pypi.org/project/koshi/)
[![Build](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml)
[![CodeQL](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-3776AB.svg)](https://www.python.org/)
[![MCP](https://img.shields.io/badge/MCP-1.0-blue.svg)](https://modelcontextprotocol.io)

## What Koshi is, in three sentences

Koshi (講師, "instructor") is a **context-engineering toolkit for AI agents**, shipped as a Model Context Protocol (MCP) server. It bundles four pillars — **retrieval**, **memory**, **context-packing**, and **team telemetry** — into a single .NET 10 binary with **20 MCP tools** and **zero cloud dependencies**. Install it as a `dotnet tool`, point Claude Code or GitHub Copilot CLI at it, and stop re-implementing the same agent plumbing in every project.

## The four pillars

| Pillar | Tools | What it solves |
|---|---|---|
| 🔎 **Retrieval** | `koshi_index`, `koshi_index_directory`, `koshi_search`, `koshi_list_indexed`, `koshi_clear_index` | BM25 over a persistent token-aware index. Index code/docs once, search forever. |
| 🧠 **Memory** | `koshi_remember`, `koshi_recall`, `koshi_memory_stats`, `koshi_forget`, `koshi_clear_memories` | Durable facts, decisions, patterns that survive across sessions. |
| 📦 **Context** | `koshi_compile_context`, `koshi_token_count`, `koshi_budget_plan` | Token-budgeted prompt assembly. Give it a budget; it ranks, dedupes, trims. |
| 📊 **Telemetry** | `koshi_register_team`, `koshi_score_turn`, `koshi_team_dashboard`, `koshi_analyze_feedback`, `koshi_list_teams` | Score agent turns. See where your team is bleeding tokens or accuracy. |

Plus 2 diagnostics tools (`koshi_version`, `koshi_health`). **20 total.**

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

## Why you'd use this instead of writing it yourself

- **It's done.** 20 tools, 89 unit tests, .NET 10, MIT.
- **It's local.** No API keys, no cloud, no telemetry phoning home. Same wire format and on-disk format whether you reach it via `pip`, `dotnet tool`, or the raw AOT binary.
- **It's friction-free.** `pip install koshi` works from a fresh Python 3.10 environment — no .NET install — and auto-fetches a native AOT binary verified against SHA-256 hashes baked into the wheel.
- **It composes.** The sub-agent personas know which tools each persona is allowed to call — so your agents don't accidentally clobber memory while searching.
- **It's measurable.** The telemetry pillar isn't an afterthought — it's a first-class set of tools.

[**→ Get started in AGENTS.md**](AGENTS.md) · [**→ Python quickstart**](docs/python-quickstart.md)

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
├── Koshi.Core.Tests/               89 xUnit unit tests
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

- [**Python quickstart**](docs/python-quickstart.md) — pip install, first 20 lines of code, all 20 tools indexed.
- [**MCP server reference**](src/Koshi.Mcp/README.md) — every tool, every argument, every client snippet.
- [**Sub-agent personas**](AGENTS.md) — librarian, memory-keeper, context-packer, quality-coach, orchestrator.
- [**Security policy**](SECURITY.md) — what's in scope, what's not, how to report.
- [**Changelog**](CHANGELOG.md) — every release, every change.
- [**Engineering notes**](docs/context-memory-harness-engineering.md) — the design of memory + retrieval + context + telemetry.

## License

[MIT](LICENSE)
