# Contributing to Koshi

Thank you for your interest in Koshi! This document explains how to get a working
development environment, the conventions the project follows, and how to land a
change.

## Quick development setup

```bash
git clone https://github.com/jsharma1105/Koshi
cd Koshi

# .NET 10 SDK or runtime is required
dotnet --info

# Restore + build everything
dotnet restore
dotnet build -c Release

# Run the unit tests
dotnet test --no-build -c Release

# Optional: smoke-test the MCP protocol end-to-end
dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release
```

## Project layout

```
src/
├── Koshi.Core/        ← Engine: retrieval, memory, context, quality
├── Koshi.Mcp/         ← MCP server (NuGet: Koshi.Mcp, command: koshi-mcp)
├── Koshi.Agents/      ← Persona installer (NuGet: Koshi.Agents, command: koshi-agents)
├── Koshi.Cli/         ← Phase-1 retrieval demo
├── Koshi.Eval/        ← Evaluation harness
├── Koshi.Tuner/       ← Configuration tuner
└── Koshi.*.Demo/      ← Walk-through demos for each engineering layer
tests/
├── Koshi.Core.Tests/      ← xUnit unit tests
└── Koshi.Mcp.SmokeTest/   ← End-to-end JSON-RPC harness
```

## How to contribute

1. **Open an issue first** for anything bigger than a bug fix. Discuss the design
   before opening a PR — this avoids wasted work.
2. **Fork** the repository.
3. **Branch** off `main` with a descriptive name (`feat/<short>`, `fix/<short>`).
4. **Write tests** for new behavior. Koshi has 89+ unit tests in `Koshi.Core.Tests`
   — add to them.
5. **Run the full build** locally: `dotnet build -c Release && dotnet test`.
6. **Open a PR** against `main`. Fill out the PR template. Link the issue.

## Coding conventions

- **C# 13 / .NET 10**: prefer `record`, primary constructors, collection expressions,
  `required` properties, pattern matching, file-scoped types.
- **Nullable reference types**: every project has `<Nullable>enable</Nullable>`.
- **Async**: `async`/`await` only. No `.Result`, no `.Wait()`, no `Task.Run` for I/O.
- **Determinism**: avoid hidden ambient state. Prefer pure functions in the engine.
- **Tests**: xUnit. Naming convention is `Method_Scenario_ExpectedResult`.

## What we will NOT accept

- New runtime dependencies on cloud APIs in `Koshi.Core` or `Koshi.Mcp`. Koshi is
  100% offline by design — embeddings, networked rerankers, etc. belong in
  optional packages, never the default install.
- Telemetry, analytics, or "phone home" code anywhere.
- Vendored copies of `Microsoft.Extensions.AI` or other Microsoft packages.
  Reference them via NuGet.

## Reporting security issues

**Do not** open a public issue for security problems. See [SECURITY.md](./SECURITY.md).

## Releases

Releases are triggered by pushing a `v*.*.*` tag. The release workflow packs and
publishes `Koshi.Mcp` and `Koshi.Agents` to NuGet automatically. Maintainers:
follow the checklist in [`src/Koshi.Mcp/README.md`](src/Koshi.Mcp/README.md#releasing).

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](./LICENSE).
