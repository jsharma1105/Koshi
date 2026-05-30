# Client setup

How to register `koshi-mcp` with every supported MCP client.

> **Easiest path:** run `koshi-mcp init` after installing — it detects the
> clients on your machine and writes (or merges) the `mcpServers.koshi`
> entry for each one. The manual snippets below are for clients the
> wizard does not write automatically and for users who prefer to edit
> configs by hand.

The five clients Koshi has been tested against (in alphabetical order):

| Client | Wizard writes config? | Manual snippet |
|---|:---:|---|
| Claude Desktop / Claude Code | ✅ | [below](#claude-desktop--claude-code) |
| Cursor | ❌ | [below](#cursor) |
| GitHub Copilot CLI | ✅ | [below](#github-copilot-cli) |
| Microsoft Agency | ❌ | [below](#microsoft-agency) |
| Windsurf | ❌ | [below](#windsurf) |
| Generic stdio MCP client | n/a | [below](#generic-stdio-mcp-client) |

After editing any config, **restart the client once** so it picks up the new
`koshi` entry. The wizard prints the same reminder.

---

## Claude Desktop / Claude Code

Edit `claude_desktop_config.json` (Claude Desktop) or `.claude/settings.json`
(Claude Code):

| OS | Path |
|---|---|
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| Linux | `~/.config/Claude/claude_desktop_config.json` |

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_PROJECT_ROOT": "/Users/you/code/my-project",
        "KOSHI_INDEX_PATH": "/Users/you/code/my-project"
      }
    }
  }
}
```

> ⚠️ **Claude Desktop caveat:** Claude Desktop launches MCP servers with
> `cwd=$HOME`, not your project. Always set `KOSHI_PROJECT_ROOT`
> explicitly so the `<project>/.koshi/` defaults resolve correctly.

---

## GitHub Copilot CLI

```bash
copilot mcp add koshi koshi-mcp \
  --env KOSHI_PROJECT_ROOT=/path/to/project \
  --env KOSHI_INDEX_PATH=/path/to/project
```

Or edit `~/.copilot/mcp-config.json` directly (note the hyphen):

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_PROJECT_ROOT": "/path/to/project"
      }
    }
  }
}
```

Copilot CLI launches MCP servers with `cwd=$PWD`, so the project-root
defaults work without `KOSHI_PROJECT_ROOT` if you always launch from the
project directory.

---

## Cursor

Add to `.cursor/mcp.json` in your project root (or to the global config
under `~/.cursor/`):

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_PROJECT_ROOT": "${workspaceFolder}",
        "KOSHI_INDEX_PATH": "${workspaceFolder}"
      }
    }
  }
}
```

Cursor expands `${workspaceFolder}` to the open project, so the same
snippet works across every repo.

---

## Windsurf

Add to `.windsurf/mcp.json`:

```json
{
  "mcpServers": {
    "koshi": {
      "command": "koshi-mcp",
      "env": {
        "KOSHI_PROJECT_ROOT": "${workspaceFolder}"
      }
    }
  }
}
```

---

## Microsoft Agency

[Agency](https://aka.ms/agency) wraps Copilot CLI and Claude Code with
marketplace plugins. Koshi ships `plugin.json` at the repo root and
`.claude-plugin/plugin.json` for compatibility with both formats.

**Option A — one-shot MCP proxy** (simplest, no install):

```bash
agency mcp local --command koshi-mcp \
  --env-var KOSHI_PROJECT_ROOT=C:\path\to\project \
  --env-var KOSHI_INDEX_PATH=C:\path\to\project
```

**Option B — persistent Agency plugin** (from a fork or marketplace):

```bash
# Once Koshi is published to a marketplace repo:
agency plugin install gh:jsharma1105/Koshi@main
agency copilot --plugin koshi
```

---

## Generic stdio MCP client

Any MCP client that supports stdio transport works:

```bash
# Server reads JSON-RPC on stdin and writes responses on stdout.
# Logs go to stderr only — stdout is reserved for the protocol.
# Pass `--version` or `--help` for one-shot informational output.
koshi-mcp
```

If your client expects a different transport (HTTP, SSE) Koshi will not
work today — file an issue with the client name and we will scope it.

---

## Verifying the registration

After editing any config:

```bash
koshi-agents doctor
```

`doctor` checks each registered client config, confirms the `koshi`
entry resolves to an installed `koshi-mcp` on `PATH`, and prints the
exact snippet to paste if anything is missing. It never writes config
files.

## Next steps

- [Configuration reference](configuration.md) — all 11 environment variables.
- [Walkthroughs](walkthroughs.md) — index, search, remember, share.
- [Troubleshooting](troubleshooting.md) — fixes for the most common setup
  errors.
