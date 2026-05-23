# Koshi vault mode — share memories across teams via Git

> **Status:** Available since v0.6.0. The vault backend is opt-in; setting
> `KOSHI_MEMORY_VAULT` activates it, otherwise Koshi continues to use the
> single-file JSON backend (`KOSHI_MEMORY_FILE`) byte-identically to v0.5.x.

---

## TL;DR

```bash
# 1. Pick (or create) a folder. Any folder. It can be an Obsidian vault,
#    a Foam/Logseq workspace, or just an empty directory.
mkdir -p ~/teams/our-memories

# 2. Point Koshi at it.
export KOSHI_MEMORY_VAULT="$HOME/teams/our-memories"

# 3. (Optional but recommended.) Track it in Git so your teammates get the
#    same memories.
cd ~/teams/our-memories
git init
git add koshi/
git commit -m "seed koshi memories"
git remote add origin git@github.com:your-team/agent-memories.git
git push -u origin main
```

That's it. Every `koshi_remember` from now on writes one Markdown file to
`~/teams/our-memories/koshi/<type>/<subject>--mem-NNNNNN.md`. Every
teammate who clones the repo and sets `KOSHI_MEMORY_VAULT` to their local
checkout sees the same memories.

> **Tip — relative vault paths.** Since v0.6.0, `KOSHI_MEMORY_VAULT` accepts
> a relative path; it's resolved against `KOSHI_PROJECT_ROOT` (which itself
> defaults to the cwd Koshi was launched from). So
> `KOSHI_MEMORY_VAULT=team-vault` lands the vault next to your code at
> `<project>/team-vault/koshi/...`, which is handy when you want the vault
> to live inside the project repo. Absolute paths still work exactly as
> before.

---

## Why a vault?

The legacy `KOSHI_MEMORY_FILE` backend stores everything in a single opaque
JSON blob. That works fine for a single user but breaks down for teams:

| Problem with JSON file | How vault mode fixes it |
|---|---|
| Not human-readable — you can't open a memory in a text editor | One `.md` file per memory, plain Markdown |
| Every save rewrites the whole file → noisy Git diffs | One file per change → clean diffs |
| Merge conflicts always touch the same JSON file → conflict storms | Each memory is independent → conflicts only on the few memories actually changed |
| Can't ingest your existing notes/wikis | Drop any Obsidian/Foam/Logseq vault into `KOSHI_MEMORY_VAULT` — your notes coexist with Koshi memories |
| Editing a memory means restarting the server | Vault reloads from disk on every tool call — edits take effect immediately |

---

## On-disk format

Every Koshi-managed memory is a Markdown file with a YAML frontmatter block
that contains a `koshi:` sub-block. Here's a complete example:

```markdown
---
koshi:
  id: mem-000123
  type: Decision
  scope:
    user: "*"
    workspace: opp
    thread: null
  source: user
  confidence: 0.90
  created-at: 2026-05-21T21:05:00Z
  updated-at: 2026-05-22T09:14:00Z
  last-accessed-at: 2026-05-22T09:14:00Z
  access-count: 3
  tier: Hot
tags: [database, dome, decisions]
aliases: [chose-dapper-over-ef]
---
# We chose Dapper over EF Core

EF Core change-tracking overhead breaks our async-streaming bulk-update
use cases. Dapper's micro-ORM model maps cleanly to our stored-proc-only
data layer.
```

### File-system layout

```
<vault>/
└── koshi/
    ├── facts/
    │   └── ols-offer-id-format--mem-000001.md
    ├── decisions/
    │   └── we-chose-dapper-over-ef--mem-000123.md
    ├── patterns/
    │   └── controller-to-repo-via-isqlhelper--mem-000045.md
    └── preferences/
        └── prefer-records-over-classes--mem-000088.md
```

