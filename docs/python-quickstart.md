# Python quickstart

A 5-minute, end-to-end tour of `koshi` (the Python client on PyPI).

By the end of this doc you'll have:

1. Installed the package.
2. Indexed a repository.
3. Searched it.
4. Stored a durable memory.
5. Recalled it from a brand-new process — proving persistence.
6. Verified your install with `koshi_health` and `koshi_version`.

No `.NET install` is needed. The Python wheel bundles SHA-256 hashes of every
release binary and auto-downloads the one that matches your OS/CPU on first
call.

---

## 1. Install

```bash
pip install koshi
```

Requires **Python 3.10+** on Linux (x64 or arm64), macOS (x64 or arm64), or
Windows (x64 or arm64). Zero runtime dependencies — every byte is stdlib.

Verify:

```bash
python -c "from koshi import Client; \
with Client() as k: print(k.version())"
```

The first run does three things:

1. Picks an OS/CPU-specific binary name (e.g. `koshi-mcp-linux-x64`).
2. Looks for the binary in `KOSHI_BIN` → versioned cache → `PATH` → downloads
   from the matching GitHub release if nothing was found.
3. SHA-256-verifies the downloaded file against the hash baked into the wheel.
   If the hash doesn't match, the file is deleted and `BinaryCorruptedError`
   is raised — there is no fallback path.

Output looks like:

```
Koshi MCP Server v0.4.1
Status: healthy
Tools: 20 registered
```

---

## 2. Index your repository

```python
from koshi import Client

with Client() as koshi:
    result = koshi.index_directory(
        path="/path/to/your/repo",
        pattern="*.py",       # any glob; defaults to a broad text-file set
    )
    print(result)
```

You should see something like:

```
✓ Indexed 743 chunks across 187 files from /path/to/your/repo (3.2s)
```

The index lives in-memory by default, attached to the spawned server process.
If you want it to outlive a single `Client()` block, persist it by setting
`KOSHI_INDEX_PATH` in the environment before constructing the client — the
server will auto-index that directory on first `search`.

---

## 3. Search

```python
from koshi import Client

with Client() as koshi:
    hits = koshi.search(query="how does authentication work", top_k=5)
    print(hits)
```

This is pure **BM25** — no embeddings, no API calls, no cloud. Results include
the source file, line range, and a ranked relevance score, plus the actual
matching text.

---

## 4. Store a memory

Memories are durable facts, decisions, patterns, and preferences. They survive
across sessions.

```python
from koshi import Client

with Client() as koshi:
    koshi.remember(
        content="We use JWT for auth — 1h access tokens, refresh via /api/refresh.",
        subject="auth pattern",
        type="Decision",   # one of: Fact, Decision, Pattern, Preference
        confidence=0.95,
    )
```

By default, memories are kept in-process. To make them persistent across
restarts, point at a JSON file with `KOSHI_MEMORY_FILE` before creating the
client:

```python
import os
os.environ["KOSHI_MEMORY_FILE"] = "/tmp/koshi-memories.json"

from koshi import Client
with Client() as koshi:
    koshi.remember(content="Postgres 16 with pgvector",
                   subject="database",
                   type="Decision")
```

The file is plain JSON. Open it in any text editor.

---

## 5. Recall from a fresh process

```python
import os, subprocess, sys
os.environ["KOSHI_MEMORY_FILE"] = "/tmp/koshi-memories.json"

# Imagine this is a *new* shell, a *new* day, a *new* deployment.
# As long as KOSHI_MEMORY_FILE points to the same path, the memory is there.
from koshi import Client
with Client() as koshi:
    print(koshi.recall(query="what database do we use", type="All", top_k=3))
```

You should see the Postgres decision come back, with score, tier, and the
timestamps the original `remember()` call wrote.

---

## 6. Verify your install

```python
from koshi import Client

with Client() as koshi:
    print(koshi.version())   # Koshi MCP Server v0.4.1
    print(koshi.health())    # corpus size, memory store, persistence, uptime, GC
```

`koshi.health()` is your one-stop diagnostic. If memory persistence is
configured, it shows the path. If the index is hot, it shows the chunk count.

---

## All 20 tools

