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

## How to install (automatic, with `koshi-mcp init`)

As of #77 Layer 4 this is folded into the wizard: running
`koshi-mcp init` in your project drops every template into its
canonical path in one pass — `AGENTS.md`,
`.github/copilot-instructions.md`, `.cursorrules`, and
`.windsurfrules`. Project-level, not per-client, because the same
checkout often gets opened by multiple AI clients in parallel.

The installer is **safe by default**:

- **Missing file** → writes the full template.
- **Existing file *without* the Koshi marker** → appends the template
  block with a blank-line gap so your existing rules survive verbatim.
- **Existing file *with* the Koshi marker** → no-op; the install is
  idempotent.
- `--force-templates` overwrites instead of appending.
- `--skip-templates` opts out entirely.

The marker the installer looks for is the literal string
`koshi-mcp:steering-template:v1`, embedded in every template (the `:v1`
suffix lets future Koshi versions detect-and-upgrade instead of blindly
re-appending).

## Auto-install on every `git clone` (opt-in, per-machine — #78 Gap C)

You can also tell git itself to drop these files into every freshly
cloned repository. Run **once per machine**:

```bash
koshi-mcp init --register-git-template
```

What it does:

1. Writes `~/.git-template-koshi/` containing a copy of the four
   steering files plus a `hooks/post-checkout` POSIX shell hook.
2. Runs `git config --global init.templatedir ~/.git-template-koshi` so
   every future `git clone` seeds `.git/` from that directory.
3. The hook fires the *first* time HEAD is checked out after the clone
   and copies any of the four steering files into the working tree
   **only if they are not already present** — so a repo that already
   ships its own `.cursorrules` keeps it.

Honest limits — surfaced via `koshi-mcp init --help` too:

- **`git init` does not fire the hook.** Git has no `init` hook and
  `init.templatedir` only seeds `.git/`, not the working tree. For
  fresh projects, just run `koshi-mcp init` from inside the repo.
- **Bare clones and `git clone --no-checkout`** never run
  `post-checkout` either.
- **`git worktree add` also fires the hook.** This is intentional — a
  linked worktree of an existing project deserves the same steering
  surface as a fresh clone of the same project. Files already present
  in the worktree (because they live in the parent repo) are skipped.
- **Submodules are skipped on purpose** — the parent repo already owns
  the steering layout.
- **A global `core.hooksPath`** (some monorepo / corporate setups)
  overrides per-repo hooks; if you've set that, our hook never fires.
  The installer detects this and prints a warning.
- **Symlinked destinations are refused for safety.** If the cloned
  working tree contains `.github -> ..` or `AGENTS.md -> /etc/passwd`
  (dangling), the hook skips that file rather than write through the
  symlink.
- **Existing `init.templatedir`** is treated as a conflict and we
  refuse to overwrite it unless you also pass `--force-git-template`.

Why opt-in: it mutates your global gitconfig and writes to your home
directory. That's a per-machine choice, not a per-project one. The
default `koshi-mcp init` is and stays a no-op on `~/.gitconfig`.

To uninstall manually:

```bash
git config --global --unset init.templatedir
rm -rf ~/.git-template-koshi
```

## How to install (manual, fallback)

If you'd rather not run the wizard, use the commands in the previous
section to paste the relevant template into each rule file by hand.

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
