# Context, Memory & Harness Engineering — Deep Dive Guide

## What This Document Covers

This is your reference guide for understanding the three pillars of AI infrastructure engineering.
Read this alongside building Koshi — theory without practice is trivia, practice without theory is guessing.

---

## Part 1: Context Engineering

### Definition
Context Engineering is the discipline of **selecting, compressing, positioning, and budgeting**
the information that goes into an LLM's context window to maximize output quality per token spent.

### Why It Matters

```
Without Context Engineering:
  "Here's my entire 500-file codebase, please fix the bug"
  → 640K tokens (won't fit)
  → $12.80 per query (GPT-4o input pricing)
  → Model drowns in irrelevant code, misses the bug

With Context Engineering:
  "Here are the 3 most relevant files + the error trace + the ADR explaining why we use this pattern"
  → 4K tokens (fits perfectly)
  → $0.08 per query
  → Model has exactly what it needs, fixes the bug
```

### The Five Problems Context Engineering Solves

| # | Problem | Without CE | With CE |
|---|---------|-----------|---------|
| 1 | **Selection** | Include everything | Include only what's relevant to THIS query |
| 2 | **Compression** | Verbatim code dumps | Summarize boilerplate, verbatim for critical sections |
| 3 | **Positioning** | Random order | Most relevant at beginning/end (Lost in the Middle) |
| 4 | **Budgeting** | Hope it fits | Explicit token allocation per section |
| 5 | **Caching** | Every call starts fresh | Stable system prompt hits prompt cache (saves 50-90%) |

### Context Window Anatomy

```
┌─────────────────────────────────────────────────┐
│  SYSTEM PROMPT (stable, cacheable)              │ ← 10-15% of budget
│  • Role definition                              │
│  • Output format instructions                   │
│  • Behavioral constraints                       │
├─────────────────────────────────────────────────┤
│  RETRIEVED CONTEXT (dynamic, per-query)         │ ← 40-60% of budget
│  • Most relevant chunk (position 1 — highest    │
│    attention)                                   │
│  • Supporting chunks (middle — lower attention) │
│  • Second-most relevant (near end — high        │
│    attention)                                   │
├─────────────────────────────────────────────────┤
│  MEMORY (persistent, session/team knowledge)    │ ← 10-20% of budget
│  • Relevant facts from past sessions            │
│  • Team conventions and decisions               │
│  • User preferences                             │
├─────────────────────────────────────────────────┤
│  CONVERSATION HISTORY (sliding window)          │ ← 10-20% of budget
│  • Recent turns (verbatim)                      │
│  • Older turns (summarized)                     │
├─────────────────────────────────────────────────┤
│  USER QUERY (always last — high attention zone) │ ← 5% of budget
├─────────────────────────────────────────────────┤
│  RESPONSE BUFFER (reserved for output)          │ ← ~25% of total window
└─────────────────────────────────────────────────┘
```

### Key Papers & Concepts

1. **Lost in the Middle** (Liu et al., 2023)
   - LLMs recall information better from the beginning and end of context
   - Middle positions see 20-40% accuracy drop
   - Implication: Position your most important context strategically

2. **Prompt Caching** (Anthropic, OpenAI)
   - If the first N tokens of your prompt are identical across calls, the provider
     caches the KV computation and charges ~10% of normal input cost
   - Implication: Put STABLE content first (system prompt, team context),
     VARIABLE content last (retrieved chunks, user query)
   - This is a direct incentive to architect your context window carefully

3. **Needle in a Haystack** tests
   - Models can find specific facts in long contexts, but accuracy degrades
     with context length and position
   - Implication: Less context that's highly relevant > more context with noise

### Metrics to Track

| Metric | What it measures | Target |
|--------|-----------------|--------|
| Tokens per query | Cost efficiency | Minimize while maintaining quality |
| Context hit rate | % of included context actually used by the model | > 70% |
| Cache hit rate | % of prompt tokens that hit prompt cache | > 50% for stable prompts |
| First-attempt success | Did the model get it right without reprompting? | > 75% |
| Precision@K | Of top K retrieved chunks, how many were relevant? | > 0.7 |

