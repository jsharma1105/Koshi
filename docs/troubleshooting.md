# Troubleshooting

The most common symptoms and what to do, grouped by audience.

| Section | When to read |
|---|---|
| [MCP server / client setup](#mcp-server--client-setup) | The server won't start, or your client can't see Koshi. |
| [Indexing and memory](#indexing-and-memory) | `koshi_search` returns nothing, memories disappear, or the index re-builds on every restart. |
| [Python wrapper errors](#python-wrapper-errors) | Your `pip install koshi` workflow raises one of the named exceptions. |
| [Diagnostics checklist](#diagnostics-checklist) | Where to look first. |

---

## MCP server / client setup

| Symptom | Fix |
|---|---|
| `command not found: koshi-mcp` | Ensure your tool prefix is on `PATH`. Shell installer: `~/.local/bin` (Unix) or `%LOCALAPPDATA%\Programs\Koshi` (Windows). `dotnet tool install`: `~/.dotnet/tools` (Unix) or `%USERPROFILE%\.dotnet\tools` (Windows). |
| MCP client can't connect | Run `koshi-mcp` directly and send a JSON-RPC line. Logs appear on stderr; stdout stays silent until a request arrives. If the binary launches cleanly, the client config (path/env) is the issue — see [client setup](client-setup.md) for per-client paths, or install `Koshi.Agents` (`dotnet tool install --global Koshi.Agents`) and run `koshi-agents doctor`. |
| Vulnerability warning during install | The MCP package itself is clean. Some sibling demo projects in the source repo pull in older transitive packages — these never reach `koshi-mcp`. |
| Wrong file written: `mcp_config.json` (underscore) | The correct filename for Copilot CLI is `mcp-config.json` (hyphen). Older releases of `Koshi.Agents` had the wrong default — upgrade to v0.7.1 or later. |
| Server reports a version mismatch on startup | The on-disk binary version does not match the wrapper or persona installer. Reinstall the wrapper that matches the binary, or pin both to the same release tag. |

---

## Indexing and memory

| Symptom | Fix |
|---|---|
| `❌ No documents indexed` from `koshi_search` | Either set `KOSHI_INDEX_PATH` in your client config, or call `koshi_index_directory(path="...")` first. |
| `❌ No supported, readable files found in: ...` | The path is empty, contains only excluded files (binaries, build output, hidden dirs), or no files match the pattern. Check with `koshi_list_indexed` and try a wider `pattern`. |
| Memories disappear on restart | Set `KOSHI_MEMORY_FILE` to an absolute path. The file is created automatically. |
| Index re-built on every restart (slow) | Set `KOSHI_INDEX_FILE` to an absolute path. The snapshot auto-loads on first search; if the source directory has changed since it was saved, the fingerprint check discards it and a fresh re-index runs. |
| Vault changes from `git pull` are not reflected | Confirm `KOSHI_VAULT_WATCH=on` (default). On network mounts / container bind mounts, set `KOSHI_VAULT_WATCH=off` to fall back to reload-on-every-call. |
| Stemming is matching too aggressively | Set `KOSHI_BM25_STEMMING=off`. Useful if your corpus is dominated by exact-match codes or identifiers. |

---

## Python wrapper errors

Every exception is a subclass of `KoshiError` so you can catch them
generically. The specific exceptions tell you what failed before any
tool call ran:

| Exception | Triggered by | Fix |
|---|---|---|
| `BinaryNotFoundError` | Nothing on disk and the download from GitHub failed. | Check network access to `github.com`. Or pre-download the binary and set `KOSHI_BIN=/path/to/koshi-mcp`. |
| `IncompatibleBinaryError` | The on-disk `koshi-mcp --version` does not match the Python package version exactly. | `pip install -U koshi` to align both ends. If you pin `KOSHI_BIN`, install a binary with the matching version tag from the [releases page](https://github.com/jsharma1105/Koshi/releases). |
| `BinaryCorruptedError` | The downloaded binary's SHA-256 does not match the hash baked into the wheel. | Likely a partial download. Delete the cache directory (see [`python/README.md`](https://github.com/jsharma1105/Koshi/blob/main/python/README.md#where-binaries-live)) and let the next `Client()` call re-download. |
| `UnsupportedPlatformError` | No AOT artifact for this OS/CPU (e.g. `osx-x64`). | Install via `dotnet tool install --global Koshi.Mcp` instead. |
| `ProtocolError` | A malformed MCP message arrived from the server (very rare; usually a binary/version mismatch in disguise). | Re-run `pip install -U koshi`. If it persists, file an issue with the exact error text. |
| `ToolError` | The tool ran but the server returned an error response (e.g. `koshi_search` before any index exists). | Check the message — it includes the server-side reason. |
| `KoshiTimeoutError` | A request took longer than `client.timeout` (default 30s). | Increase `Client(timeout=...)` for big `koshi_index_directory` calls. |

---

## Diagnostics checklist

When in doubt, run these in order:

1. **`koshi-mcp --list-tools`** — confirms the binary is on `PATH` and
   the server starts; enumerates every registered tool. No extra install
   required.
2. **`koshi-agents doctor`** (optional) — checks every detected client
   config and tells you the exact JSON snippet to paste if anything is
   missing. Never writes config files. Requires the companion .NET
   tool: `dotnet tool install --global Koshi.Agents`.
3. **`koshi-mcp --version`** — confirms the binary is on `PATH` and
   prints the version string the server will report.
4. **Call `koshi_health` from any MCP client** — shows runtime
   configuration: which env values are in effect, where each path
   resolves, indexed corpus size, memory backend kind, working set.
5. **Read the server logs** — they go to stderr. The MCP client usually
   exposes them under a "Show server logs" / "Inspect" button.

## Next steps

If you still hit something unexpected, search
[open bugs](https://github.com/jsharma1105/Koshi/issues?q=is%3Aissue+is%3Aopen+label%3Abug)
before assuming it's you. If nothing matches, file an issue and include
the output of `koshi_health` plus the smallest failing snippet.
