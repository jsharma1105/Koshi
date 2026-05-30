# Koshi

> A local-first MCP server that gives any AI coding agent durable memory,
> project-aware retrieval, and team-shared decisions — without cloud, API
> keys, or per-chat steering prompts.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Build](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml/badge.svg)](https://github.com/jsharma1105/Koshi/actions/workflows/build.yml)

## What you get

- **One install command** drops a verified native binary on your PATH;
  newer releases also auto-run the setup wizard, older releases print the
  one manual command to wire your clients.
- **Your agent remembers decisions across sessions** because the
  capture-turn skill is installed automatically into every supported client.
- **Your teammates inherit those decisions via `git pull`** when you commit
  the shared memory directory.

## Install

```sh
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.sh | sh

# Windows (PowerShell)
irm https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.ps1 | iex
```

Full installer flags, pip / dotnet-tool / manual paths, and release-pinning
options: [`docs/install.md`](docs/install.md).

## First worked example

Capture a decision in one client …

```
you  ▸ We just switched the cache from in-process to Redis because the
       in-process layer lost coherency across the 3 API replicas during
       the 2026-05-21 incident. Capture that as a decision (PR #123).
agent ▸ Captured. Stored 1 Decision memory linked to PR #123, visible to
       every MCP client pointed at this project.
```

… commit your shared memory directory and push. A week later, on a
different machine in a different MCP client:

```
you  ▸ Why is the cache layer Redis here?
agent ▸ Per a captured decision (PR #123, 2026-05-21): the in-process
       cache lost coherency across the 3 API replicas during an incident.
       Source: shared Koshi memory for this project.
```

Full mechanics: [`docs/auto-capture.md`](docs/auto-capture.md).

## Works with

[![Copilot CLI](https://img.shields.io/badge/GitHub-Copilot_CLI-24292e?logo=github)](docs/client-setup.md)
[![Claude](https://img.shields.io/badge/Claude-Code_&_Desktop-d97757?logo=anthropic)](docs/client-setup.md)
[![Cursor](https://img.shields.io/badge/Cursor-000000?logo=cursor&logoColor=white)](docs/client-setup.md)
[![Windsurf](https://img.shields.io/badge/Windsurf-09b6a2)](docs/client-setup.md)
[![Agency](https://img.shields.io/badge/Microsoft-Agency_CLI-0078D4?logo=microsoft)](docs/client-setup.md)
[![PyPI](https://img.shields.io/badge/Python-pip_install_koshi-3776AB?logo=python&logoColor=white)](python/README.md)

One server, one on-disk format. Memories captured in one client are
visible to every other client pointed at the same project or vault.

## What's automatic vs. what's manual

| Action | Automatic after install | Notes |
|---|---|---|
| MCP server binary on PATH | yes | shell installer; SHA-256 verified |
| MCP config entry written | Claude Code / Copilot CLI | Cursor / Windsurf / Agency: see [client setup](docs/client-setup.md) |
| Sub-agent personas installed | Claude Code / Copilot CLI | same scope as config above |
| Steering snippets dropped in project | yes, when detected | `AGENTS.md` always; `.cursorrules` / `.windsurfrules` / `.github/copilot-instructions.md` when their marker is present |
| Decision-capture prompt installed | yes | agents are instructed to call `koshi_capture_turn` after non-trivial decisions, not every reply |
| Project indexed for retrieval | **manual** | ask your agent to call `koshi_index_directory`, or set `KOSHI_INDEX_WATCH=on` |
| Team vault wiring | from `.koshi-team.yml` if present | otherwise the wizard prompts you |
| Memory shared with teammates | `git add` + `git push` the vault | one-time choice per team |
| MCP client restart | **manual** | once after install |

If a row above doesn't match your install, please [file an issue](https://github.com/jsharma1105/Koshi/issues/new).

## Five sub-agent personas

- **`koshi-librarian`** — indexes code/docs and searches them.
- **`koshi-memory-keeper`** — stores and recalls facts, decisions, and patterns; manages the vault.
- **`koshi-context-packer`** — plans token budgets and packs prompt windows.
- **`koshi-quality-coach`** — scores AI interactions per team and surfaces trends.
- **`koshi-orchestrator`** — generalist that routes cross-pillar requests.

Each persona has a single-responsibility scope. Details: [`AGENTS.md`](AGENTS.md).

## Known issues

Hitting something unexpected? Search [open bugs](https://github.com/jsharma1105/Koshi/issues?q=is%3Aissue+is%3Aopen+label%3Abug)
before assuming it's you.

## Next steps

After install, try:

```sh
koshi-agents doctor   # verify every detected client sees the server
```

Then ask your agent any project question; it will recall what it knows
and capture new decisions as it goes.

---

[Full docs](docs/) · [Contributing](CONTRIBUTING.md) · [License (MIT)](LICENSE)

