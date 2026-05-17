# Changelog

All notable changes to the Koshi MCP Server are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - Unreleased

### Added
- **Python client (`pip install koshi`)** — new official Python package on PyPI.
  Zero runtime dependencies (stdlib only), wraps all 20 MCP tools as Pythonic
  methods, auto-downloads the matching native AOT binary on first use, and
  verifies it against SHA-256 hashes baked into the wheel at release time. No
  .NET install required for Python users.
- **Native AOT release binaries** — single-file, self-contained `koshi-mcp`
  executables for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`,
  `win-x64`, and `win-arm64`, attached to every GitHub release with
  `manifest.json` and `.sha256` sidecars. Run on a clean machine without any
  .NET runtime installed.
- **AOT-clean engine** — `src/Koshi.Mcp` is now flagged `IsAotCompatible=true`
  with `InvariantGlobalization`, an exhaustive `WarningsAsErrors` list for
  every trim/AOT analyzer code (IL2026–IL3056), a source-generated JSON
  context (`Koshi.Mcp.Internal.KoshiJsonContext`), and explicit
  `WithTools<T>()` registration in place of the reflection-based
  `WithToolsFromAssembly()`. `dotnet publish -p:PublishAot=true` succeeds with
  zero warnings.
- **`aot-smoke (linux-x64)` PR gate** — `build.yml` now AOT-publishes
  `koshi-mcp`, runs the expanded smoke harness against the native binary, and
  reports the binary size to the workflow summary. Required check in branch
  protection.
- **Expanded smoke harness** — `Koshi.Mcp.SmokeTest` gained an `--exe` mode for
  AOT binaries, all 20 tools are now exercised on every run, 3 bad-argument
  paths verify error responses, and the `koshi_version` output is asserted to
  match the expected version stamp.
- **4-stage release pipeline** — `release.yml` is now a DAG of `nuget` →
  `aot` (matrix of 6 RIDs, fail-fast disabled) → `release` (single writer,
  hard-gated on all 6 RIDs present) → `pypi` (gated on the
  `PUBLISH_PYPI` repo variable and the `pypi-release` environment).
- **`scripts/inject-manifest.py`** — release-time helper that injects the
  binary SHA-256 hashes into the Python wheel's `_manifest.py` before the
  wheel is built, binding wheel and binaries by content.

### Changed
- **`Koshi.Mcp.Internal.PersistenceEnvelope`** and
  **`Koshi.Mcp.Internal.DocInput`** are now top-level internal types (were
  private nested) so the source-generated JSON context can reference them.
  No public API change.
- **Versions** — `Koshi.Mcp` and `Koshi.Agents` both bumped to `0.4.0` in
  lockstep. Same engine, same MCP tool surface — only the distribution
  channels are new.

### No behavior changes
- All 20 MCP tools accept the same arguments and return the same shapes as
  v0.3.0. The 89 unit tests pass unchanged. v0.3.0 persisted memory files
  load cleanly under v0.4.0.

## [0.3.0] - 2026-05-15

### Added
- **MCP smoke test** (`tests/Koshi.Mcp.SmokeTest`) is now part of `Koshi.slnx`
  and runs as a dedicated `smoke (MCP protocol)` job in CI on every PR, and
  also runs as a release gate before any NuGet publish — catches stdio /
  JSON-RPC regressions that unit tests don't.

### Changed
- **Dependency upgrade:** `ModelContextProtocol` 1.0.0 → 1.3.0.
- **Dependency upgrade:** `Microsoft.Extensions.AI.Abstractions` → 10.6.0.
- **Dependency upgrade:** `Microsoft.SourceLink.GitHub` → 10.0.300.
- **Dependency upgrade:** `GitHubActionsTestLogger` → 3.0.4.
- **Workflow refresh:** `actions/checkout@v6`, `actions/setup-dotnet@v5`,
  `github/codeql-action@v4`, `actions/upload-artifact@v7`.
- **README refresh:** new hero section explaining the four pillars and a
  60-second install path; repository-layout section updated to match the
  current `Core / Mcp / Agents` shape (the previous list of demo projects
  was removed in earlier cleanup but the README hadn't caught up).
- **`CONTRIBUTING.md` / `src/Koshi.Mcp/README.md`:** project-layout sections
  no longer reference deleted demo / CLI / eval / tuner directories. The
  Mcp README's "Releasing" section now documents the tag-triggered release
  workflow instead of manual `dotnet nuget push`.
- **CI hardening:** `actions/upload-artifact` steps marked
  `continue-on-error: true` so transient artifact-service outages (HTML
  responses instead of JSON when the backend is degraded) no longer fail
  builds whose tests passed.

### Fixed
- **`Koshi.Core/Context/CachePrefixOptimizer.EstimateSavings`** — possible
  integer overflow when `queriesPerDay × prefixTokens` exceeded `int.MaxValue`
  before the cast to `decimal` (e.g. 1M queries × 10K tokens = 10^10).
  First operand is now widened to `decimal` before the multiply.
- **`Koshi.Agents`** — `Path.Combine` → `Path.Join` everywhere; `Path.Join`
  concatenates with separators and never silently drops earlier arguments
  if a later one looks rooted.
- **Dead-code / warning cleanups across `Koshi.Core` and `Koshi.Mcp`** —
  unused `deficitRoles`, `trackerEntries`, `budget` locals removed;
  redundant `(float)` casts removed; redundant `if`/`else` collapsed to
  a ternary in `TeamTools`; `foreach`-immediately-mapped loops rewritten
  with `.Select(...)` in `UninstallCommand` and `DoctorCommand` per CodeQL
  `cs/linq/missed-select`; implicit-filter `foreach`es in
  `SafeFileEnumerator` rewritten with `.Where(...)` per `cs/linq/missed-where`.

### Security
- **Transitive pin:** `Microsoft.Bcl.Memory` held at 10.0.8, above the
  vulnerable 9.0.4 (GHSA-73j8-2gch-69rq). Not exploitable in Koshi's
  default configuration; pinned out of an abundance of caution.

### Removed
- `Koshi.Cli`, `Koshi.Eval`, `Koshi.Tuner`, and the per-pillar demo
  projects (`Koshi.Retrieval.Demo`, `Koshi.Memory.Demo`,
  `Koshi.Context.Demo`, `Koshi.Harness.Demo`, `Koshi.Team.Demo`) were
  deleted in earlier cleanup — this release ensures every doc reflects
  that.

## [0.2.0] - 2026-05-14

### Added
- `koshi_index_directory` tool: recursively index supported text files from disk
  with safe enumeration (skips hidden dirs, secrets, binaries, large files, symlinks).
- `koshi_clear_index` tool: reset the indexed corpus without restarting the server.
- `koshi_clear_memories` tool: reset all stored memories (requires `confirm=true`).
- `koshi_version` and `koshi_health` diagnostic tools.
- Optional JSON file persistence for memories via the `KOSHI_MEMORY_FILE` env var
  (atomic writes, schema versioning).
- Auto-index from `KOSHI_INDEX_PATH` env var on first `koshi_search` call.
- Default exclude lists for secrets (`.env*`, `*.pem`, `*.key`, `secrets.*`),
  build output (`bin`, `obj`, `node_modules`, `dist`, `target`, etc.), and hidden dirs.
- Symbol package (`.snupkg`) and SourceLink integration for debugging.
- Full NuGet package metadata: license, repository URL, tags, README, release notes.

### Changed
- Pinned `ModelContextProtocol` to stable `1.0.0` (was floating `0.*`).
- Pinned `Microsoft.Extensions.Hosting` to `10.0.8` (was floating `10.*`).
- `Program.cs` clears default logging providers so stdout is reserved exclusively
  for MCP JSON-RPC traffic; status messages suppressed.
- Server version now read from assembly metadata instead of hardcoded.
- `koshi_search`: `topK` clamped to 1–50; empty queries rejected.
- `koshi_recall`: `topK` clamped to 1–25; recall scoring weights now sum to 1.0.
- TokenCounter promoted to a singleton (`Lazy<TokenCounter>`) — no more per-call init.
- `koshi_search` no longer auto-indexes the process working directory by default;
  it only auto-indexes when `KOSHI_INDEX_PATH` is set (security/privacy).
- `koshi_index_directory` now requires an explicit path or `KOSHI_INDEX_PATH`.

### Fixed
- Race condition in `koshi_search`: keyword retriever reference is now captured
  inside the lock before use.
- Memory recall scoring weights now sum to exactly 1.0 (previously summed to 1.1).

### Security
- Auto-indexing of arbitrary process CWD removed by default.
- Sensitive file types (`.pem`, `.key`, `.pfx`, etc.) and patterns (`.env*`,
  `secrets.*`, `id_rsa`) are excluded from disk indexing.
- Hidden directories are skipped except for an allow-list (`.github`,
  `.vscode-test`).

## [0.1.0] - 2026-05-13

### Added
- Initial MCP server release with 14 tools across four domains:
  - **Retrieval:** `koshi_index`, `koshi_search`, `koshi_list_indexed`
  - **Context:** `koshi_compile_context`, `koshi_token_count`, `koshi_budget_plan`
  - **Memory:** `koshi_remember`, `koshi_recall`, `koshi_memory_stats`, `koshi_forget`
  - **Team / Quality:** `koshi_register_team`, `koshi_score_turn`,
    `koshi_team_dashboard`, `koshi_analyze_feedback`, `koshi_list_teams`
- Stdio transport.
- BM25 keyword retrieval, GPT-4 cl100k tokenization, in-memory state.