| Group | Methods |
|---|---|
| **Retrieval** | `index_directory`, `index`, `search`, `list_indexed`, `clear_index` |
| **Memory** | `remember`, `recall`, `forget`, `memory_stats`, `clear_memories` |
| **Context** | `compile_context`, `token_count`, `budget_plan` |
| **Team / Quality** | `register_team`, `score_turn`, `team_dashboard`, `analyze_feedback`, `list_teams` |
| **Diagnostics** | `version`, `health` |

Full Python API: [`python/README.md`](../python/README.md).
Full tool reference (every argument, every shape): [`src/Koshi.Mcp/README.md`](../src/Koshi.Mcp/README.md#available-tools-20).

---

## Environment variables

| Var | Effect |
|---|---|
| `KOSHI_BIN` | Path to a `koshi-mcp` binary. Skips auto-download, overrides PATH lookup. Version still verified at spawn time. |
| `KOSHI_MEMORY_FILE` | Persist memories to this JSON file across server restarts. Default: in-memory only. |
| `KOSHI_INDEX_PATH` | Default directory the server auto-indexes on first `search` if you don't pass one explicitly. |
| `KOSHI_INDEX_FILE` | Persist the BM25 retrieval index to this JSON file across server restarts. On startup the snapshot auto-loads; if the source directory has drifted (file fingerprint mismatch) it's discarded and a fresh re-index runs. Default: in-memory only — every restart re-chunks from scratch. |

---

## Working with Claude Desktop or Claude Code

The Python client is great for scripts, notebooks, and pipelines — but if you
also use Claude Desktop / Claude Code, point them at the **same** binary the
Python package uses. Run this once to discover where the wheel cached it:

```python
import subprocess
from koshi.binary import resolve_binary
print(resolve_binary())
```

Then in `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) / `%APPDATA%\Claude\claude_desktop_config.json` (Windows) /
`~/.config/Claude/claude_desktop_config.json` (Linux):

```json
{
  "mcpServers": {
    "koshi": {
      "command": "<the path resolve_binary() printed>",
      "env": {
        "KOSHI_MEMORY_FILE": "/your/path/koshi-memories.json"
      }
    }
  }
}
```

That way your Python scripts and your AI assistant share the same memory file
and the same indexed corpus.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `UnsupportedPlatformError` | We don't ship a binary for your OS/CPU combination. File an issue — or build from source. |
| `BinaryCorruptedError` | The downloaded SHA-256 didn't match the one baked into the wheel. Either your wheel is tampered with, or the GitHub release was. Verify both. |
| `IncompatibleBinaryError` | `koshi-mcp` reported a version that doesn't match the `koshi` Python package version. Re-install with `pip install -U koshi`, or delete `KOSHI_BIN` if you pinned an old binary. |
| `BinaryNotFoundError` | Nothing was on disk, and the download failed (no network, GH outage, firewall). Pre-download the binary on a machine with network, set `KOSHI_BIN`. |
| First call is slow | Expected — that's the binary download (~15 MB). Subsequent calls reuse the cached copy. |

---

## Air-gapped install

```bash
# On a machine with internet:
pip download --no-deps koshi -d ./offline-koshi
curl -L -o koshi-mcp \
  https://github.com/jsharma1105/Koshi/releases/download/v0.4.1/koshi-mcp-linux-x64
curl -L -o koshi-mcp.sha256 \
  https://github.com/jsharma1105/Koshi/releases/download/v0.4.1/koshi-mcp-linux-x64.sha256
sha256sum -c <(awk '{print $1"  koshi-mcp"}' koshi-mcp.sha256)

# Move the wheel and binary to the air-gapped machine, then:
pip install ./offline-koshi/koshi-0.4.1-py3-none-any.whl
chmod +x ./koshi-mcp
export KOSHI_BIN=./koshi-mcp
python -c "from koshi import Client; \
with Client() as k: print(k.version())"
```

The Python package version and the binary version **must** match exactly.

---

## Next steps

- [Read the comparison table](../src/Koshi.Mcp/README.md#why-koshi) to see how Koshi stacks up against MCP Memory, AWS KB Retrieval, and RAG SaaS.
- [Set up sub-agent personas](../AGENTS.md) if you also use Claude Code or GitHub Copilot CLI.
- [File an issue](https://github.com/jsharma1105/Koshi/issues) — feedback shapes v0.5.0.
