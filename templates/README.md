# Koshi steering templates (`#77` Layer 3)

These are the per-ecosystem agent-steering files Koshi ships so the agent
*reliably* calls `koshi_capture_turn` (and the recall / search tools) at
the right moments, **without** every user having to handcraft the
guidance from scratch in every project.

Layer 1 (tool `[Description]`s) and Layer 2 (MCP prompts) of #77 already
work for any MCP-aware client. These Layer 3 files are the
ecosystem-native conveniences that pull the steering into each client's
own auto-loaded config file, so users get the behaviour the moment they
land in the project — no copy-paste from the docs.

| Client | Auto-loaded path in your project | Template file |
|---|---|---|
| Claude Code | `AGENTS.md` or `.claude/CLAUDE.md` | [`koshi.AGENTS.md`](./koshi.AGENTS.md) |
| Claude Desktop | (system prompt — paste manually) | [`koshi.AGENTS.md`](./koshi.AGENTS.md) |
| Copilot CLI | `.github/copilot-instructions.md` | [`koshi.copilot-instructions.md`](./koshi.copilot-instructions.md) |
| Cursor | `.cursorrules` | [`koshi.cursorrules`](./koshi.cursorrules) |
| Windsurf | `.windsurfrules` | [`koshi.windsurfrules`](./koshi.windsurfrules) |
| Anything else | `AGENTS.md` | [`koshi.AGENTS.md`](./koshi.AGENTS.md) |

## How to install (manual, today)

Pick the template that matches your client. Append (don't replace — most
of these files are shared with other rule sets) into the right project
file:

```bash
# Cursor
cat templates/koshi.cursorrules >> .cursorrules

# Windsurf
cat templates/koshi.windsurfrules >> .windsurfrules

# Claude Code / generic
cat templates/koshi.AGENTS.md >> AGENTS.md

# Copilot CLI
mkdir -p .github
cat templates/koshi.copilot-instructions.md >> .github/copilot-instructions.md
```

```powershell
# Same on Windows PowerShell
Get-Content templates\koshi.cursorrules            | Add-Content .cursorrules
Get-Content templates\koshi.windsurfrules          | Add-Content .windsurfrules
Get-Content templates\koshi.AGENTS.md              | Add-Content AGENTS.md
New-Item -ItemType Directory -Force .github | Out-Null
Get-Content templates\koshi.copilot-instructions.md | Add-Content .github\copilot-instructions.md
```

## How to install (automatic, next)

Layer 4 (`#77` Layer 4, tracked as `issue-77-layer4-init-autoinstall`)
folds this into the `koshi-mcp init` wizard (#68): for each detected
client, the wizard auto-appends the matching template at install time
and prints a one-line confirmation. Until that lands, the table above is
the install matrix.

## What the templates contain

Every template is a thin per-ecosystem wrapper around the same core
guidance from
[`docs/copilot-instructions-snippet.md`](../docs/copilot-instructions-snippet.md):

1. **Call `koshi_recall` and `koshi_search` *before* answering** any
   question that might benefit from prior decisions or indexed code.
2. **Call `koshi_capture_turn` *after* any turn that produced a
   concrete decision, bug fix, or architectural choice** — phrased so
   the deterministic extractor catches it.
3. **Skip the capture call** on pure exploration / chitchat / Q&A
   without a decision.

The templates intentionally stay short — long agent rules get ignored
or aged out of context. Anything more elaborate than these ~30 lines
belongs in tool `[Description]`s (which every client surfaces to the
model automatically) instead of in per-project rule files.

## Updating

When `koshi_capture_turn`'s contract changes, update
`docs/copilot-instructions-snippet.md` first, then propagate the
relevant phrases into each template here. The Copilot CLI
`koshi-orchestrator` persona (`.github/copilot/agents/koshi-orchestrator.agent.md`)
also carries a one-line restatement and should be updated in lockstep.