---

## Part 2: Memory Engineering

### Definition
Memory Engineering is the discipline of **persisting, organizing, compressing, and retrieving**
knowledge across AI interactions so that the system learns and improves over time.

### The Memory Gap

```
Current AI tools (Copilot, ChatGPT, Claude):
  Session 1: "Our API uses stored procedures, not EF Core"
  Session 2: "Our API uses stored procedures, not EF Core"  ← you re-explain
  Session 3: "Our API uses stored procedures, not EF Core"  ← and again

  Cost: 3x the tokens, 3x the time, 0 learning

With Memory Engineering:
  Session 1: "Our API uses stored procedures, not EF Core"
  Session 2: (Memory auto-loaded) → Model already knows
  Session 3: (Memory auto-loaded) → Model already knows

  Cost: 1x the tokens, then near-zero overhead
```

### Memory Types (from Cognitive Science)

| Type | What it stores | Duration | AI Equivalent |
|------|---------------|----------|---------------|
| **Sensory** | Raw input | Milliseconds | Current request context |
| **Working** | Active processing | Seconds | Current context window |
| **Episodic** | Events & experiences | Long-term | Session logs, conversation history |
| **Semantic** | Facts & knowledge | Long-term | Extracted facts, team conventions |
| **Procedural** | How to do things | Long-term | Code patterns, workflows, templates |

### Memory Architecture for AI Systems

```
┌─────────────────────────────────────────────────┐
│                  Memory Tiers                   │
├─────────────┬───────────────┬───────────────────┤
│    HOT      │     WARM      │       COLD        │
│  (< 1 hour) │  (1h - 7 days)│   (> 7 days)      │
├─────────────┼───────────────┼───────────────────┤
│ Full context│ Summarized    │ Key facts only    │
│ Verbatim    │ Key decisions │ Indexed for       │
│ turns       │ + outcomes    │ retrieval         │
├─────────────┼───────────────┼───────────────────┤
│ Token cost: │ Token cost:   │ Token cost:       │
│ HIGH        │ MEDIUM        │ LOW               │
│ ~500 tokens │ ~100 tokens   │ ~20 tokens        │
│ per memory  │ per memory    │ per memory        │
└─────────────┴───────────────┴───────────────────┘
```

### The Hard Problems

1. **What to remember** — Not everything is worth storing. "The user said hello" is noise.
   Extract FACTS, DECISIONS, PATTERNS, and FAILURES.

2. **When to forget** — Memory without forgetting is hoarding. Use decay functions:
   ```
   relevance = base_relevance × e^(-λ × days_since_access)
   ```
   Where λ controls decay rate. Memories that are never accessed decay to zero.

3. **Conflict resolution** — "The API uses EF Core" (old) vs. "We migrated to Dapper" (new).
   Newer facts should supersede older ones, but not silently — log the change.

4. **Memory retrieval** — Which memories are relevant to THIS query? Same retrieval problem
   as document search, but over structured facts instead of raw text.

5. **Privacy & access control** — Personal memories vs. team memories. Who can read what?

### Key Papers & Concepts

1. **MemGPT** (Packer et al., 2023)
   - Treats context window as "RAM" and external storage as "disk"
   - LLM manages its own memory via function calls (page in/page out)
   - Key insight: The LLM itself can decide what to remember and what to evict

2. **Generative Agents** (Park et al., 2023)
   - Simulated agents with memory, retrieval, and reflection
   - Reflection: periodically summarize experiences into higher-level insights
   - Implication: Memory isn't just storage — it's also synthesis

3. **LangMem** (LangChain)
   - Open-source memory framework
   - Types: conversation buffer, summary, entity extraction, knowledge graph

### Memory Operations (Phase 2 Interface)

