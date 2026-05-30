# Auto-capture decisions, share them across platforms

> The single biggest reason teammates repeat the same regression is that
> the *reasoning* behind a fix never leaves the original PR description.

Koshi's **`koshi_capture_turn`** tool (added in v0.8.0) closes that loop —
the agent itself stores the decision the moment it is made, and Git
distributes it to every other developer on every client.

## 1. The agent stores memory by itself

At the end of any non-trivial turn (a regression fix, an architectural
choice, a tricky workaround), the agent calls **one** tool:

```jsonc
koshi_capture_turn(
  summary: "Decision: switch the cache layer from in-memory to Redis because
            the in-process cache lost coherency across the 3 API replicas
            during the 2026-05-21 incident.",
  linked_pr: 123,
  linked_commits: ["a1b2c3d"]
)
```

Koshi runs a deterministic, pattern-based extractor over the summary
(no LLM in the loop — AOT-friendly, no network, no hidden cost), picks
out the decision-shape sentences, and persists each one as a `Decision`
memory with a provenance footer (`PR #123`, `commits a1b2c3d`, capture
timestamp). Questions, chitchat, negations, and hypothetical sentences
are filtered out so the store stays clean.

After running `koshi-mcp init`, the steering snippet is installed
automatically into each detected client's standard rules file
(`.github/copilot-instructions.md` for Copilot CLI, `.cursorrules` for
Cursor, etc.) so any MCP-aware coding agent calls `koshi_capture_turn`
reliably at the end of meaningful turns. Per-client templates also live
in [`templates/`](../templates/) if you prefer to install them by hand.

## 2. Share across every platform from one binary

Koshi is a single MCP server with **24 tools** — the *same* wire format
and on-disk format whether you reach it via `pip`, `dotnet tool`, or a
raw AOT binary. Configure it once per client; the memories your agent
captures are visible to all of them.

| Platform | Config file |
|---|---|
| **GitHub Copilot CLI** | `~/.copilot/mcp-config.json` |
| **Claude Code / Desktop** | `claude_desktop_config.json` / `.claude/settings.json` |
| **Cursor / Windsurf** | `.cursor/mcp.json` / `.windsurf/mcp.json` |
| **Microsoft Agency CLI** | `plugin.json` (shipped with this repo) |
| **Python host** | none — `from koshi import Client` |

`koshi-mcp init` writes the correct JSON into each detected client's
config. Full per-client snippets live in
[`docs/client-setup.md`](../docs/client-setup.md)
for manual setup.

## 3. Distribute the memories with Git, not Slack

Point `KOSHI_MEMORY_VAULT` at a directory and Koshi writes **one Markdown
file per memory** under a flavor-specific layout (Obsidian / Foam /
Logseq / Dendron — pick yours with `KOSHI_VAULT_FLAVOR`). Commit the
directory and your teammates inherit the same captured decisions on
`git pull`:

```bash
export KOSHI_MEMORY_VAULT=./team-memories
# the agent captures a decision...
git add team-memories/ && git commit -m "chore(memory): cache-layer decision"
git push
```

A teammate who clones the repo and sets the same env var sees the memory
the next time they ask the agent _"why is the cache layer Redis here?"_
— no Slack archaeology, no rerunning the regression to learn the answer.
External edits, deletes, and `git pull`s are picked up automatically via
a filesystem watcher; see [`docs/vault-mode.md`](./vault-mode.md) for the
full format spec and migration guide.

The `.koshi-team.yml` convention takes this one step further: the first
teammate writes the file once and commits it, and every new teammate's
`koshi-mcp init` reads it to clone the team vault, set
`KOSHI_MEMORY_VAULT` in every detected client's config, and register the
team — fully wired in one command, zero copy-paste.

## Why this beats "just write it in the PR description"

| Problem with PR-description-only memory | How `koshi_capture_turn` + vault fixes it |
|---|---|
| Future devs don't read every old PR | The next agent session **recalls** the decision automatically on a relevant query |
| Slack threads expire / are siloed | One Markdown file per decision, in Git, searchable forever |
| Each client re-implements memory | One MCP server, every client (Copilot CLI, Claude, Cursor, Windsurf, Agency, Python) reads the same store |
| Easy to forget to write it down | The agent does it at turn-end as part of "done" |
