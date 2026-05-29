using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Prompts;

/// <summary>
/// MCP prompts that ship cross-client steering with Koshi instead of
/// asking users to paste a per-client snippet (issue #77, Layer 2).
///
/// <para>
/// Every MCP-compliant client surfaces server-supplied prompts in its
/// native UX — slash menu (Claude Desktop), command palette
/// (Cursor / Windsurf), <c>@</c>-mentions (Copilot CLI). Each prompt
/// below returns a complete steering block the agent can adopt for the
/// session.
/// </para>
///
/// <para>
/// AOT note: registration in <c>Program.cs</c> uses
/// <c>.WithPrompts&lt;SteeringPrompts&gt;()</c> (analogous to
/// <c>.WithTools&lt;T&gt;()</c>) so the trimmer keeps this type's public
/// static methods. The methods themselves are deterministic constant
/// returns — no I/O, no static-ctor side effects, safe under Native AOT.
/// </para>
/// </summary>
[McpServerPromptType]
public sealed class SteeringPrompts
{
    [McpServerPrompt(Name = "koshi/capture-turn-guide"), Description(
        "Tells the agent WHEN to call koshi_capture_turn at the end of a " +
        "turn so decisions land in the shared memory vault.")]
    public static string CaptureTurnGuide() =>
        """
        # Koshi memory capture

        WHEN TO CALL `koshi_capture_turn`:
        After landing a non-trivial change, debugging a regression, or
        making an architectural choice in this conversation. Pass a
        one-paragraph summary of what was decided and *why*. Include any
        linked PR number or commit SHAs as provenance.

        HOW TO PHRASE THE SUMMARY:
        The deterministic extractor looks for explicit decision markers.
        Sentences that match these shapes land as Decision memories:
          - "Decision: switch the cache layer from in-memory to Redis."
          - "We chose retry-with-backoff over circuit-breaker because the
             dep recovers within 5s."
          - "Fixed by upgrading the Azure SDK to 9.0.313."

        WHEN TO SKIP:
        Pure discussion, exploration, or chitchat with no concrete decision.

        WHY IT MATTERS:
        This is what lets the next developer who pulls this repo skip the
        work you just did. Without it, decisions don't land in the shared
        vault and the cross-developer "skip the regression" loop breaks.
        """;

    [McpServerPrompt(Name = "koshi/recall-before-answer"), Description(
        "Tells the agent to call koshi_recall and koshi_search BEFORE " +
        "answering questions that could be informed by prior decisions or " +
        "code in this workspace.")]
    public static string RecallBeforeAnswer() =>
        """
        # Recall before answering

        WHEN TO CALL `koshi_recall` / `koshi_search`:
        Before answering any question that might benefit from prior team
        decisions, code patterns, or facts. Both calls are cheap and
        always preferable to guessing.

        WHICH TOOL TO USE:
          - Factual / decision / preference questions → `koshi_recall query="<topic>"`
          - "Where is X in the code" / "how does X work" → `koshi_search query="<keywords>"`
          - Question spans intent + code → call both.

        WHEN TO SKIP:
        Pure chitchat or questions whose entire answer is in the user's
        current message (e.g. "summarise what I just pasted").

        WHY IT MATTERS:
        Recalled context lands at the head of the prompt window and
        becomes cacheable system content. Downstream LLM cost drops
        50-90% on cache hits. Guessing wastes a turn and risks
        contradicting a decision the team already made.
        """;

    [McpServerPrompt(Name = "koshi/context-pack-discipline"), Description(
        "Tells the agent to assemble prompt windows with " +
        "koshi_compile_context (token-budget aware, cache-friendly) " +
        "instead of stuffing raw retrieval into the message body.")]
    public static string ContextPackDiscipline() =>
        """
        # Context-packing discipline

        WHEN TO CALL `koshi_compile_context`:
        Whenever you need to build a prompt window for a downstream LLM
        call. Prefer it over stuffing raw retrieval results into the
        message body.

        SUGGESTED FLOW:
          1. Gather candidates: `koshi_search` (code/docs) + `koshi_recall` (memories).
          2. Inspect the plan: `koshi_budget_plan totalBudget=N` to see
             the split across system / team / retrieval / memory / history
             layers and the history reserve.
          3. Assemble — REQUIRED params: `systemPrompt`, `userQuery`.
             OPTIONAL params: `retrievedContent` (chunks joined by lines
             of `---`), `memories` (JSON array, or raw koshi_recall
             output — auto-parsed), `teamContext`, `tokenBudget`
             (default 8192), `strategy` (CacheOptimized | PrimacyRecency
             | RelevanceDescending | Chronological).

        EXAMPLE:
            koshi_compile_context(
                systemPrompt    = "You are a helpful coding assistant.",
                userQuery       = "<what the user just asked>",
                retrievedContent = "<chunk 1>\n---\n<chunk 2>",
                memories        = "<raw koshi_recall output OR JSON array>",
                tokenBudget     = 8192,
                strategy        = "CacheOptimized")

        WHY IT MATTERS:
          - Token-budget aware: drops oldest history first, then
            lowest-scoring retrieval, never silently truncates the system
            prompt.
          - Cache-friendly: stable system + team blocks at the head of
            the prompt window maximise downstream prompt-cache hits.
        """;

    [McpServerPrompt(Name = "koshi/score-every-turn"), Description(
        "Tells the agent to call koshi_score_turn after responding so " +
        "per-team telemetry, dashboards, and tuning recommendations " +
        "have data to operate on.")]
    public static string ScoreEveryTurn() =>
        """
        # Score every turn

        WHEN TO CALL `koshi_score_turn`:
        After responding to the user — if a team is registered for this
        workspace — log a composite quality score for the just-completed
        turn. Cheap, call once per meaningful response.

        TYPICAL CALL:
            koshi_score_turn(
                teamId            = "<team>",                         # required
                retrievedChunks   = <how many chunks you used>,
                memoriesRecalled  = <how many memories you used>,
                budgetUtilization = <0..1, tokens used / total budget>,
                cacheRatio        = <0..1, cached input tokens / total input tokens>,
                latencyMs         = <round-trip ms for the user-visible response>,
                userRating        = <1..5, 0 if no rating yet>,
                issues            = "<comma-separated subset of irrelevant,incomplete,hallucinated,verbose,terse,format,outdated,slow OR omit>",
                tokensUsed        = <total input tokens for the turn; 0 = derive from budgetUtilization x team budget>)

        WHEN TO SKIP:
          - No team is registered (check via `koshi_list_teams`).
          - Pure chitchat with no retrieval / memory / generation work.

        WHY IT MATTERS:
        The team's score history feeds `koshi_team_dashboard` and
        `koshi_analyze_feedback`. Without per-turn scores, tuning
        recommendations cannot fire and the team cannot objectively
        answer "is our context engineering getting better over time?".
        """;
}
