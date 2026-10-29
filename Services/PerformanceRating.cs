namespace UncoveringGreatnessCRM.Services;

/// <summary>
/// The rules behind the status badge on the admin team-performance table. They are deliberately simple and are
/// explained on the dashboard itself, so a rating is never a mystery to the person being rated.
/// </summary>
public static class PerformanceRating
{
    public const string TopPerformer = "Top performer";
    public const string OnTrack = "On track";
    public const string NeedsAttention = "Needs attention";
    public const string NoLeads = "No leads yet";

    /// <summary>Follow-ups overdue at or above this count is a concern.</summary>
    public const int OverdueFollowUpLimit = 3;
    /// <summary>An open lead nobody has contacted for this many days counts as stale.</summary>
    public const int StaleAfterDays = 14;
    /// <summary>A win rate below this (once enough deals have closed) is a concern.</summary>
    public const double LowWinRatePct = 20;
    /// <summary>How many closed deals are needed before the win rate is judged.</summary>
    public const int MinClosedForWinRate = 3;

    public static (string Status, string Reason) Evaluate(int totalLeads, int openLeads, int overdueFollowUps, int staleLeads,
        int closedCount, double? winRatePct, bool isTopThisMonth)
    {
        if (totalLeads == 0) return (NoLeads, "No leads are assigned to this person.");

        var concerns = new List<string>();
        if (overdueFollowUps >= OverdueFollowUpLimit)
            concerns.Add($"{overdueFollowUps} overdue follow-ups");
        if (openLeads >= 2 && staleLeads * 2 >= openLeads)
            concerns.Add($"{staleLeads} of {openLeads} open leads not contacted in {StaleAfterDays} days");
        if (closedCount >= MinClosedForWinRate && winRatePct.HasValue && winRatePct.Value < LowWinRatePct)
            concerns.Add($"win rate only {(int)Math.Round(winRatePct.Value)}%");

        if (concerns.Count > 0) return (NeedsAttention, string.Join("; ", concerns) + ".");
        if (isTopThisMonth) return (TopPerformer, "Highest closed-won value this month.");
        return (OnTrack, "Follow-ups and contact are up to date.");
    }
}
