# koshi

[![PyPI version](https://img.shields.io/pypi/v/koshi.svg)](https://pypi.org/project/koshi/)
[![Python versions](https://img.shields.io/pypi/pyversions/koshi.svg)](https://pypi.org/project/koshi/)
[![License](https://img.shields.io/pypi/l/koshi.svg)](https://github.com/jsharma1105/Koshi/blob/main/LICENSE)

**Python client for the [Koshi MCP server](https://github.com/jsharma1105/Koshi).** Retrieval, memory, context engineering, and quality scoring for any LLM workflow — 24 tools, all wrapped as Pythonic methods.

> No `.NET install` required. The first call auto-downloads a small native AOT binary (~15 MB) into your user cache.

---

## Install

```bash
pip install koshi
```

Requires Python 3.10+ on Linux x64/arm64, macOS x64/arm64, or Windows x64/arm64. Zero runtime dependencies — every byte is Python stdlib.

## Quick start

```python
from koshi import Client

with Client() as koshi:
    print(koshi.version())                          # diagnostics
    koshi.index_directory("/path/to/your/repo")     # BM25 index of all text files
    print(koshi.search("auth pattern", top_k=5))    # ranked retrieval
    koshi.remember(content="JWT tokens expire after 1h",
                   subject="auth", type="Decision")
    print(koshi.recall("auth"))                     # memory recall with recency + confidence
```

The first `Client()` call:

1. Detects your OS/CPU.
2. Looks for the matching `koshi-mcp` binary in `KOSHI_BIN` → versioned cache → `PATH` → GitHub release.
3. Downloads, verifies SHA-256 (hashes are baked into the wheel — supply-chain safe), caches under your user cache dir.
4. Spawns it as a subprocess, performs the MCP `initialize` handshake.
5. Confirms the server's reported version matches the Python package version exactly. Mismatch raises `IncompatibleBinaryError`.

## The 24 tools

| Group | Methods |
|---|---|
| **Retrieval** | `index_directory`, `index`, `search`, `list_indexed`, `clear_index` |
| **Memory** | `remember`, `recall`, `forget`, `memory_stats`, `clear_memories`, `capture_turn`, `memory_export_to_vault`, `memory_import_from_vault`, `memory_sync_vault` |
| **Context** | `compile_context`, `token_count`, `budget_plan` |
| **Team / Quality** | `register_team`, `score_turn`, `team_dashboard`, `analyze_feedback`, `list_teams` |
| **Diagnostics** | `version`, `health` |

Every method returns the formatted text response from the MCP server. The full Koshi tool reference lives in the [main README](https://github.com/jsharma1105/Koshi#mcp-tools).

> 💡 **`capture_turn`** is the v0.8.0 auto-capture entry point — pass a
> 1–3 paragraph summary plus optional `linked_pr` / `linked_commits` and
> the server extracts decision-shape sentences (no LLM, deterministic)
> and persists them as `Decision` memories with a provenance footer.
> Combine with `KOSHI_MEMORY_VAULT` pointing at a Git-tracked directory
> and your teammates inherit the same captured decisions on `git pull` —
> the cross-platform "skip the regression" loop. See the
> [recommended snippet](https://github.com/jsharma1105/Koshi/blob/main/docs/copilot-instructions-snippet.md)
> for the agent-steering text.

## Environment variables

| Var | Effect |
|---|---|
| `KOSHI_BIN` | Path to a `koshi-mcp` binary. Skips auto-download, overrides PATH lookup. Version still verified at spawn time. |
| `KOSHI_MEMORY_FILE` | Persist memories to this JSON file across server restarts. Default: in-memory only. |
| `KOSHI_INDEX_PATH` | Default directory for `search` / `index_directory` if you don't pass one. |
| `KOSHI_INDEX_FILE` | Persist the BM25 retrieval index to this JSON file across server restarts. Auto-loads on first `search`; stale snapshots are invalidated by a `(relpath, size, mtime)` fingerprint check. Default: in-memory only. |

## Where binaries live

| OS | Cache path |
|---|---|
| Linux | `$XDG_CACHE_HOME/koshi/bin/<version>/` or `~/.cache/koshi/bin/<version>/` |
| macOS | `~/Library/Caches/koshi/bin/<version>/` |
| Windows | `%LOCALAPPDATA%\koshi\bin\<version>\` |

The version is part of the path, so upgrading `pip install -U koshi` does not invalidate the cache for older Python projects pinned to an older `koshi` version.

## Air-gapped / offline

Download the binary that matches your platform from a GitHub release of [Koshi](https://github.com/jsharma1105/Koshi/releases) on a machine with internet, copy it onto the target machine, and point `KOSHI_BIN` at it. The version must match the installed `koshi` Python package exactly.

## Error handling

```python
from koshi import (
    Client,
    KoshiError,            # base class
    BinaryNotFoundError,   # nothing on disk + download failed
    IncompatibleBinaryError,  # version mismatch
    BinaryCorruptedError,  # SHA-256 mismatch
    UnsupportedPlatformError,  # no AOT artifact for this OS/CPU
    ProtocolError,         # malformed MCP message
    ToolError,             # tool returned an error response
    KoshiTimeoutError,     # request took longer than client.timeout
)
```

## Why this exists

Koshi's engine is .NET. That's a deliberate choice — the runtime gets us best-in-class JSON, threading, and a great MCP SDK. But it's also adoption friction: most MCP users live in Python.

`koshi` (Python) closes the gap. You `pip install koshi` and never think about .NET. Under the hood, you're talking to the same engine the .NET ecosystem uses (`Koshi.Mcp` on NuGet), wire-compatible byte-for-byte.

## Links

- **Source & issues**: <https://github.com/jsharma1105/Koshi>
- **Release binaries**: <https://github.com/jsharma1105/Koshi/releases>
- **NuGet (.NET tool)**: <https://www.nuget.org/packages/Koshi.Mcp>
- **Changelog**: <https://github.com/jsharma1105/Koshi/blob/main/CHANGELOG.md>

## License

MIT — see [LICENSE](https://github.com/jsharma1105/Koshi/blob/main/LICENSE).
