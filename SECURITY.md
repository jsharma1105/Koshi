# Security Policy

## Reporting a vulnerability

If you believe you have found a security vulnerability in Koshi, **please do not
open a public GitHub issue**. Instead, report it privately:

1. Use GitHub's **["Report a vulnerability"](https://github.com/jsharma1105/Koshi/security/advisories/new)**
   button on the repository's Security tab. This routes the report to the
   maintainers privately and starts a coordinated disclosure thread.
2. Include in your report:
   - A clear description of the issue.
   - Steps to reproduce, including the affected version (e.g. `koshi-mcp 0.4.0`,
     `koshi (Python) 0.4.0`), OS / CPU architecture, .NET runtime version (if
     applicable), Python version (if applicable), and MCP client.
   - For Python-client reports: whether the binary came from auto-download,
     `KOSHI_BIN`, or `PATH`; and the exact SHA-256 your `koshi` package
     expected vs. the SHA-256 of the binary that ran.
   - The potential impact (information disclosure, code execution, etc.).
   - Any suggested mitigation.

We will acknowledge receipt within **3 business days** and aim to provide an
initial assessment within **10 business days**. Coordinated disclosure timelines
are negotiable based on severity.

## Scope

In scope:

- `Koshi.Mcp` MCP server (NuGet package, `koshi-mcp` binary).
- `Koshi.Agents` persona installer (NuGet package, `koshi-agents` binary).
- `Koshi.Core` library.
- **Native AOT release binaries** (`koshi-mcp-<rid>`) published as GitHub
  release assets, including their `.sha256` sidecars and the `manifest.json`.
- **`koshi` Python package** on PyPI — the resolver, downloader, SHA-256
  verifier, and JSON-RPC client (`python/src/koshi/`).
- Build / release workflows in `.github/workflows/`.
- `scripts/inject-manifest.py` — release-time hash injector for the Python
  wheel.

Out of scope:

- Issues in third-party MCP clients (Claude Code, Copilot CLI, Cursor, Windsurf).
  File those with the respective vendors.
- Issues in transitive dependencies that are not exploitable in Koshi's default
  configuration. Please file with the upstream maintainer.
- Demo and example projects under `src/Koshi.*.Demo/` and `src/Koshi.Cli/`.

## Security posture

Koshi is designed to fail safely:

- **No runtime network calls.** Once started, `koshi-mcp` (NuGet, AOT binary,
  or spawned by the Python client) makes zero outbound HTTP/HTTPS requests for
  the full lifetime of the server process.
- **First-use download is the only network code path.** The `koshi` Python
  package may download a matching `koshi-mcp` binary from a pinned
  GitHub-release URL on first use *if* `KOSHI_BIN` is unset and no binary is
  on `PATH`. The download is over HTTPS, the SHA-256 is baked into the wheel
  at release time (not fetched), and the file is atomically replaced under a
  per-cache-dir lock. Set `KOSHI_BIN` to an offline binary to eliminate even
  this path.
- **Wheel ↔ binary integrity is cryptographic.** `scripts/inject-manifest.py`
  embeds the SHA-256 of every per-RID binary into the wheel before publish.
  A tampered binary on the GitHub release alone is insufficient to compromise
  a `pip install koshi` user — the wheel itself has to be tampered with too.
- **No telemetry.** No analytics, no phone-home, in any distribution channel.
- **Stdio transport only.** `koshi-mcp` does not open listening ports.
- **Logs to stderr only.** Stdout is reserved for MCP JSON-RPC.
- **Safe file enumeration.** `koshi_index_directory` skips hidden directories,
  build output, secret patterns, sensitive extensions, symlinks, and files
  above a configurable size cap. See `SafeFileEnumerator.cs`.
- **No code execution paths.** Indexed content is parsed as text. No
  deserialization of untrusted code, no shell-out, no `eval`.
- **AOT-clean engine.** `Koshi.Mcp` is `<IsAotCompatible>true</IsAotCompatible>`
  with an exhaustive `WarningsAsErrors` list (IL2026–IL3056) and a source-
  generated JSON context. No reflection-driven JSON paths are reachable at
  runtime, which closes off most JSON-payload deserialization gadgets.

If you find a deviation from any of these guarantees, that is a security bug —
please report it.

## Supported versions

| Version | Supported |
|---------|-----------|
| 0.4.x   | ✅ Yes — current |
| 0.3.x   | ✅ Yes — previous stable, fix-only |
| 0.2.x   | ❌ No  — superseded; upgrade to 0.4.x |
| 0.1.x   | ❌ No  — pre-release; upgrade to 0.4.x |

## Hall of fame

Researchers who report valid vulnerabilities will be credited here (with
permission) once a fix ships.
