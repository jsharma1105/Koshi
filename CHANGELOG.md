# Changelog

All notable changes to the Koshi MCP Server are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-05-22

### Added
- **Vault-mode memory backend (`KOSHI_MEMORY_VAULT`) — share memories across teams via Git.**
  Setting this env var to a directory makes Koshi store every memory as a
  human-readable `.md` file with YAML frontmatter under
  `<vault>/koshi/{facts,decisions,patterns,preferences}/`, instead of the
  single opaque JSON envelope used by `KOSHI_MEMORY_FILE`. Memories are
  Git-friendly (one file per memory → clean diffs, no merge storms),
  Obsidian/Foam/Logseq-compatible, and editable in any text editor —
  external edits, deletes, and Git pulls are picked up on the next tool
  call without a server restart. See [`docs/vault-mode.md`](docs/vault-mode.md)
  for the format spec and migration guide.
- **Three new MCP tools for vault interop:**
  - `koshi_memory_export_to_vault(vaultPath, overwrite)` — bulk-export the
    current memory store to a vault directory.
  - `koshi_memory_import_from_vault(vaultPath, mode)` — import memories
    from a vault into the current backend. Modes: `merge` (keep current on
    id collision), `overlay` (vault wins on id collision), `replace`
    (destructive, full swap).
  - `koshi_memory_sync_vault()` — force a fresh re-scan of the vault and
    refresh the in-memory cache (no-op for the JSON backend).
- **Unmanaged-note reporting.** Any `.md` file under `<vault>/koshi/` that
  lacks a `koshi.id` in its frontmatter is reported in `koshi_memory_stats`
  as an "unmanaged note" — surfaced but never auto-promoted into the memory
  store and never overwritten.
- **`koshi_memory_stats` now reports the active backend kind, location,
  unmanaged-note count, and duplicate-id warning count** so users can see
  at a glance whether the JSON or vault backend is active.
- **`KOSHI_MEMORY_VAULT` added to `koshi_diagnostics` output** alongside the
  other env vars.

### Format (vault mode)
- One `.md` file per memory at
  `<vault>/koshi/<type>/<subject-slug>--<id>.md` (e.g.
  `koshi/decisions/we-chose-dapper-over-ef--mem-000123.md`).
- File identity is `koshi.id` from frontmatter — **filename is cosmetic**.
  Subject rename → atomic move to new path + delete of old path; the id
  follows the file.
- Frontmatter stores: `id, type, scope.{user,workspace,thread}, source,
  confidence, created-at, updated-at, last-accessed-at, access-count, tier`
  (plus `superseded-by` / `contradiction-note` only when non-null).
- **Derived fields (embeddings, compressed-content) are intentionally
  omitted** from disk to avoid bloating Git diffs — they regenerate on
  demand.
- **Unknown top-level frontmatter keys** (your own `aliases:`, `cssclass:`,
  `publish:`, etc.) are preserved verbatim on rewrite — Koshi only edits
  the `koshi:` block.

### Behaviour
- When both `KOSHI_MEMORY_VAULT` and `KOSHI_MEMORY_FILE` are set, the vault
  wins and a one-line stderr warning is emitted; `KOSHI_MEMORY_FILE` is
  ignored.
- Vault mode reloads from disk on **every tool call** — external deletes,
  edits, and Git pulls take effect immediately without restart.
- ID allocation re-scans the vault before allocating, so externally-added
  memories can't collide with `mem-NNNNNN` ids generated in-process.
- Duplicate `koshi.id` across two files (a possible Git-merge artifact) —
  newer file mtime wins and a stderr warning is emitted; both files are
  never loaded.
- Atomic writes (temp file + `File.Move(overwrite:true)`) on every mutation;
  half-written `.tmp` files left by a crash are ignored by subsequent loads.

### Compatibility
- **`KOSHI_MEMORY_FILE` behaviour is byte-identical to v0.5.1** when
  `KOSHI_MEMORY_VAULT` is not set. Existing users see zero change.
- The internal `MemoryPersistence` type was renamed to `JsonFileBackend` and
  now implements a new internal `IMemoryBackend` interface (vault/json
  swap-in). Public API surface is unchanged.

