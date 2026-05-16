# Security Policy

## Reporting a vulnerability

If you believe you have found a security vulnerability in Koshi, **please do not
open a public GitHub issue**. Instead, report it privately:

1. Use GitHub's **["Report a vulnerability"](https://github.com/jsharma1105/Koshi/security/advisories/new)**
   button on the repository's Security tab. This routes the report to the
   maintainers privately and starts a coordinated disclosure thread.
2. Include in your report:
   - A clear description of the issue.
   - Steps to reproduce, including the affected version (e.g. `koshi-mcp 0.2.0`),
     OS, .NET runtime version, and MCP client.
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
- Build / release workflows in `.github/workflows/`.

Out of scope:

- Issues in third-party MCP clients (Claude Code, Copilot CLI, Cursor, Windsurf).
  File those with the respective vendors.
- Issues in transitive dependencies that are not exploitable in Koshi's default
  configuration. Please file with the upstream maintainer.
- Demo and example projects under `src/Koshi.*.Demo/` and `src/Koshi.Cli/`.

## Security posture

Koshi is designed to fail safely:

- **No network calls.** The default install never makes outbound HTTP/HTTPS
  requests.
- **No telemetry.** No analytics, no phone-home.
- **Stdio transport only.** `koshi-mcp` does not open listening ports.
- **Logs to stderr only.** Stdout is reserved for MCP JSON-RPC.
- **Safe file enumeration.** `koshi_index_directory` skips hidden directories,
  build output, secret patterns, sensitive extensions, symlinks, and files
  above a configurable size cap. See `SafeFileEnumerator.cs`.
- **No code execution paths.** Indexed content is parsed as text. No
  deserialization of untrusted code, no shell-out, no `eval`.

If you find a deviation from any of these guarantees, that is a security bug —
please report it.

## Supported versions

| Version | Supported |
|---------|-----------|
| 0.3.x   | ✅ Yes — current |
| 0.2.x   | ❌ No  — superseded; upgrade to 0.3.x |
| 0.1.x   | ❌ No  — pre-release; upgrade to 0.3.x |

## Hall of fame

Researchers who report valid vulnerabilities will be credited here (with
permission) once a fix ships.
