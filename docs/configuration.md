# Configuration reference

Koshi is configured exclusively through **environment variables** — no
config files, no flags. Set them in your MCP client's `env` block (see
[client setup](client-setup.md)).

Since v0.6.0 every path env var derives a sensible default from the
project root, so most users never need to set anything. If you launch
`koshi-mcp` from `~/myrepo` (or `C:\src\myrepo`), your memory and index
land at `<project>/.koshi/memory.json` and `<project>/.koshi/index.json`
automatically.

> ⚠️ **Claude Desktop caveat:** Claude Desktop launches MCP servers with
> `cwd=$HOME`, not your project. Set `KOSHI_PROJECT_ROOT` explicitly in
> `claude_desktop_config.json`. Copilot CLI and Cursor launch servers
> with `cwd=$PWD`, so defaults Just Work there.

## All 11 environment variables

| Env var | Default | Purpose |
|---|---|---|
| [`KOSHI_PROJECT_ROOT`](#koshi_project_root) | `Environment.CurrentDirectory` | Base for all derived paths below. |
| [`KOSHI_INDEX_PATH`](#koshi_index_path) | `<root>` | Auto-index directory for `koshi_search`. |
| [`KOSHI_INDEX_FILE`](#koshi_index_file) | `<root>/.koshi/index.json` | Persists the BM25 index across restarts. |
| [`KOSHI_MEMORY_FILE`](#koshi_memory_file) | `<root>/.koshi/memory.json` | Persists memories across restarts. |
| [`KOSHI_MEMORY_VAULT`](#koshi_memory_vault) | _unset_ | Markdown-vault memory backend (Git-friendly). |
| [`KOSHI_VAULT_FLAVOR`](#koshi_vault_flavor) | `obsidian` | Vault file layout. |
| [`KOSHI_VAULT_WATCH`](#koshi_vault_watch) | `on` | Watch the vault for external edits. |
| [`KOSHI_TOKENIZER_MODEL`](#koshi_tokenizer_model) | `gpt-4` | Tokenizer model for chunking + budget. |
| [`KOSHI_CHUNK_MAX_TOKENS`](#koshi_chunk_max_tokens) | `512` | Default chunk size for indexing. |
| [`KOSHI_CHUNK_OVERLAP_TOKENS`](#koshi_chunk_overlap_tokens) | `50` | Default chunk overlap. |
| [`KOSHI_BM25_STEMMING`](#koshi_bm25_stemming) | `on` | English stemmer for `search` / `recall`. |

Absolute env values are used as-is. Relative values resolve against
`KOSHI_PROJECT_ROOT`. Empty or whitespace values are treated as unset.
Run `koshi_health` to see exactly which value is in effect for each path
and whether it came from `[env]` or `[default]`.

---

## Per-variable detail

### `KOSHI_PROJECT_ROOT`

Base directory used to derive defaults for every other path env var.
Resolves relative env values like `KOSHI_INDEX_PATH=./src` against this
root. Set it explicitly in Claude Desktop configs where the spawned
working directory is not your project.

### `KOSHI_INDEX_PATH`

Absolute path that `koshi_search` auto-indexes on first use.
**Auto-index is opt-in** — only set this when you want the first
`koshi_search` call to scan the directory automatically. When unset,
`koshi_search` requires an explicit `koshi_index_directory(path)` first.

### `KOSHI_INDEX_FILE`

Path to a JSON file used to persist the BM25 retrieval index across
server restarts. On startup the snapshot is auto-loaded and validated
against the live filesystem (`relpath + size + mtime` fingerprint);
stale snapshots are discarded and a re-index runs. Writes are atomic and
the file is schema-versioned.

### `KOSHI_MEMORY_FILE`

Path to a JSON file used to persist memories across server restarts.
Writes are atomic and the file is schema-versioned. **Ignored when
`KOSHI_MEMORY_VAULT` is also set** — a one-line stderr warning is
emitted when both are explicitly set.

### `KOSHI_MEMORY_VAULT`

Path to a directory used to persist memories as **one Markdown file per
memory** under a flavor-specific layout (see `KOSHI_VAULT_FLAVOR`).
Git-friendly. External edits and deletes are picked up automatically via
a file-system watcher (see `KOSHI_VAULT_WATCH`). Relative paths resolve
against `KOSHI_PROJECT_ROOT`. See [vault mode](vault-mode.md) for the
full format spec and migration guide.

### `KOSHI_VAULT_FLAVOR`

Selects the file layout used when `KOSHI_MEMORY_VAULT` is set. Valid
values:

| Flavor | Layout |
|---|---|
| `obsidian` (default) | `<vault>/koshi/<type>/<slug>--<id>.md` |
| `foam` | identical to `obsidian` |
| `logseq` | `<vault>/pages/koshi-<type>-<slug>--<id>.md` (flat) |
| `dendron` | `<vault>/koshi.<type>.<slug>--<id>.md` (dot-namespaced at vault root) |

The wire format (YAML frontmatter) is identical across flavors; only
filename and placement differ. Unrecognized values log a stderr warning
and fall back to `obsidian`.

### `KOSHI_VAULT_WATCH`

When `KOSHI_MEMORY_VAULT` is set, controls whether Koshi attaches a
`FileSystemWatcher` to the vault. With the watcher (default), reloads
only happen when external changes are observed — near-zero steady-state
cost. Set to `off` / `false` / `0` on network mounts, container bind
mounts, or any FS where inotify-style events are unreliable; Koshi falls
back to reload-on-every-call.

### `KOSHI_TOKENIZER_MODEL`

Selects the encoding used by both retrieval (chunking) and context
(token counts). Accepts any model name supported by
[SharpToken](https://github.com/dmitry-brazhenko/SharpToken):

| Model name | Encoding |
|---|---|
| `gpt-4`, `gpt-3.5-turbo` | `cl100k_base` |
| `gpt-4o`, `gpt-4o-mini` | `o200k_base` |

Read once per tokenizer access. Changes mid-process take effect on the
next call.

### `KOSHI_CHUNK_MAX_TOKENS`

Default chunk size (in tokens) used by `koshi_index` and
`koshi_index_directory` when the per-call `maxTokens` argument is unset.
Clamped to `[64, 2048]`.

### `KOSHI_CHUNK_OVERLAP_TOKENS`

Default overlap (in tokens) between consecutive chunks. Clamped to
`[0, 256]` AND `< maxTokens / 2`.

### `KOSHI_BM25_STEMMING`

When set to `off` / `false` / `0`, disables the built-in English stemmer
that backs `koshi_search` / `koshi_recall`. Useful if your corpus is
dominated by exact-match codes or identifiers.

---

## Default safety limits

These are hardcoded but tweakable per-call where applicable:

- Max indexed chunks: **50,000**
- Max stored memories: **1,000**
- Max file size for `koshi_index_directory`: **256 KB** (override per call)
- Max files scanned: **5,000** (override per call)
- `topK` clamped to **1–50** (search) and **1–25** (recall)

## Next steps

- [Tools](tools.md) — full reference for the 24 MCP tools.
- [Walkthroughs](walkthroughs.md) — common patterns and recipes.
- [Vault mode](vault-mode.md) — Git-shared memory layout.
