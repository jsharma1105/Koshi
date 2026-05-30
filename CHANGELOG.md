# Changelog

All notable changes to the Koshi MCP Server are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.0] - 2026-05-30

This is a substantial minor release covering 33 commits across 27 PRs since
v0.8.1. Headline themes: **one-command install** for every supported MCP
client, an **interactive `koshi-mcp init` wizard**, **per-ecosystem steering
templates** for .NET / Python / TypeScript / Java / Go / Rust, **structured
JSON output** across all 24 tools, and an **`IndexWatcher`** that keeps the
index fresh in steady state. Quality-side: a four-model deep code review
shipped 48 fixes (#110), every production CodeQL alert was resolved (#109),
and the README split into a 194-line landing page + 7 focused guides under
`docs/` (#111). 868/868 tests green.

### Added

- **One-command shell installers (#74 Option A, PR #107)** — `curl -fsSL
  https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.sh | sh`
  (Linux/macOS) or `irm .../install.ps1 | iex` (Windows PowerShell)
  downloads a SHA-256-verified native AOT `koshi-mcp` binary for the
  current platform, registers Koshi with every detected MCP client
  (Claude Code, Copilot CLI, Cursor, Windsurf, Microsoft Agency),
  installs the 5 sub-agent personas, and optionally indexes the current
  directory. No `.NET` install required.
- **`koshi-mcp init` interactive wizard (#68, #78 Gap A, PR #101)** —
  detects installed MCP clients → checkbox list, merges
  `mcpServers.koshi` into each selected client's config (with the
  correct per-client filename), installs personas, offers to index
  `$PWD` with live streaming progress, registers a team via the new
  `.koshi-team.yml` convention, and runs `koshi-mcp doctor`. Flags:
  `--non-interactive`, `--client <name>`, `--skip-index`,
  `--skip-personas`, `--skip-team`.
- **`koshi-mcp init --register-git-template` (#78 Gap C, PR #105)** —
  writes a `.koshi/team-template.yml` so teammates pulling the repo
  inherit the team config when they run `koshi-mcp init`.
- **Per-ecosystem steering templates (#77 Layer 3, PR #103 + Layer 4,
  PR #104)** — Koshi auto-installs Claude/Copilot steering snippets
  tailored for .NET, Python, TypeScript, Java, Go, Rust, plus a generic
  fallback. Each template teaches the agent how to use Koshi for that
  stack (e.g. `.NET` projects: `koshi_search` your NuGet sources before
  proposing new dependencies). The `koshi-orchestrator` persona is
  expanded to all 24 tools.
- **`koshi-mcp` MCP prompts for cross-client steering (#77 Layer 2,
  PR #95)** — registers `koshi/capture-turn-guide`,
  `koshi/recall-before-answer`, `koshi/context-pack-discipline`, and
  `koshi/score-every-turn` via `[McpServerPrompt(...)]`. Surfaces as
  slash menu items in Claude Desktop, command-palette entries in
  Cursor and Windsurf, and `@-mentions` in Copilot CLI.
- **`IndexWatcher` for steady-state index freshness (#78 Gap D,
  PR #106)** — files modified after `koshi_index_directory` are
  re-tokenized incrementally (no full re-index) via a debounced
  `FileSystemWatcher`. Honors the same skip rules (hidden dirs, build
  output, secret patterns, sensitive extensions, oversize files,
  symlinks) as the initial index call.
- **`koshi_index_directory` streams per-file progress (#69, PR #97)** —
  emits progress on `stderr` *and* via MCP `notifications/progress`
  with `total`, `progressToken`, and the current file path, so the
  wizard's "indexing your project…" prompt is never a black box.
- **Structured JSON output for every tool (#66 Phases 1/2a/2b,
  PRs #98 / #99 / #100)** — set `KOSHI_OUTPUT_FORMAT=json` (env) or
  pass `format=json` per-call. Tools return a typed envelope
  `{tool, success, result, metadata}` instead of formatted prose;
  enables Python and .NET clients to consume Koshi without regex-
  parsing strings. Default is still `text` for backwards compatibility.
- **Offline tool introspection (#67, PR #94)** — `koshi-mcp --list-tools`
  and `koshi-mcp --describe <tool>` print the full tool surface
  without an MCP handshake. The init wizard uses this to show users
  "you got these 24 tools" before any client restart.
- **`koshi-mcp doctor` live-pings each client (#71, PR #96)** —
  instead of just verifying that the config file mentions Koshi, it
  now spawns each detected client's `koshi` registration, runs the
  MCP `initialize` handshake, and reports actual reachability.
- **`koshi-mcp config` subcommand (#78 Gap B, PR #89)** — list /
  set / unset / get env vars without manually editing client config
  files. Knows about all 11 `KOSHI_*` env vars.
- **`koshi-agents install --client X`** prints a Next Steps banner
  (#72, PR #93) telling the user what to restart and what to try
  first. Supports `--quiet` to silence and `--show-next-steps` to
  print the banner without re-running install.
- **`koshi_health` reports snapshot-loaded status (#70, PR #91)** —
  returning users can confirm the BM25 index loaded from
  `.koshi/index.json` instead of starting empty.
- **Configurable history reserve in `koshi_budget_plan` (#73,
  PR #92)** — `KOSHI_BUDGET_HISTORY_RESERVE_PCT` env (or per-call
  override) replaces the hard-coded 25% reserve, with auto-detection
  when both `history_token_count` and `total_budget` are known.

### Fixed

- **Multi-model deep code review (PR #110)** — Claude Haiku (Core),
  Sonnet (Tools), Opus (Cli + Internal), and GPT-Codex (Agents + tests)
  reviewed every C# file in parallel. 54 findings total; 6 false-
  positives dismissed; **48 actionable fixes shipped** across 5 phases:
  HIGH-severity security (path traversal hardening, narrowed catches),
  contract drift (DTO ↔ JSON envelope), new `AtomicFileWriter` helper
  (atomic-rename on Windows + POSIX), concurrency hardening
  (`SemaphoreSlim` ownership audit), CLI polish (exit codes,
  `--help` text, stderr formatting), and 10 new tests. Three follow-up
  CodeQL alerts in the new code were fixed in the same PR.
- **22 production CodeQL alerts (PR #109)** — replaced ad-hoc
  `Path.Combine` with `Path.Join` everywhere user-controlled input
  could flow into a path; narrowed `catch (Exception)` blocks to
  specific types (`IOException`, `UnauthorizedAccessException`,
  `JsonException`). Zero open CodeQL alerts on `main`.
- **`koshi_compile_context` parses `koshi_recall` output as one
  section per entry (#60, PR #87)** — previously the parser treated
  the entire recall output as a single section, breaking section
  boundaries and skewing token counts.
- **`koshi-agents install --client X` now registers the koshi MCP
  server (#64, PR #82)** — the subcommand previously installed
  personas without writing the server registration, leaving Cursor/
  Windsurf users with personas that pointed at nothing.
- **Team registry + scores persist across restarts (#59, PR #84)** —
  `TeamRegistry` now flushes to `.koshi/teams.json` on every mutation
  and reloads on startup.
- **`koshi_team_dashboard` populates all four metrics (#61, PR #86)** —
  Tokens Used, Avg Latency, Cache Hit Rate, and Budget Utilization
  previously showed `-` for every team because the score-to-metric
  reducer was never wired up.
- **`koshi-mcp` auto-loads `.koshi/index.json` snapshot on startup
  (#62, PR #85)** — eliminates the "first search is always slow"
  problem; existing snapshots are detected and mapped into memory
  before the MCP handshake completes.
- **Python wrapper exposes 4 previously-missing MCP tools (#76,
  PR #83)** — `koshi_capture_turn`, `koshi_memory_export_to_vault`,
  `koshi_memory_import_from_vault`, `koshi_memory_sync_vault` are
  now first-class `Client` methods. Also adds `Client.call_tool(name,
  **kwargs)` as an escape hatch for any tool the wrapper doesn't
  surface explicitly.

### Changed

- **README readability audit complete (#75 Waves 1 / 2 / 3,
  PRs #90 / #108 / #111)** — root README rebuilt to ≤120 lines
  (outcomes-only), MCP README split from 643 → 194 lines plus 7
  focused guides under `docs/` (`client-setup`, `configuration`,
  `tools`, `walkthroughs`, `troubleshooting`, `development`,
  `comparison`). All four READMEs (root, MCP, Agents, Python) gained
  `Next steps` and `Known issues` sections. NuGet- and PyPI-packed
  READMEs use absolute GitHub URLs so cross-doc links survive on
  registry pages.
- **All 24 tool descriptions rewritten to lead with WHEN+WHAT
  (#77 Layer 1, PR #88)** — every `[McpServerTool]` description now
  starts with the trigger ("When the user asks…") and the action
  ("…use this tool to…"), making cross-client tool-discovery
  consistent regardless of which client's heuristic picks the tool.

### Dependencies

- Bumped `Microsoft.NET.Test.Sdk` from 18.5.1 to 18.6.0 (PR #102).

### Stats

- **33 commits, 27 PRs** since v0.8.1.
- **868 / 868 tests green** across `dotnet test` on
  `ubuntu-latest`, `macos-latest`, `windows-latest`.
- **0 open CodeQL alerts** on production `src/` code.

## [0.8.1] - 2026-05-26

### Fixed (Koshi.Agents personas — closes the Copilot CLI tool-search-gating bug)

- **Copilot CLI sub-agent personas now load Koshi MCP tools at startup
  instead of lazily.** All five `.github/copilot/agents/*.agent.md`
  personas previously declared `tools: [search, read, agent]`, which
  per the [GitHub custom-agents reference](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
  enables *only* those built-in aliases and silently excludes every
  unlisted MCP tool. The result: invoking `koshi-librarian`,
  `koshi-memory-keeper`, `koshi-context-packer`, `koshi-quality-coach`,
  or `koshi-orchestrator` from Copilot CLI started the sub-agent with
  **zero Koshi tools in its default toolset**, forcing each one to
  rediscover them via tool-search before any work could happen. The
  Claude Code personas (`.claude/agents/*.md`) were unaffected — they
  already used explicit `mcp__koshi__koshi_*` allow-lists. Fix:
  rewrite every `tools` list to spell out each Koshi tool using the
  documented `koshi/<tool>` namespacing, in parity with the Claude
  twins; drop the built-in aliases entirely so personas operate
  strictly through Koshi MCP tools (preserving pillar isolation).
- **Memory pillar parity with README/AGENTS.md.** The
  `koshi-memory-keeper` persona previously allowed only 5 memory tools
  + health, missing the four v0.8.0 additions (`koshi_capture_turn`,
  `koshi_memory_export_to_vault`, `koshi_memory_import_from_vault`,
  `koshi_memory_sync_vault`). Allow-lists in both the Claude and
  Copilot variants now include all nine memory tools. The
  `koshi-orchestrator` persona is expanded from 20 to **all 24**
  Koshi tools; its "Memory (5 tools)" summary section is updated to
  "(9 tools)" and lists each tool by name.
- **Persona body text refreshed** where counts and "Tools you own"
  tables had drifted from the actual MCP surface.

### Release scope

- `Koshi.Agents` 0.8.1 carries the persona fix (all five `.agent.md`
  files + two `.md` files are embedded as `<EmbeddedResource>` in the
  Agents package, so existing global installs do not auto-update
  without a release).
- `Koshi.Mcp` 0.8.1 is bit-for-bit functionally identical to 0.8.0 —
  no server-side source changes — but the version is bumped in sync
  with `Koshi.Agents` to keep the unified release tagging convention
  the project follows.
- Native AOT binaries (`koshi-mcp-<rid>`) and the Python `koshi` wheel
  are republished at 0.8.1 so the manifest hashes and binary version
  assertions in the smoke tests line up with the tag.

## [0.8.0] - 2026-05-23

### Added
- **`koshi_capture_turn` MCP tool — turn-end auto-capture for decisions.**
  The agent passes a 1-3 paragraph summary of the turn (plus optional
  `linked_pr` / `linked_commits` for provenance); the server runs a
  lightweight pattern-based extractor (no LLM dep, AOT-friendly,
  deterministic) and persists each decision-shape sentence as a
  `Decision` memory under the active backend. Closes the auto-capture
  gap that prior phases (supersession, lint, dedupe) need records to
  operate on.
- Decision extractor patterns (`Koshi.Core.Memory.DecisionExtractor`):
  - `Decision: <text>` explicit marker (conf 0.95)
  - `X over Y because Z` comparative with rationale (conf 0.90)
  - `we / I / the team + chose / decided / picked / went with / opted for` (conf 0.80)
  - `fixed by / resolved by / patched / worked around by` resolution markers (conf 0.75)
  - `decided to / chose to / picked to / opted to` plain decision verbs (conf 0.70)
- Questions and short fragments (<20 chars) are filtered out so chitchat
  doesn't auto-capture.
- `auto_promote=false` returns extracted candidates without persisting,
  letting the agent or user preview captures before saving.
- Subject-exact-match dedupe within scope at write time (string-match
  only; full similarity-based dedupe lands in #44).
- Provenance footer (`PR #N`, `commits <sha>...`, `captured: <ISO-8601>`)
  appended to memory body when linked metadata is provided.
- `docs/copilot-instructions-snippet.md` — recommended snippet teams
  paste into their `.github/copilot-instructions.md` so MCP-aware agents
  call the tool reliably without each repo reinventing the prompt.

### Notes
- Extraction is pattern-only by design (no LLM). False-negative rate is
  the tradeoff — the snippet teaches the agent to phrase decisions
  explicitly so the heuristics catch them. LLM-based extraction can be
  layered on later without breaking the wire contract.
- This tool is the cornerstone of the cross-developer "skip the
  regression" workflow. Pairs with the upcoming #45 (supersession) and
  #44 (similarity dedupe).

### Fixed (review-pass on PR #47)
- **Negation/hypothetical sentences no longer auto-capture.** Sentences
  like "We did NOT choose Dapper over EF Core because of cost", "If we
  had chosen X over Y", "Per the docs, you choose X over Y" previously
  matched the comparative-with-rationale pattern at 0.9 and silently
  persisted as Decision memories. They are now filtered upstream by
  `IsCandidateSentence`. (Surfaced by opus-deep-review.)
- **Bullet-list summaries now extract every decision instead of one.**
  Real-world agent summaries are usually multi-bullet markdown lists;
  the sentence splitter was treating them as one sentence and dropping
  every decision except the highest-confidence one. The splitter now
  breaks on `\n -`, `\n *`, `\n •`, `\n 1.`, `\n 1)` in addition to
  sentence terminators and blank-line gaps. (Surfaced by opus-deep-review.)
- **`Decision: <log-like-tail>` no longer captures as 0.95-confidence
  garbage.** The explicit-marker pattern now requires an English-looking
  tail (≥3 alpha characters), so log-fragment summaries like
  `Decision: 200 OK was returned ...` no longer slip through.
  (Surfaced by opus-deep-review.)
- **Subject normalization: leading bullet markers stripped, internal
  whitespace collapsed.** Two agents writing the same decision with
  different whitespace or one prefixed with `- ` now produce identical
  subject strings, so subject-exact-match dedupe actually catches
  duplicates instead of letting near-twins through. (Surfaced by
  opus-deep-review.)
- **Capture dedupe now matches the full memory scope (UserId,
  WorkspaceId, ThreadId), not just WorkspaceId.** One user's capture
  could previously suppress another user's identical-subject capture
  in the same workspace; thread-scoped captures couldn't coexist with
  workspace-scoped ones. (Surfaced by codex-cross-review.)
- **`No decision-shape sentences detected` message is no longer
  misleading when the extractor returned zero candidates.** Old text
  said "all below confidence floor 0.50" even when the count was zero.
  (Surfaced by opus-deep-review.)

### Fixed (durable-write contract — closes #48)
- **Backend writes now signal failure instead of silently swallowing
  IO errors.** Previously a disk-full / read-only-file / vault-not-
  writable condition could cause `koshi_remember`, `koshi_forget`,
  `koshi_clear_memories`, `koshi_memory_export_to_vault`,
  `koshi_memory_import_from_vault`, and `koshi_capture_turn` to return
  `✅ Remembered as mem-NNNNNN` while the durable copy never made it
  to disk — and worse, a later process restart would lose every
  in-session memory because the cache was the only copy. Now:
  - `JsonFileBackend.Save`, `VaultBackend.Upsert`, `VaultBackend.Delete`,
    and the owned-file deletes inside `VaultBackend.ReplaceAll` log to
    stderr and throw a typed `MemoryPersistenceException` carrying
    backend kind (`json` / `vault`), durable location (file path /
    vault root), and the underlying IO exception.
  - Catch filters widened to include `ArgumentException`,
    `NotSupportedException`, and `DirectoryNotFoundException` so
    path-related failures (invalid chars, reserved Windows names,
    vault dir deleted concurrently) also surface through the typed
    exception instead of leaking raw IO exceptions to MCP clients.
  - `MemoryStore.WithFreshState` and `MemoryStore.ReplaceAll` catch
    `MemoryPersistenceException`, reload the in-memory cache from
    disk so it mirrors the actual durable state (no more cache
    ahead of disk after a partial multi-step mutation), and rethrow.
    Secondary failures during reload are logged but do not mask the
    original exception.
  - Every mutation tool now returns a clear `❌ persistence failed on
    {backend} backend at '{location}': {reason}. Cache has been
    reloaded from disk to mirror the actual durable state.` message
    instead of misleading ✅. Calling `koshi_recall` immediately after
    a failed write returns the actual durable state, not an in-memory
    illusion.
  - Post-durable-write orphan cleanup (subject-rename old-file delete
    in `VaultBackend.Upsert`) is documented as intentionally outside
    the contract — leftover files are reaped by the next
    `ReplaceAll` and never cause data loss.

## [0.7.0] - 2026-05-22

### Added
- **Multi-flavor vault adapters — Obsidian, Foam, Logseq, Dendron.**
  Selectable via `KOSHI_VAULT_FLAVOR=obsidian|foam|logseq|dendron`
  (default: `obsidian`, same as v0.6.x). The wire format (YAML
  frontmatter, identity, all field semantics) is identical across
  flavors; only file naming and placement differ so Koshi-managed
  memories sit naturally alongside whatever PKM tool the team already
  uses.
- New layout adapters:
  - **Obsidian** (default) — `<vault>/koshi/{facts,decisions,patterns,preferences}/<slug>--<id>.md`. Backwards-compatible with v0.6.x vaults.
  - **Foam** — identical on-disk layout to Obsidian (Foam is built on
    Obsidian-compatible markdown). Diagnostics report `foam` so it's
    distinguishable in `koshi_health`.
  - **Logseq** — `<vault>/pages/koshi-<type>-<slug>--<id>.md` (flat).
    Lives in Logseq's idiomatic `pages/` dir; the `koshi-` prefix keeps
    Koshi files from colliding with the user's own Logseq pages.
  - **Dendron** — `<vault>/koshi.<type>.<slug>--<id>.md` at the vault
    root, matching Dendron's dot-namespaced hierarchy convention.
- `koshi_health` reports the active vault flavor (`obsidian`/`foam`/`logseq`/`dendron`).
- File-system watcher (v0.6.1) is flavor-aware: the watch scope, filter,
  and recursion mode come from the adapter, so Logseq/Dendron watchers
  don't fire on every user page edit — only on Koshi-owned files.

### Changed
- `VaultBackend` now delegates file layout to `IVaultLayoutAdapter`
  selected at construction (via env var by default). All existing
  enumeration / target-path / dir-creation code paths route through
  the adapter; the wire format is unchanged.
- `koshi_memory_export_to_vault` and `koshi_memory_import_from_vault`
  accept a new `flavor` parameter (default `obsidian`) so the target
  / source vault's layout is chosen explicitly, independent of the
  active `KOSHI_VAULT_FLAVOR` env var. Without this, a user running
  with `KOSHI_VAULT_FLAVOR=logseq` who imported a real Obsidian vault
  saw "no managed memories found" because the import scanned
  `<vault>/pages/` instead of `<vault>/koshi/<type>/`.

### Fixed (review-pass on PR #41)
- **Test-isolation regression in `VaultBackendTests` and `VaultWatcherTests`.**
  The 2-arg `VaultBackend(string, bool)` constructor (used by the
  pre-flavor test classes) used to be Obsidian-hardcoded; this PR
  rewired it to read `KOSHI_VAULT_FLAVOR`. The legacy tests hardcode
  Obsidian paths and weren't guarding the env var, so a developer
  with `KOSHI_VAULT_FLAVOR=dendron` set in their shell would see
  every test in those classes fail. Plus a parallel-test race:
  `VaultFlavorTests.ResolveFromEnv_reads_env_var` mutates the env
  var mid-test, and xUnit ran the constructors in parallel. Fixed
  by capturing/nulling/restoring the env var in both setups and
  putting all three vault-touching test classes in the same xUnit
  `[Collection("VaultEnvVar")]` so they serialize. (Surfaced by
  sonnet-review.)
- **`MemoryTools.GetStatus` was reading `_store.Backend` four times
  in the same record initializer.** A hypothetical future swap (lazy
  init, reconnect) could let `VaultWatcherStatus` and `VaultFlavor`
  observe different instances and silently disagree on whether the
  backend is a vault. Cached the property in a local. (Surfaced by
  sonnet-review.)

### Migration
- Users on v0.6.x with the default Obsidian layout: **no action needed.**
  The default flavor stays `obsidian` and the on-disk format is
  byte-identical.
- Users wanting to switch flavors: export from old vault, set
  `KOSHI_VAULT_FLAVOR` to the new flavor, point `KOSHI_MEMORY_VAULT` at
  a fresh directory, and run `koshi_memory_import_from_vault` from the
  old vault. (Cross-flavor in-place migration is not automatic — keep
  the old vault until you've verified the new one.)

### Unknown-flavor handling
- Setting `KOSHI_VAULT_FLAVOR` to an unrecognized value logs a single
  stderr warning and falls back to `obsidian`. The server never fails
  to start because of a bad flavor name.

## [0.6.1] - 2026-05-22

### Added
- **Vault file-system watcher — hot-reload without per-call directory scans.**
  When `KOSHI_MEMORY_VAULT` is set, Koshi now attaches a `FileSystemWatcher`
  to `<vault>/koshi/**/*.md` and only reloads the in-memory cache when an
  external change is observed (git pull, Obsidian edit, manual edit). The
  v0.6.0 behavior was to rescan the whole directory tree on every tool call;
  on large vaults this added ~10-50 ms of fixed latency per call. The watcher
  reduces the steady-state cost to near zero while preserving correctness.
- New env var `KOSHI_VAULT_WATCH` (`on`/`off`/`true`/`false`/`0`/`1`/etc.,
  default `on`) for disabling the watcher when running over network mounts,
  containers, or any FS that does not deliver inotify-style events
  reliably. When disabled, Koshi falls back to v0.6.0 "reload on every call"
  semantics — same correctness, slightly higher latency.
- `koshi_health` reports the watcher status (`healthy` / `disabled` /
  `unavailable`) when the active backend is a vault.

### Changed
- Internal: replaced `IMemoryBackend.RequiresReloadPerCall` (property) with
  `IMemoryBackend.ShouldReload()` (method) so backends can return `false`
  when their cache is known to be fresh. JSON backend unchanged (returns
  `false` always); vault backend uses read-and-clear semantics on a dirty
  bit set by the watcher callback.

### Fixed
- Transient `VaultBackend` instances created by `koshi_memory_export_to_vault`
  / `koshi_memory_import_from_vault` no longer attach a file-system watcher
  (they're disposed immediately after use); also explicitly disposed via
  `try/finally`.

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
  `~/myrepo`, your memory and index land at `~/myrepo/.koshi/memory.json` and
  `~/myrepo/.koshi/index.json` automatically — no configuration needed.
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
    "env": { "KOSHI_PROJECT_ROOT": "/Users/you/myrepo" }
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
- **Memory persistence is now discoverable (#30).** v0.6.0 already makes
  persistence on by default at `<root>/.koshi/memory.json` (no env var
  needed), and `koshi_memory_stats` already surfaces backend kind and
  location at the top. This follow-up updates the `koshi_remember` tool
  description to lead with the v0.6.0 default and adds a one-line stderr
  nudge on Remember when persistence is genuinely disabled.
- **Porter-style English stemmer for BM25 (#27).** `KeywordRetriever`
  now stems both indexed terms and query terms with a built-in light
  Porter-1980 implementation (`Koshi.Core.Tokenization.EnglishStemmer`).
  Queries like `authentication` now match documents that say
  `authenticate`, `authenticating`, `authenticated`, etc.
  Opt out with `KOSHI_BM25_STEMMING=off` if your corpus is heavy on
  exact-match codes / identifiers.
- **Embedding provider plumbing — Phase 1 (#28).** Adds
  `IEmbeddingProvider` interface and `EmbeddingProviderRegistry` in
  `Koshi.Core.Retrieval` so optional adapter packages
  (`Koshi.Embeddings.OpenAI`, `Koshi.Embeddings.Local`, etc.) can
  register a provider at startup without bloating the AOT binary. The
  default build ships no provider — BM25 keyword search remains the
  sole retriever. `koshi_remember` now accepts an optional
  `embedSelf: bool` parameter that populates the existing
  `MemoryRecord.Embedding` field when a provider is configured (no-op
  otherwise, with a one-line note in the response). `koshi_health`
  reports the configured provider's model + dimensions.
- **Multi-corpus retrieval (#23).** `koshi_index`, `koshi_index_directory`,
  `koshi_search`, `koshi_list_indexed`, and `koshi_clear_index` now
  accept an optional `corpus` parameter. The `default` corpus retains
  the v0.6.0 single-corpus semantics (snapshot persistence,
  auto-index, etc.); named corpora are in-memory only and live
  side-by-side, letting agents query multiple repos without
  thrashing snapshots. `koshi_clear_index(corpus="*")` clears every
  corpus; `koshi_list_indexed(corpus=null)` lists all corpora;
  `koshi_health` reports per-named-corpus stats.
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