### Tests
- 65 new unit tests across `SlugTests`, `VaultDocumentTests`,
  `VaultBackendTests`, `JsonFileBackendTests`, `MemoryStoreTests` —
  including a cache-divergence regression guard that catches the bug where
  an external file delete is masked by a stale in-process cache.
- The smoke test gained a dedicated **vault-mode phase**: spawns a second
  server process with `KOSHI_MEMORY_VAULT` set, exercises remember/recall/
  external-edit/external-delete/unmanaged-note paths against the live MCP
  protocol.

### Not in scope (deferred to 0.6.1 / 0.7.0)
- `FileSystemWatcher`-driven cache invalidation (deferred to 0.6.1) — vault
  mode currently reloads on every tool call, which is correct but does a
  directory scan per call. The watcher will let us skip the scan when the
  cache is known fresh.
- Logseq/Dendron file-layout adapters (deferred to 0.7.0). The current
  layout is Obsidian-flavoured Markdown, which also works in Foam and
  Logseq's "Markdown mode".

### Added — project-root path defaults (2026-05-22 amendment)
- **`KOSHI_PROJECT_ROOT` env var.** All Koshi path env vars now derive
  sensible defaults from the project root. If you launch `koshi-mcp` from
  `C:\OPP`, your memory and index land at `C:\OPP\.koshi\memory.json` and
  `C:\OPP\.koshi\index.json` automatically — no configuration needed.
- **Defaults table** (each env var still wins when set):

  | Env var              | Unset default                       |
  |----------------------|-------------------------------------|
  | `KOSHI_PROJECT_ROOT` | `Environment.CurrentDirectory`      |
  | `KOSHI_INDEX_FILE`   | `<root>/.koshi/index.json`          |
  | `KOSHI_MEMORY_FILE`  | `<root>/.koshi/memory.json`         |
  | `KOSHI_MEMORY_VAULT` | (null — vault stays opt-in)         |
  | `KOSHI_INDEX_PATH`   | `<root>` (for `koshi_index_directory`<br/>and `koshi_diagnostics` display) |

- **Relative paths in env values now resolve against `KOSHI_PROJECT_ROOT`.**
  `KOSHI_MEMORY_VAULT=team-vault` resolves to `<root>/team-vault`. Absolute
  paths are still used as-is. Whitespace-only values are treated as unset.
- **Auto-index remains opt-in.** Setting `KOSHI_INDEX_PATH` is still the
  signal that says "auto-index this directory on first search." We do *not*
  auto-scan the project root by default — that would risk a slow first
  search in large mono-repos. Users explicitly call `koshi_index_directory()`
  (which now also defaults to the project root) or set `KOSHI_INDEX_PATH`.
- **`koshi_health` (diagnostics) now shows the resolved value and source**
  (`[env]` vs `[default]`) for every path. Easier to see exactly what's in
  effect.
- **Caveat for Claude Desktop users:** Claude Desktop typically launches
  MCP servers with cwd=`%USERPROFILE%`, not your project. Set
  `KOSHI_PROJECT_ROOT` explicitly in your `claude_desktop_config.json`:
  ```json
  "koshi": {
    "command": "koshi-mcp",
    "env": { "KOSHI_PROJECT_ROOT": "C:/your/project" }
  }
  ```
  Copilot CLI and Cline launch servers with cwd=your project, so the
  defaults Just Work there.
- **Backwards-compatible.** Existing `v0.5.x` setups that set
  `KOSHI_MEMORY_FILE`, `KOSHI_INDEX_FILE`, and/or `KOSHI_INDEX_PATH`
  behave byte-identically. The defaults only kick in for paths you
  *didn't* configure.

### Changed
- **`koshi_index_directory()` with no `path` argument now defaults to
  the project root** instead of returning `"path is required"`. In
  v0.5.x callers had to pass an explicit path (or set `KOSHI_INDEX_PATH`).
  v0.6.0 indexes `<root>` when called with no args. This is an observable
  behavior change but a strict improvement in usability — and you can
  still pass any explicit `path` to scope the index narrower.

### Safety
- **`<root>/.koshi/.gitignore` is auto-seeded** when Koshi first uses
  the default state directory. Contents: `*` plus `!.gitignore`, so
  memory and index files don't get accidentally committed. We never
  overwrite an existing `.gitignore` — power users who *want* to track
  Koshi state in Git can delete or edit the file freely.
