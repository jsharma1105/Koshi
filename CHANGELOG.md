# Changelog

All notable changes to the Koshi MCP Server are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
