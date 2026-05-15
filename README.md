# Koshi

> Context engineering toolkit for AI agents. Build it, measure it, ship it.

[![NuGet](https://img.shields.io/nuget/v/Koshi.Mcp.svg?label=Koshi.Mcp&color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Koshi.Mcp.svg?color=004880)](https://www.nuget.org/packages/Koshi.Mcp/)
[![Build](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml)
[![CodeQL](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dot.net)
[![MCP](https://img.shields.io/badge/MCP-1.0-blue.svg)](https://modelcontextprotocol.io)

Koshi (講師, "instructor") is a .NET 10 toolkit that solves four hard problems
that every serious AI-agent system eventually hits:

1. **Retrieval** — Find the right chunks of code/docs to feed the model
2. **Memory** — Persist facts, decisions, and patterns across sessions
3. **Context Engineering** — Pack the prompt window for accuracy *and* cache reuse
4. **Quality Scoring** — Measure and improve agent quality per team, over time

It is shipped as:

- 📦 [`Koshi.Mcp`](src/Koshi.Mcp/) — A **Model Context Protocol** server you install
  with `dotnet tool install -g Koshi.Mcp`. Plugs into Claude Code, Copilot CLI,
  Cursor, Windsurf, and every other MCP-compatible client.
- 🛠️ [`Koshi.Core`](src/Koshi.Core/) — The underlying library you can embed in
  your own .NET agents/services.

➡️ **Most people want the MCP server. See [`src/Koshi.Mcp/README.md`](src/Koshi.Mcp/README.md).**

---

## Repository layout

```
src/
├── Koshi.Core/                     The engine: retrieval, context, memory, quality
├── Koshi.Mcp/                      MCP server (NuGet: Koshi.Mcp)
├── Koshi.Retrieval.Demo/           Demo: BM25 + RRF + reranking
├── Koshi.Memory.Demo/              Demo: typed memory store with time-decay
├── Koshi.Context.Demo/             Demo: budget-aware context compilation
├── Koshi.Harness.Demo/             Demo: end-to-end multi-turn session pipeline
└── Koshi.Team.Demo/                Demo: per-team quality scoring + dashboards
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
