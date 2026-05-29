# Koshi memory + retrieval discipline

This project uses the [Koshi MCP server](https://github.com/jsharma1105/Koshi)
for shared memory and BM25 retrieval across the team. To make that
actually pay off, follow these three rules in every conversation.

## Before answering

Call **`koshi_recall`** and **`koshi_search`** before answering any
question that might benefit from prior decisions or indexed code in
this repo. They're cheap and almost always preferable to guessing. If
either returns relevant hits, cite them in your answer.

## After landing a decision

After landing a non-trivial change, debugging a regression, or making
an architectural choice in this conversation, call
**`koshi_capture_turn`** with a one-paragraph summary of what was
decided and *why*. Include any linked PR number or commit SHAs.

Phrase decisions explicitly so the heuristic extractor catches them:

- `Decision: switch the cache layer from in-memory to Redis.`
- `We chose retry-with-backoff over circuit-breaker because the dep recovers within 5s.`
- `Fixed by upgrading the Azure SDK to 9.0.313.`

**Skip** the capture call if the turn was pure discussion, exploration,
or chitchat with no concrete decision.

## When packing context

For long-running threads, use **`koshi_compile_context`** with an
explicit token budget (typical: 16000) so the prompt window stays
predictable and cacheable. Combine retrieval hits and recalled
memories in the same compile call.