- `<vault>` is whatever directory you set in `KOSHI_MEMORY_VAULT`.
- `koshi/` is created automatically. Koshi only writes inside this
  subdirectory — your other notes (siblings of `koshi/`) are never touched.
- Subdirectories are one per `MemoryType` (lowercase plural):
  `facts`, `decisions`, `patterns`, `preferences`.

### Filename rules

`<subject-slug>--mem-<id>.md`

- `<subject-slug>` is the ASCII-lowercase kebab-case version of the memory's
  subject (max 64 chars, Windows-reserved names like `CON` / `PRN` /
  `NUL` are sanitized, trailing dots/spaces stripped).
- The `--` separator followed by `mem-NNNNNN.md` makes it trivial to look
  up a memory by id without parsing the filename.
- **Filename is cosmetic.** Koshi finds memories by the `koshi.id` field in
  frontmatter, not by filename. Rename `foo--mem-000001.md` to anything you
  like and Koshi still finds it.
- When you change a memory's subject, Koshi atomically moves the file to
  the new slug path (write new, delete old) — never two files for the same
  id.

### What's stored in `koshi:`

| Field | Required | Notes |
|---|---|---|
| `id` | yes | Format: `mem-NNNNNN`. The identity of the memory. |
| `type` | yes | One of `Fact`, `Decision`, `Pattern`, `Preference`. |
| `scope.user` | yes | A user id, or `"*"` for global. Always quoted in YAML. |
| `scope.workspace` | yes | Workspace id (e.g. `opp`). |
| `scope.thread` | yes | Thread id, or `null`. |
| `source` | yes | Free-text source label (`user`, `llm`, `agent:foo`, …). |
| `confidence` | yes | `0.00`–`1.00` (invariant-culture decimal). |
| `created-at` | yes | ISO-8601 UTC. |
| `updated-at` | yes | ISO-8601 UTC. Bumped on every write. |
| `last-accessed-at` | yes | ISO-8601 UTC. Updated on recall. |
| `access-count` | yes | Integer ≥ 0. |
| `tier` | yes | One of `Hot`, `Warm`, `Cold`. |
| `superseded-by` | no | Another `mem-NNNNNN` id. Omitted when null. |
| `contradiction-note` | no | Free-text. Omitted when null. |

### What's NOT stored in `koshi:`

The following fields exist in the in-memory `MemoryRecord` but are
**intentionally not written to disk**, to avoid bloating Git diffs:

- `Embedding` (float vector — regenerated on demand)
- `EmbeddingModel`, `EmbeddingDimensions` (paired with the vector)
- `CompressedContent` (derived from `Content`)

A JSON → vault → JSON round-trip loses ONLY these fields. The smoke test
asserts this explicitly.

### Your own frontmatter is preserved

Vault mode uses **frontmatter surgery**, not a general-purpose YAML
rewriter. Koshi only edits the `koshi:` sub-block. Everything else inside
the `---...---` fences is preserved **byte-identically** on rewrite:

```yaml
---
koshi:
  # Koshi owns this block — re-rendered canonically on every save
  id: mem-000123
  # ...
tags: [database, dome]       # ← preserved
aliases: [chose-dapper]      # ← preserved
cssclass: code-doc           # ← preserved (Obsidian)
publish: true                # ← preserved (Quartz / Obsidian Publish)
description: |               # ← preserved, including block-scalar form
  Multi-line
  description.
---
```

If you want to attach Obsidian-style tags to every Koshi-managed memory,
just add `tags: [koshi, decisions]` (or whatever) to the file once and
Koshi will leave it alone forever.

---

## Coexistence with your existing vault

Already use Obsidian / Foam / Logseq? Point `KOSHI_MEMORY_VAULT` at the
same root and Koshi will:

1. **Create `<vault>/koshi/` on first use** if it doesn't exist.
2. **Only write inside `koshi/`** — your other notes are never touched.
3. **Report hand-created notes under `koshi/`** (notes without a `koshi.id`
   in frontmatter) as "unmanaged notes" in `koshi_memory_stats`. They're
   surfaced so you know they exist, but never auto-promoted into the
   memory store and never overwritten.

