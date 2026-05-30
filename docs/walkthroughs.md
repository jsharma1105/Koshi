# Usage walkthroughs

Five worked examples — paste the natural-language prompts into your MCP
client and let your agent call the right Koshi tool.

| # | Walkthrough |
|---|---|
| 1 | [Index a project and search it](#1-index-a-project-and-search-it) |
| 2 | [Plan a context budget before calling an LLM](#2-plan-a-context-budget-before-calling-an-llm) |
| 3 | [Remember decisions across sessions](#3-remember-decisions-across-sessions) |
| 4 | [Share memories across the team via a Git-backed vault](#4-share-memories-across-the-team-via-a-git-backed-vault) |
| 5 | [Auto-capture decisions at turn end](#5-auto-capture-decisions-at-turn-end) |
| 6 | [Track team quality](#6-track-team-quality) |

---

## 1. Index a project and search it

In your MCP client, just ask:

> _"Index this project and find the authentication code."_

The agent calls:

```
koshi_index_directory(path="/path/to/project")  # or use KOSHI_INDEX_PATH
koshi_search(query="authentication", topK=5)
```

You get the top 5 matching chunks with file paths, BM25 scores, and
content previews.

---

## 2. Plan a context budget before calling an LLM

> _"Plan a 16K-token budget for a system prompt of 800 tokens and a
> 1,200-token team context."_

```
koshi_budget_plan(totalBudget=16384, systemPrompt="...", teamContext="...")
```

Output explains fixed costs, remaining budget, suggested splits, and
cache-prefix savings.

---

## 3. Remember decisions across sessions

> _"Remember that we settled on Postgres for the auth service
> (Decision, source: arch-meeting-2026-04)."_

```
koshi_remember(
  type="Decision",
  subject="auth-service-database",
  content="Use Postgres 16 with row-level security for the auth service.",
  source="arch-meeting-2026-04",
  confidence=0.95
)
```

Later, in any session:

> _"What database did we decide on for auth?"_

```
koshi_recall(query="auth database", type="Decision")
```

---

## 4. Share memories across the team via a Git-backed vault

Want your teammates to inherit the same memories you've stored? Point
Koshi at a directory and commit it to Git:

```bash
mkdir -p ~/teams/our-memories
export KOSHI_MEMORY_VAULT=~/teams/our-memories

# (optional) check it into a shared repo
cd ~/teams/our-memories
git init && git add koshi/ && git commit -m "seed memories"
```

Every `koshi_remember` from now on writes one Markdown file:

```
~/teams/our-memories/
└── koshi/
    └── decisions/
        └── auth-service-database--mem-000001.md
```

…with human-readable YAML frontmatter:

```yaml
---
koshi:
  id: mem-000001
  type: Decision
  scope: { user: "*", workspace: default, thread: null }
  source: arch-meeting-2026-04
  confidence: 0.95
  # ...
---
# auth-service-database

Use Postgres 16 with row-level security for the auth service.
```

External edits, deletes, and `git pull`s are picked up on the next tool
call without a server restart. Hand-written notes that lack a `koshi.id`
in their frontmatter are reported as "unmanaged notes" in
`koshi_memory_stats` but never overwritten. See
[vault mode](vault-mode.md) for the full format spec, migration guide,
and FAQ.

---

## 5. Auto-capture decisions at turn end

The hardest memory to keep is the one no one remembered to write down.
`koshi_capture_turn` is the tool the agent calls at the end of any
non-trivial turn so the *reasoning* behind the change is durable before
anyone closes the tab.

> _"We just landed a fix — capture the decision."_

```jsonc
koshi_capture_turn(
  summary: "Decision: switch the cache layer from in-memory to Redis
            because the per-replica cache lost coherency under load.
            Fixed by upgrading StackExchange.Redis to 2.8.x.",
  linked_pr: 123,
  linked_commits: ["a1b2c3d", "e4f5g6h"],
  auto_promote: true        // false = preview candidates without saving
)
```

What happens:

1. A deterministic pattern extractor (no LLM, no network) pulls
   decision-shape sentences out of the summary: explicit `Decision: …`
   markers, `X over Y because Z` comparisons, `we chose / decided /
   picked / went with`, and `fixed by / resolved by` resolution markers.
2. Questions, negations, hypotheticals, and short fragments are
   filtered out so chitchat never lands in the store.
3. Each surviving sentence becomes a `Decision` memory under the active
   backend (JSON file or vault) with a provenance footer carrying the
   PR number, commit SHAs, and capture timestamp.
4. Subject-exact-match dedupe within scope means two agents capturing
   the same decision in the same workspace produce one memory, not two.

Pair this with `KOSHI_MEMORY_VAULT` and Git, and the next developer (or
the next agent session) recalls the decision the instant they ask about
the cache layer — they don't get to repeat the incident.

To make agents call it reliably, paste the 30-line snippet at
[`docs/copilot-instructions-snippet.md`](copilot-instructions-snippet.md)
into your repo's `.github/copilot-instructions.md` (or your team's
equivalent agent-steering doc). Without that hint, agents tend to forget
to call the tool; with it, capture-on-turn-end becomes part of "done".

Full mechanics: [auto-capture](auto-capture.md).

---

## 6. Track team quality

```
koshi_register_team(teamId="platform-eng", name="Platform Engineering",
                    tokenBudget=16384, qualityTarget=0.75)

# After each AI interaction:
koshi_score_turn(teamId="platform-eng",
                 retrievedChunks=5, budgetUtilization=0.78,
                 cacheRatio=0.62, latencyMs=2400, userRating=4)

# Periodically:
koshi_team_dashboard(teamId="platform-eng")
koshi_analyze_feedback(teamId="platform-eng")
```

The feedback loop surfaces concrete config tweaks (e.g. _"increase topK
from 5 → 7, weakest dimension is retrieval"_).

## Next steps

- [Tools](tools.md) — full reference for every tool used above.
- [Configuration](configuration.md) — env vars that change tool behaviour.
- [Troubleshooting](troubleshooting.md) — fixes for the most common errors.