- **Index snapshots are containment-checked.** With the default
  `<root>/.koshi/index.json`, if a snapshot's source path is *outside*
  `KOSHI_PROJECT_ROOT`, it is discarded with a stderr diagnostic
  instead of silently serving foreign results. (Setting
  `KOSHI_INDEX_PATH` explicitly skips this check — explicit user intent
  wins.)
- **Defensive Windows path handling.** Rooted-but-not-fully-qualified
  paths like `\foo` (root-relative) and `C:foo` (drive-relative) used
  to escape `KOSHI_PROJECT_ROOT` via `Path.IsPathRooted` + `Path.Combine`.
  v0.6.0 switches to `IsPathFullyQualified` + `Path.Join`, which keeps
  these contained under the project root.
- **PathConfig static-init is fault-tolerant.** If env vars hold values
  that `Path.GetFullPath` rejects (invalid characters, etc.), the
  server logs a one-line warning and falls back to cwd-only defaults
  instead of failing to start.

### Quality (post-vault follow-ups, 2026-05-22)
- **Shared `TokenCounters.Shared` accessor (#29).** Consolidates the two
  duplicate `Lazy<TokenCounter>` fields that previously lived in
  `RetrievalTools` and `ContextTools` into a single process-wide instance
  under `Koshi.Core.Tokenization`. The new `KOSHI_TOKENIZER_MODEL` env
  var selects the encoding for both call sites (default `gpt-4` →
  cl100k_base; set to `gpt-4o` or `gpt-4o-mini` for o200k_base). The
  active model is reported by `koshi_diagnostics`.
- **Configurable chunker token sizes (#26).** `koshi_index` and
  `koshi_index_directory` now accept optional `maxTokens` (64-2048,
  default 512) and `overlapTokens` (0-256 and `< maxTokens/2`, default
  50) parameters. Defaults can also be set globally via
  `KOSHI_CHUNK_MAX_TOKENS` / `KOSHI_CHUNK_OVERLAP_TOKENS`. Out-of-range
  values are clamped with a warning rather than rejected, and the
  effective config is surfaced in the indexing response.
- **Stale CodeQL alerts cleared.** The 20 `useless-cast-to-self` alerts
  in generated `System.Text.Json.SourceGeneration` files were filed
  before `.github/codeql/codeql-config.yml` added `paths-ignore` for
  `**/obj/**` + `**/*.g.cs`; they have been dismissed as "won't fix"
  (analysis-target only).

## [0.5.1] - 2026-05-19

### Fixed
- **`koshi_recall` now honours `MemoryScope` and uses BM25 ranking (#24).**
  Pre-v0.5.1 recall ignored the per-memory `Scope` (UserId/WorkspaceId/ThreadId)
  entirely and ranked candidates by case-insensitive substring matching on
  content, which meant memories tagged for one workspace could leak into
  another's recall and stem-different queries (`"authenticate"` vs.
  `"authentication"`) wouldn't match. The tool now accepts optional
  `userId` / `workspaceId` / `threadId` parameters: workspace and thread
  filters are exact-match, the user filter additionally always lets
  globally-scoped memories (`UserId="*"`) through. Ranking is
  `0.6·normalized_BM25 + 0.3·recency + 0.1·confidence`, with a `BM25 > 0`
  gate so pure recency/confidence hits don't surface unrelated memories.
  `koshi_remember` accepts the same three optional scope parameters and
  defaults to `UserId="*", WorkspaceId="default"` for byte-identical
  behaviour when callers omit them. Each recalled result now shows its
  scope so the user can see WHY a memory matched.
- **Auto-index from `KOSHI_INDEX_PATH` retries after a 30 s throttle
  instead of giving up forever after the first failure (#31).** Previously
  a single transient failure (e.g. directory not yet mounted, permission
  issue) flipped a one-shot `_autoIndexAttempted` flag that blocked all
  subsequent auto-indexing for the lifetime of the process — every
  later `koshi_search` would silently fall through to a generic
  "No documents indexed" message. The throttle now caches the failure
  text and replays it (with a countdown to the next retry) for 30 s,
  then re-attempts on the next search. Successful indexing clears the
  cache. The error message also tells the user how to retry immediately
  (`koshi_index_directory(path)`) without waiting for the throttle.

## [0.5.0] - 2026-05-18

### Added
- **Persistent BM25 retrieval index via `KOSHI_INDEX_FILE`.** Setting this env
  var to an absolute path makes Koshi save the indexed corpus on every
  `koshi_index_directory` / `koshi_index` and auto-load it on first
  `koshi_search` / `koshi_list_indexed`. Re-running against the same source
  directory is a millisecond-level no-op instead of a 30 s re-chunk. Writes
  are atomic (`temp → rename`), schema-versioned (`SchemaVersion: 1`), and
  go through the AOT-safe source-generated `KoshiJsonContext`. When
  unset, behaviour is byte-identical to v0.4.x — the index lives only for
  the process lifetime.
- **SHA-256 fingerprint invalidation.** Each on-disk snapshot records the
  set of `(relpath, size, mtimeUtc)` tuples it was built from. On startup,
  Koshi recomputes the fingerprint from the current filesystem; mismatches
  (files added/removed/edited, or `KOSHI_INDEX_PATH` pointed at a different
  directory) cause the stale snapshot to be discarded silently and a
  re-index to run instead of serving stale results.
- **`koshi_health` now reports retrieval persistence.** New "Index
  persistence" block surfaces whether `KOSHI_INDEX_FILE` is wired up, the
  resolved path, and whether the current corpus was loaded from a snapshot
  vs freshly indexed. `KOSHI_INDEX_FILE` is also dumped in the env-var
  section so misconfigurations are visible.
- **Smoke-test coverage for the persistence round-trip.** `Koshi.Mcp.SmokeTest`
  now pre-seeds an `IndexEnvelope` JSON fixture, sets `KOSHI_INDEX_FILE`,
  and asserts (a) the snapshot auto-loads on first `koshi_search`,
  (b) `koshi_index_directory` overwrites the file with a fresh snapshot,
  and (c) `koshi_clear_index` deletes it. Runs on every AOT RID in CI.

### Fixed
- **`koshi-mcp --version` and `koshi-mcp --help` no longer hang.** In v0.4.0
  any command-line argument was silently ignored and the server went straight
  into reading JSON-RPC off stdin — so `koshi-mcp --version` would sit
  forever printing only the `StdioServerTransport reading messages` log line.
  The binary now recognises `--version`/`-v` (prints `koshi-mcp X.Y.Z+<sha>`
  and exits 0) and `--help`/`-h`/`-?` (prints short usage and exits 0). The
  parser is hand-written, AOT-safe, and uses no third-party CLI library.
  `Koshi.Mcp.SmokeTest` now spawns the binary with `--version` before the
  JSON-RPC handshake and asserts it exits within 5 seconds with the
  expected banner, so every AOT RID in the release matrix catches any
  regression of this kind.

### Docs
- README now links both NuGet packages (`Koshi.Mcp`, `Koshi.Agents`) with
  version + download badges.
- `KOSHI_INDEX_FILE` documented alongside `KOSHI_MEMORY_FILE` in the MCP
  server README, the Python quickstart, and the PyPI README.

### No behavior changes when `KOSHI_INDEX_FILE` is unset
- All 20 MCP tools accept the same arguments and return the same shapes as
  v0.4.x. The MCP server's default mode (no env vars, no args) is
  byte-identical for retrieval-tool outputs aside from the `koshi_health`
  text expansion.

## [0.4.0] - 2026-05-17

### Added
- **Python client (`pip install koshi`)** — new official Python package on PyPI.
  Zero runtime dependencies (stdlib only), wraps all 20 MCP tools as Pythonic
  methods, auto-downloads the matching native AOT binary on first use, and
  verifies it against SHA-256 hashes baked into the wheel at release time. No
  .NET install required for Python users.
- **Native AOT release binaries** — single-file, self-contained `koshi-mcp`
  executables for `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`, and
  `win-arm64`, attached to every GitHub release with
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
  `aot` (matrix of 5 RIDs, fail-fast disabled) → `release` (single writer,
  hard-gated on all 5 RIDs present) → `pypi` (gated on the
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
