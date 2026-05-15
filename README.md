# Koshi

> Context engineering toolkit for AI agents. Build it, measure it, ship it.

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg?label=Koshi.Mcp&color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Koshi.Mcp.svg?color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![Build](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml)
[![CodeQL](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
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

```bash
# 1. Install the MCP server
dotnet tool install --global Koshi.Mcp

# 2. Tell Claude Code / Copilot CLI about it (one-time JSON edit)
#    See AGENTS.md for the exact snippet per client.

# 3. (Optional) Install the 5 sub-agent personas
dotnet tool install --global Koshi.Agents
koshi-agents install --client both
```

## Why you'd use this instead of writing it yourself

- **It's done.** 20 tools, 89 unit tests, .NET 10, MIT.
- **It's local.** No API keys, no cloud, no telemetry phoning home.
- **It composes.** The sub-agent personas know which tools each persona is allowed to call — so your agents don't accidentally clobber memory while searching.
- **It's measurable.** The telemetry pillar isn't an afterthought — it's a first-class set of tools.

[**→ Get started in AGENTS.md**](AGENTS.md)

---

## How it's shipped

- 📦 [`Koshi.Mcp`](src/Koshi.Mcp/) — A **Model Context Protocol** server you install
  with `dotnet tool install -g Koshi.Mcp`. Plugs into Claude Code, Copilot CLI,
  Cursor, Windsurf, and every other MCP-compatible client.
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
├── Koshi.Mcp/                      MCP server (NuGet: Koshi.Mcp)
└── Koshi.Agents/                   Sub-agent personas installer (NuGet: Koshi.Agents)
tests/
├── Koshi.Core.Tests/               89 xUnit unit tests
└── Koshi.Mcp.SmokeTest/            End-to-end JSON-RPC smoke harness
```

## Build everything

```bash
dotnet restore
dotnet build -c Release
dotnet test  --no-build
```

## License

[MIT](LICENSE)
