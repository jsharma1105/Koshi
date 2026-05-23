# Koshi Sub-Agent Personas

Five focused personas that wrap the **Koshi MCP server**'s 24 tools into role-specific agents you can invoke from MCP-compatible clients (GitHub Copilot CLI, Claude Code, Cursor, Windsurf, Agency, …).

The same five personas ship in two formats:

| Format | Location | Used by |
|--------|----------|---------|
| Copilot CLI / Agency | [`.github/copilot/agents/*.agent.md`](./.github/copilot/agents/) | GitHub Copilot CLI, Microsoft Agency CLI |
| Claude Code | [`.claude/agents/*.md`](./.claude/agents/) | Claude Code, Claude Desktop |

## Personas

| Persona | Pillar | Tools | When to use |
|---------|--------|-------|-------------|
| `koshi-librarian` | Retrieval | 5 + diagnostics | Index code/docs and search them. |
| `koshi-memory-keeper` | Memory | 9 + diagnostics | Store / recall facts, decisions, patterns, preferences; vault export/import/sync; turn-end auto-capture. |
| `koshi-context-packer` | Context | 3 + read-only retrieval/recall | Plan token budgets and pack prompt windows for cache reuse. |
| `koshi-quality-coach` | Quality | 5 + diagnostics | Score AI interactions per team, surface trends, recommend tuning. |
| `koshi-orchestrator` | All | All 24 | Generalist that routes cross-pillar requests. |

Each persona enforces single-responsibility boundaries — for example, `koshi-librarian` will refuse to call memory or team tools and will hand off to the right persona instead.

---

## Prerequisites

Pick one — every flavor talks to the same MCP server.

### Python (`pip install koshi`)

```bash
pip install koshi
```

That's it. The first `Client()` call auto-downloads a matching `koshi-mcp`
native AOT binary into your user cache, verifying it against the SHA-256
baked into the wheel. No .NET install required.

```python
from koshi import Client
with Client() as koshi:
    print(koshi.version())
```

The personas below are *informational* in Python — they describe how to scope
the 24 tools into role-specific prompts when you wire Koshi into Claude Code /
Copilot CLI from a Python-only host. The agent-installer below is .NET-only.

### .NET tool (`dotnet tool install`)

1. Install the Koshi MCP server:
   ```bash
   dotnet tool install --global Koshi.Mcp
   ```
2. Configure it in your MCP client (see [`src/Koshi.Mcp/README.md`](./src/Koshi.Mcp/README.md) for Copilot CLI / Claude / Cursor / Windsurf snippets). The personas assume the server is registered under the name **`koshi`** — this matches the `mcp__koshi__*` tool-name prefix used in the Claude Code persona allow-lists.

### Native AOT binary (`koshi-mcp-<rid>`)

Download the matching binary from any [GitHub release](https://github.com/jsharma1105/Koshi/releases) for your `linux-x64`/`linux-arm64`/`osx-arm64`/`win-x64`/`win-arm64` platform. Verify against the `.sha256` sidecar. Drop it on `PATH` (or point your client's `command` field at it). No .NET runtime needed. (Intel Mac users: install via `dotnet tool install --global Koshi.Mcp` instead.)

---

## Installing the personas

### GitHub Copilot CLI

The `*.agent.md` files are picked up automatically when you launch Copilot CLI from this repo (or any parent of it). Verify with:

```bash
copilot agents list
```

You should see all five `koshi-*` agents. Invoke any of them by name:

```bash
copilot --agent koshi-librarian "Index this project and find where auth is handled."
copilot --agent koshi-memory-keeper "Remember that we use Postgres 16 for the auth service."
copilot --agent koshi-context-packer "Pack a 16K-token context for: how does the catalog sync work?"
copilot --agent koshi-quality-coach "Score the last interaction for team 'platform-eng'."
copilot --agent koshi-orchestrator "Find auth code and remember the architectural decision."
```

To make the personas discoverable from *any* workspace, copy them globally:

```powershell
# Windows
Copy-Item .github\copilot\agents\*.agent.md "$env:USERPROFILE\.copilot\agents\" -Force
```

```bash
# macOS / Linux
mkdir -p ~/.copilot/agents
cp .github/copilot/agents/*.agent.md ~/.copilot/agents/
```

### Claude Code

The `.claude/agents/*.md` files are picked up automatically when Claude Code runs in this repo. Verify:

```bash
claude agents list
```

You should see all five `koshi-*` agents. Claude will route to them automatically based on the `description` field, or you can invoke explicitly:

```
@koshi-librarian Index this project and find where auth is handled.
@koshi-memory-keeper Remember that we use Postgres 16 for the auth service.
@koshi-context-packer Pack a 16K-token context for: how does the catalog sync work?
@koshi-quality-coach Score the last interaction for team 'platform-eng'.
@koshi-orchestrator Find auth code and remember the architectural decision.
```

To install globally:

```bash
mkdir -p ~/.claude/agents
cp .claude/agents/koshi-*.md ~/.claude/agents/
```

### Other MCP clients (Cursor, Windsurf, Agency)

The personas are plain Markdown system prompts. Copy the body of the relevant `.agent.md` file into the client's "custom mode" / "rules" / "instructions" panel. The frontmatter (description, tools allow-list) is informational for clients that don't enforce sub-agent isolation.

---

## How the personas relate to the 24 MCP tools

```
┌──────────────────────────── Koshi MCP (24 tools) ─────────────────────────────┐
│                                                                                │
│  Retrieval (5)        Memory (9)         Context (3)        Team/Qual (5)      │
│  ──────────────      ─────────────      ─────────────      ──────────────      │
│  index_directory ◄───┐  remember              compile  ◄───┐                    │
│  index           ◄───┤  recall                budget   ◄───┤                    │
│  search          ◄───┤  memory_stats          tokens   ◄───┤                    │
│  list_indexed    ◄───┤  forget                              │  register_team    │
│  clear_index     ◄───┤  clear_memories                      │  score_turn       │
│                      │  capture_turn (v0.8.0) ◄────────────┤  dashboard        │
│                      │  memory_export_to_vault              │  analyze          │
│                      │  memory_import_from_vault            │  list_teams       │
│                      │  memory_sync_vault                   │                   │
│         + diagnostics (2): health, version                  │                   │
│                      │                                ▼                         │
│                      ▼                                                          │
│   koshi-librarian    koshi-memory-keeper   koshi-context-packer  koshi-quality-coach │
│                                                                                 │
│                ┌────────────── koshi-orchestrator ──────────────┐                │
│                │             routes across all four              │                │
│                └─────────────────────────────────────────────────┘                │
└────────────────────────────────────────────────────────────────────────────────┘
```

`koshi-context-packer` has *read-only* access to retrieval and memory tools (it calls `koshi_search` / `koshi_recall` to gather material) but does not own them — it cannot index or store.

---

## Customizing

The personas live in this repo so they version with the rest of Koshi. To tweak them:

1. Edit the relevant `.agent.md` / `.md` file.
2. Restart your MCP client (Copilot CLI / Claude Code re-read agents on session start).

If you only need a slight tone shift (e.g., terser output), most clients accept a project-level override file that appends to the agent's system prompt — see your client's documentation.

---

## License

[MIT](./LICENSE) © Koshi Contributors.
