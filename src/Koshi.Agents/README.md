# Koshi.Agents

> One-command installer for **Koshi sub-agent personas** into Claude Code and GitHub Copilot CLI.

Companion tool to [`Koshi.Mcp`](https://www.nuget.org/packages/Koshi.Mcp/). Where `Koshi.Mcp` ships the **24 MCP tools** (retrieval, memory, context, team telemetry), `Koshi.Agents` ships the **personas that drive those tools** with strict allow-lists, handoff rules, and refusal patterns baked in.

## Install

```bash
dotnet tool install --global Koshi.Agents
```

## Quick start

```bash
# What personas are available?
koshi-agents list

# Show what a persona will do (and which MCP tools it can call)
koshi-agents show koshi-librarian

# Install all 5 personas for Claude Code (also registers the koshi MCP entry)
koshi-agents install --client claude

# ...or Copilot CLI
koshi-agents install --client copilot

# ...or both, scoped to current repo only
koshi-agents install --client both --scope repo

# Install personas only — leave the client's MCP config untouched
koshi-agents install --client copilot --no-mcp

# Preview without writing
koshi-agents install --client claude --dry-run

# Check that everything wired up correctly
koshi-agents doctor

# Remove personas (configs are untouched)
koshi-agents uninstall --client both
```

## The personas

| Persona | Job | Reads | Writes |
|---|---|---|---|
| `koshi-librarian` | Index code/docs & search them | indexes | search index |
| `koshi-memory-keeper` | Persist & recall facts/decisions | memories | memory store |
| `koshi-context-packer` | Pack the prompt window inside a token budget | search + memory (READ-ONLY) | nothing |
| `koshi-quality-coach` | Score & analyze team turns | turn telemetry | scores |
| `koshi-orchestrator` | Route work across the other four | all (advisor) | nothing directly |

Each persona ships in **two formats**:
- **Claude Code agents** → `~/.claude/agents/*.md` with `mcp__koshi__<tool>` allow-lists
- **Copilot CLI agents** → `~/.copilot/agents/*.agent.md` with markdown front-matter

## Scopes

| Scope | Claude Code | Copilot CLI |
|---|---|---|
| `--scope user` (default) | `~/.claude/agents/` | `~/.copilot/agents/` |
| `--scope repo` | `./.claude/agents/` | `./.github/copilot/agents/` |

## What `doctor` checks

- The `koshi-mcp` global tool is installed and on `PATH`
- An MCP config file exists for the requested client
- A `koshi` server entry is registered in that config
- Personas are present in the expected directory
- Tool allow-lists in the personas match the tools the running `koshi-mcp` actually advertises

`doctor` **never writes to client configs**. It tells you what's wrong and prints the exact JSON snippet to paste.

## What this is not

- Not a copy-paste guide. Use `koshi-agents install` instead.
- Not a full config editor. `install` adds (or refreshes) only the
  `mcpServers.koshi` entry in the client's MCP config — every other key
  is left untouched, and the previous file is snapshotted to `<file>.bak`
  before any write. Pass `--no-mcp` to skip the config write entirely,
  or `--dry-run` to preview.
- Not telemetry. Nothing in this tool phones home.

## License

MIT — see [`LICENSE`](https://github.com/jsharma1105/Koshi/blob/main/LICENSE).