If you'd like to **promote** a hand-written note into a managed memory,
the supported flow is:

1. Open the note in a text editor.
2. Add a complete `koshi:` block to its frontmatter (see "What's stored in
   `koshi:`" above — all required fields).
3. The next tool call (which triggers a vault re-scan) picks it up.

---

## How edits flow

Vault mode reloads from disk on **every tool call**. That means:

- **Edit a memory's body in Obsidian / VS Code / vim** → next `koshi_recall`
  returns the edited body, no server restart needed.
- **Delete a `.md` file** → next tool call notices it's gone, recall stops
  returning it.
- **`git pull` brings down new memories from a teammate** → they're
  available on the next tool call.
- **Add a memory by hand** with a fresh `koshi:` block → ditto.

The trade-off: every tool call does an O(n) directory scan. For ≤ 1000
memories this is sub-millisecond on a local SSD. A `FileSystemWatcher`-
backed cache (skip the scan when the cache is known fresh) is planned for
v0.6.1.

---

## When both env vars are set

If you set **both** `KOSHI_MEMORY_VAULT` and `KOSHI_MEMORY_FILE`:

- **Vault wins.** All operations go through the vault backend.
- A one-line warning is emitted to stderr at startup so this isn't silent.
- `KOSHI_MEMORY_FILE` is completely ignored — no reads, no writes.

To migrate cleanly:

1. **Export** your existing JSON memories into a vault directory while
   `KOSHI_MEMORY_FILE` is still active (vault not set):
   ```
   koshi_memory_export_to_vault(vaultPath="~/teams/our-memories")
   ```
2. Switch your environment: unset `KOSHI_MEMORY_FILE`, set
   `KOSHI_MEMORY_VAULT=~/teams/our-memories`.
3. Verify with `koshi_memory_stats` → backend should now report `vault`.

---

## Sync tools

Three MCP tools manage flow between backends:

### `koshi_memory_export_to_vault(vaultPath, overwrite=false)`

Bulk-export the current memory store to a vault directory. Refuses to
write into a non-empty `<vaultPath>/koshi/` tree unless `overwrite=true`
(in which case it performs a destructive `ReplaceAll`).

Use this to:
- Migrate from JSON → vault.
- Take a snapshot of the live store to commit to a shared repo.

### `koshi_memory_import_from_vault(vaultPath, mode="merge")`

Import memories from a vault into the current backend. Three modes:

| Mode | Behavior on id collision |
|---|---|
| `merge` | **Current wins** — vault entries with the same id are skipped. |
| `overlay` | **Vault wins** — vault entries replace the current entry. |
| `replace` | **Destructive** — current store is wiped, then vault is loaded. |

Use this to:
- Bootstrap a new machine from a teammate's vault.
- Overlay a known-good snapshot to recover from a bad state.

### `koshi_memory_sync_vault()`

Force a fresh re-scan of the vault and refresh the in-memory cache.
No-op for the JSON backend. Useful after a `git pull` if you want
immediate visibility without waiting for the next tool call to trigger
the per-call reload.

---

## Git tips

### `.gitattributes`

Mark the koshi files as text so Git's diff/merge logic Just Works:

```
koshi/**/*.md text eol=lf
```

### Merge conflicts

The most common conflict is two teammates both editing the same memory.
Because each memory is in its own file, conflicts are localized. Git's
3-way merge usually handles them cleanly when both sides only edited
different fields in the `koshi:` block. If a conflict marker ends up
inside the `koshi:` block:

1. Resolve the conflict (pick one side or merge fields manually).
2. The next tool call will reload from disk and validate the frontmatter.
3. If the frontmatter is malformed, the file is **rejected with a stderr
   diagnostic** — Koshi will NOT silently overwrite it. Fix and try again.

### `.gitignore`

The vault directory itself should be checked in. But you might want to
keep the JSON backend file out:

```gitignore
# Legacy single-file backend — only checked in if you're explicitly
# versioning it. Most teams use the vault mode and skip this file.
.koshi-memory.json
```

---

## FAQ

### Q: My teammate added a memory and I don't see it.

Run `koshi_memory_sync_vault()` or just trigger any other Koshi tool —
the next call reloads the vault. If still missing, check:

- Is the new file in `<vault>/koshi/<type>/`?
- Does it have a complete `koshi:` block in frontmatter?
- Run `koshi_memory_stats` — does it show in the unmanaged-note list?
  (If yes, the `koshi:` block is missing or malformed.)

### Q: I deleted a memory but it came back.

In vault mode it shouldn't — vault reloads on every tool call. If you
deleted via `koshi_forget`, the file IS removed from disk. If a deleted
memory reappears, check whether someone else committed it back to your
shared Git repo.

### Q: My subject got renamed but the old file is still there.

Open an issue. The expected behavior is atomic move — write new, delete
old. If you see a duplicate, Koshi will pick the newest mtime on the next
load and emit a stderr warning. You can delete the older file by hand.

### Q: Can I use a sibling directory of an Obsidian vault?

Yes — `KOSHI_MEMORY_VAULT` can point at any directory. If it's an Obsidian
vault, Obsidian will pick up the `koshi/` subdirectory automatically and
render the Koshi-managed notes as part of the graph.

### Q: Does the vault support binary blobs (PDFs, images)?

No — vault mode is for text memories only. The body is plain Markdown.
If you need to attach an image, link to it from the body using a normal
Markdown image syntax — Obsidian / Foam will resolve it like any other
note attachment.

### Q: What about embeddings? I want semantic search across memories.

Embeddings are computed on-demand from the body and **not persisted to
disk** — they'd bloat Git diffs and become stale on body edits. Koshi
re-embeds memories the first time it needs them in a process; subsequent
calls hit the in-memory cache.

### Q: Can I version-control the JSON backend instead?

You can, but you'll hit the merge-conflict storms described at the top of
this doc. Vault mode exists specifically to avoid that. If you want both,
use the vault as your source of truth and use
`koshi_memory_export_to_vault` to dump a JSON snapshot whenever you want
one for archival.

---

## Status & roadmap

| Version | What changes |
|---|---|
| 0.6.0 | Vault backend, three sync tools, unmanaged-note reporting, per-call disk reload. |
| 0.6.1 | `FileSystemWatcher`-backed cache invalidation — skips the per-call scan when the cache is known fresh. |
| 0.7.0 | Multi-flavor file layouts via `KOSHI_VAULT_FLAVOR`: Obsidian (default), Foam, Logseq (`<vault>/pages/koshi-<type>-<slug>.md`), Dendron (`<vault>/koshi.<type>.<slug>.md`). The frontmatter schema is unchanged across flavors. |
| **0.8.0** (current) | `koshi_capture_turn` — turn-end auto-capture of decision-shape sentences from agent summaries. Persists each as a `Decision` memory with optional `linked_pr` / `linked_commits` provenance. Pair with [`docs/copilot-instructions-snippet.md`](./copilot-instructions-snippet.md). |
| Coming | Durable-write contract (issue [#48](https://github.com/jsharma1105/Koshi/issues/48)) — backends will throw `MemoryPersistenceException` instead of swallowing IO failures, so tools report ❌ on persistence failure with cache rolled back. |

---

## Reporting issues

If something goes wrong, please open an issue at
<https://github.com/jsharma1105/Koshi/issues> with:

- Output of `koshi_memory_stats` (shows backend, location, counts).
- Output of `koshi_diagnostics` (shows env vars).
- The contents of any `.md` file Koshi is misbehaving on (after redacting
  anything sensitive).
