# Recommended `copilot-instructions.md` snippet for Koshi

Paste this block into your repo's `.github/copilot-instructions.md` (or
`AGENTS.md`, or your team's equivalent agent steering doc) so that
Copilot, Claude, and other MCP-aware coding agents call `koshi_capture_turn`
reliably at the end of meaningful turns. Without this, decisions don't
land in the shared vault and the cross-developer "skip the regression"
loop breaks.

The snippet is intentionally short — long agent instructions get
ignored.

> **Tip:** [`templates/`](../templates/) contains pre-formatted versions
> of this guidance ready to drop into Cursor (`.cursorrules`), Windsurf
> (`.windsurfrules`), Claude Code / generic (`AGENTS.md`), and Copilot
> CLI (`.github/copilot-instructions.md`). The text below is the
> canonical source these templates are derived from.

---

## Koshi memory capture

After landing a non-trivial change, debugging a regression, or making an
architectural choice in this conversation, call `koshi_capture_turn`
with a one-paragraph summary of what was decided and *why*. Include any
linked PR number or commit SHAs as provenance.

This is what lets the next developer who pulls this repo skip the work
you just did.

Phrase decisions explicitly so the heuristic extractor catches them:

- `Decision: switch the cache layer from in-memory to Redis.`
- `We chose retry-with-backoff over circuit-breaker because the dep recovers within 5s.`
- `Fixed by upgrading the Azure SDK to 9.0.313.`

Skip the call if the turn was pure discussion, exploration, or chitchat
with no concrete decision.

---

## Optional: also call it on every meaningful save

If your team uses Koshi's vault mode (`KOSHI_MEMORY_VAULT` set) and
commits the vault alongside code, calling `koshi_capture_turn` is part
of "done" for any feature that includes a non-obvious technical choice.
Treat it like updating a CHANGELOG entry — small effort, large
downstream payoff.

## How it works

`koshi_capture_turn` runs a pattern-based extractor over the summary
you pass in. No LLM in the loop; the heuristics look for explicit
decision markers, comparative-with-rationale sentences (`X over Y
because Z`), first-person decision verbs (`we chose`, `the team
decided`), and resolution markers (`fixed by`). Questions and short
fragments are filtered out. Set `auto_promote: false` to preview
candidates before persisting.

See [`tests/Koshi.Core.Tests/DecisionExtractorTests.cs`](../tests/Koshi.Core.Tests/DecisionExtractorTests.cs)
for the full pattern spec.