```csharp
interface IMemoryManager
{
    // WRITE — extract and store facts from an interaction
    Task<IReadOnlyList<Memory>> ExtractAndStoreAsync(
        string interaction, MemoryScope scope);

    // READ — retrieve relevant memories for a query
    Task<IReadOnlyList<Memory>> RecallAsync(
        string query, int maxTokenBudget);

    // COMPRESS — summarize old memories to save tokens
    Task<int> CompressAsync(TimeSpan olderThan);

    // FORGET — remove memories below relevance threshold
    Task<int> ForgetAsync(float minRelevance);

    // REFLECT — synthesize patterns from recent memories
    Task<IReadOnlyList<Memory>> ReflectAsync(int recentCount);
}
```

---

## Part 3: Harness Engineering

### Definition
Harness Engineering is the discipline of **designing, building, and operating the scaffolding**
that connects LLMs to the real world — orchestrating context, memory, tools, and workflows
into reliable, observable, cost-effective AI systems.

### The Harness Stack

```
┌────────────────────────────────────────────────────────┐
│  APPLICATION LAYER                                     │
│  (chat, code review, search, automation)               │
├────────────────────────────────────────────────────────┤
│  ORCHESTRATION LAYER (your Harness)                    │
│  Session Manager │ Context Compiler │ Memory Manager   │
│  Budget Manager  │ Cache Manager    │ Tool Router      │
│  Eval Harness    │ Fallback Strategy│ Quality Tracker  │
├────────────────────────────────────────────────────────┤
│  RETRIEVAL LAYER                                       │
│  Vector Search │ Keyword (BM25) │ External MCPs│ Memory│
├────────────────────────────────────────────────────────┤
│  MODEL LAYER (swappable)                               │
│  GPT-4o │ Claude Sonnet │ Local model backend          │
├────────────────────────────────────────────────────────┤
│  OBSERVABILITY LAYER                                   │
│  Token Tracking │ Quality Scoring │ Latency Tracing    │
└────────────────────────────────────────────────────────┘
```

### Session Lifecycle

```
1. SESSION START → Load memory, init budget, start trace
2. QUERY ARRIVES → Retrieve context, compile window, select model
3. MODEL CALL   → Send context, track tokens/latency, stream response
4. POST-CALL    → Extract facts → memory, log metrics, update feedback
5. SESSION END  → Compress memories, write summary, flush metrics
```

### Key Design Patterns

**Context Compiler Pattern**: Score each piece by relevance × recency × importance.
Greedily pack into budget (knapsack). Compress lower-priority items. Position strategically.

**Fallback Cascade Pattern**: Full context → Summarized → Key facts only → Simpler model.

**Quality Feedback Loop**: User rates output → Update retrieval weights → Better next time.

### Anti-Patterns

| Anti-Pattern | Why It Fails |
|-------------|-------------|
| Dump everything into context | Token waste, noise, "Lost in the Middle" |
| Cache nothing | Every call is expensive |
| No memory | Every session starts cold |
| Single retriever | Misses exact matches OR semantic matches |
| No quality measurement | Can't improve what you can't measure |
| Hardcoded model | Can't optimize cost vs. quality |

---

## How The Three Disciplines Connect

```
                    ┌──────────────────────┐
                    │  HARNESS ENGINEERING │
                    │  (the orchestrator)   │
                    └──────────┬───────────┘
                               │
                    ┌──────────┴───────────┐
                    │                      │
          ┌─────────▼──────────┐ ┌────────▼─────────┐
          │ CONTEXT ENGINEERING│ │ MEMORY ENGINEERING│
          │ (what to include   │ │ (what to remember │
          │  in THIS call)     │ │  across calls)    │
          └────────────────────┘ └──────────────────┘
```

- **Context Engineering** → "What should the model see RIGHT NOW?"
- **Memory Engineering** → "What should the system REMEMBER for later?"
- **Harness Engineering** → "How do we make all of this WORK reliably at scale?"

They are layers of the same system. Your Koshi project implements all three.
