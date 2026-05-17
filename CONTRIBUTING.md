# Contributing to Koshi

Thank you for your interest in Koshi! This document explains how to get a working
development environment, the conventions the project follows, and how to land a
change.

## Quick development setup

### .NET (engine + MCP server + Agents installer)

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

# Optional: smoke-test the MCP protocol end-to-end (JIT)
dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release

# Optional: smoke-test the MCP protocol against a Native AOT binary
dotnet publish src/Koshi.Mcp -c Release -r linux-x64 -p:PublishAot=true \
  -o publish-aot/linux-x64
dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- \
  --exe publish-aot/linux-x64/koshi-mcp --expected-version 0.4.0
```

### Python package development

The Python client lives under [`python/`](python/) and is zero-runtime-deps
(stdlib only). Development needs Python 3.10+ with `pip` and a few dev tools.

```bash
cd python

# Create a virtual environment
python -m venv .venv
. .venv/bin/activate          # macOS / Linux
# .venv\Scripts\Activate.ps1   # Windows PowerShell

# Install with dev extras (pytest, ruff, mypy, build, twine)
pip install -e ".[dev]"

# Run the test suite (27 unit tests, fast; --slow runs 2 binary-download tests)
pytest -m "not slow"

# Lint + type-check
ruff check src tests
mypy --strict src

# Build a wheel + sdist
python -m build
twine check dist/*
```

The Python tests use a stub `koshi-mcp` binary (no real .NET install needed).
The 2 `@pytest.mark.slow` tests do a live binary resolution and are run in CI
only.

## Project layout

```
src/
├── Koshi.Core/        ← Engine: retrieval, memory, context, quality
├── Koshi.Mcp/         ← MCP server (NuGet: Koshi.Mcp, command: koshi-mcp)
└── Koshi.Agents/      ← Persona installer (NuGet: Koshi.Agents, command: koshi-agents)
python/
├── src/koshi/         ← Python client (PyPI: koshi)
└── tests/             ← pytest unit + 2 slow integration tests
tests/
├── Koshi.Core.Tests/      ← xUnit unit tests (89 tests today)
└── Koshi.Mcp.SmokeTest/   ← End-to-end JSON-RPC smoke harness (JIT + AOT)
scripts/
└── inject-manifest.py ← Release-time SHA-256 injector for the Python wheel
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
- **AOT-clean**: `src/Koshi.Mcp` has `<IsAotCompatible>true</IsAotCompatible>` and
  an exhaustive `WarningsAsErrors` for trim/AOT analyzers (IL2026–IL3056). New
  JSON shapes must be added to `Koshi.Mcp.Internal.KoshiJsonContext` and any new
  tool class must be registered with explicit `WithTools<T>()` in `Program.cs` —
  not the reflection-based `WithToolsFromAssembly()`.
- **Async**: `async`/`await` only. No `.Result`, no `.Wait()`, no `Task.Run` for I/O.
- **Determinism**: avoid hidden ambient state. Prefer pure functions in the engine.
- **Tests**: xUnit. Naming convention is `Method_Scenario_ExpectedResult`.
- **Python**: `ruff check` clean, `mypy --strict` clean, type hints on every
  public function signature. Zero runtime dependencies — every byte is stdlib.

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

Releases are triggered by pushing a `v*.*.*` tag. The release workflow runs as a
4-job DAG:

1. **`nuget`** — packs `Koshi.Mcp` + `Koshi.Agents` and publishes to NuGet.
2. **`aot`** — matrix of 6 RIDs (`linux-x64`, `linux-arm64`, `osx-x64`,
   `osx-arm64`, `win-x64`, `win-arm64`). Each produces a self-contained
   single-file `koshi-mcp` and its `.sha256` sidecar. `fail-fast: false`.
3. **`release`** — single writer that hard-gates on all 6 AOT artifacts being
   present, builds a unified `manifest.json` with cross-platform SHA-256
   hashes (computed in one Linux shell for consistency), and uploads every
   binary + sidecar + manifest to the GitHub release.
4. **`pypi`** — runs `scripts/inject-manifest.py` to bake the release SHA-256
   set into the wheel's `_manifest.py`, builds wheel + sdist, runs
   `twine check`, and publishes to PyPI. Gated on the `PUBLISH_PYPI` repo
   variable being `true` *and* the `pypi-release` environment (which requires
   the `PYPI_API_TOKEN` secret).

Maintainers: follow the checklist in
[`src/Koshi.Mcp/README.md`](src/Koshi.Mcp/README.md#releasing) and the launch-day
sequence in [`_local/marketing/release-day-checklist.md`](_local/marketing/release-day-checklist.md).

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](./LICENSE).
