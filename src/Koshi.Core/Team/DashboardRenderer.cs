namespace Koshi.Core.Team;

using Koshi.Core.Context;
using Koshi.Core.Harness;

/// <summary>
/// Renders team quality dashboards to the console.
/// Shows metrics, trends, recommendations, and comparisons.
/// </summary>
public static class DashboardRenderer
{
    /// <summary>
    /// Render a team dashboard as formatted text lines.
    /// Returns lines suitable for console output (no Spectre dependency in Core).
    /// </summary>
    public static IReadOnlyList<string> Render(TeamDashboard dashboard)
    {
        var lines = new List<string>
        {
            $"╔══════════════════════════════════════════════════════════╗",
            $"║  Team Dashboard: {dashboard.TeamName,-40}║",
            $"╠══════════════════════════════════════════════════════════╣",
            $"║  Quality Score:   {dashboard.AvgQualityScore,6:F2}  (target: {dashboard.QualityTarget:F2})         ║",
            $"║  Target Hit Rate: {dashboard.TargetHitRate,6:P0}                               ║",
            $"║  User Rating:     {FormatRating(dashboard.AvgUserRating),-6}  ({dashboard.FeedbackCount} reviews)          ║",
            $"╠══════════════════════════════════════════════════════════╣",
            $"║  Turns:           {dashboard.TotalTurns,6}                               ║",
            $"║  Tokens Used:     {dashboard.TotalTokensConsumed,6:N0}                               ║",
            $"║  Avg Latency:     {dashboard.AvgLatencyMs,6:F0}ms                             ║",
            $"║  Cache Hit Rate:  {dashboard.AvgCacheHitRate,6:P0}                               ║",
            $"║  Budget Util:     {dashboard.AvgBudgetUtilization,6:P0}                               ║",
            $"║  Fallbacks:       {dashboard.FallbackCount,6}                               ║",
            $"║  Facts Extracted: {dashboard.TotalFactsExtracted,6}                               ║",
        };

        // Quality trend
        if (dashboard.QualityTrend.Count > 0)
        {
            lines.Add($"╠══════════════════════════════════════════════════════════╣");
            lines.Add($"║  Quality Trend:                                          ║");
            foreach (var (label, score) in dashboard.QualityTrend)
            {
                int barLen = (int)(score * 30);
                string bar = new string('█', barLen) + new string('░', 30 - barLen);
                lines.Add($"║    {label,-10} {bar} {score:F2}  ║");
            }
        }

        // Recommendations
        if (dashboard.Recommendations.Count > 0)
        {
            lines.Add($"╠══════════════════════════════════════════════════════════╣");
            lines.Add($"║  Recommendations:                                        ║");
            foreach (var rec in dashboard.Recommendations)
            {
                // Wrap long recommendations
                if (rec.Length <= 54)
                {
                    lines.Add($"║    {rec,-54}║");
                }
                else
                {
                    lines.Add($"║    {rec[..54]}║");
                    lines.Add($"║    {rec[54..].PadRight(54)}║");
                }
            }
        }

        lines.Add($"╚══════════════════════════════════════════════════════════╝");
        return lines;
    }

    /// <summary>
    /// Render a comparison between two teams.
    /// </summary>
    public static IReadOnlyList<string> RenderComparison(TeamDashboard team1, TeamDashboard team2)
    {
        var lines = new List<string>
        {
            $"┌─────────────────────────┬──────────────┬──────────────┐",
            $"│ Metric                  │ {team1.TeamName,-12} │ {team2.TeamName,-12} │",
            $"├─────────────────────────┼──────────────┼──────────────┤",
            $"│ Quality Score           │ {team1.AvgQualityScore,12:F2} │ {team2.AvgQualityScore,12:F2} │",
            $"│ Target Hit Rate         │ {team1.TargetHitRate,11:P0} │ {team2.TargetHitRate,11:P0} │",
            $"│ User Rating             │ {FormatRating(team1.AvgUserRating),12} │ {FormatRating(team2.AvgUserRating),12} │",
            $"│ Total Turns             │ {team1.TotalTurns,12} │ {team2.TotalTurns,12} │",
            $"│ Avg Latency (ms)        │ {team1.AvgLatencyMs,12:F0} │ {team2.AvgLatencyMs,12:F0} │",
            $"│ Cache Hit Rate          │ {team1.AvgCacheHitRate,11:P0} │ {team2.AvgCacheHitRate,11:P0} │",
            $"│ Budget Utilization      │ {team1.AvgBudgetUtilization,11:P0} │ {team2.AvgBudgetUtilization,11:P0} │",
            $"│ Fallbacks               │ {team1.FallbackCount,12} │ {team2.FallbackCount,12} │",
            $"└─────────────────────────┴──────────────┴──────────────┘",
        };

        // Winner callout
        if (team1.AvgQualityScore > team2.AvgQualityScore + 0.05f)
            lines.Add($"  → {team1.TeamName} leads by {team1.AvgQualityScore - team2.AvgQualityScore:F2} in quality score");
        else if (team2.AvgQualityScore > team1.AvgQualityScore + 0.05f)
            lines.Add($"  → {team2.TeamName} leads by {team2.AvgQualityScore - team1.AvgQualityScore:F2} in quality score");
        else
            lines.Add($"  → Teams are closely matched in quality");

        return lines;
    }

    private static string FormatRating(float rating)
    {
        if (rating <= 0) return "N/A";
        int stars = (int)Math.Round(rating);
        return new string('★', stars) + new string('☆', 5 - stars) + $" ({rating:F1})";
    }
}
