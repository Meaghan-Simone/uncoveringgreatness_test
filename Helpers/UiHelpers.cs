using System.Globalization;
using System.Text.RegularExpressions;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Helpers;

public static class UiHelpers
{
    /// <summary>"ClosedWon" -> "Closed won", "SalesRep" -> "Sales rep".</summary>
    public static string Label(this Enum value)
    {
        var s = Regex.Replace(value.ToString(), "(?<=[a-z])(?=[A-Z])", " ");
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
            : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    public static string Money(decimal v) => "R " + v.ToString("N0", CultureInfo.InvariantCulture);

    public static string MoneyShort(decimal v) => v switch
    {
        >= 1_000_000m => $"R {(v / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture)}m",
        >= 1_000m => $"R {(v / 1_000m).ToString("0", CultureInfo.InvariantCulture)}k",
        _ => $"R {v.ToString("0", CultureInfo.InvariantCulture)}"
    };

    public static string StageBadge(LeadStage s) => s switch
    {
        LeadStage.Lead => "badge-gray",
        LeadStage.Qualified => "badge-blue",
        LeadStage.Proposal => "badge-violet",
        LeadStage.Negotiation => "badge-amber",
        LeadStage.ClosedWon => "badge-green",
        _ => "badge-red"
    };

    public static string StageColor(LeadStage s) => s switch
    {
        LeadStage.Lead => "var(--text-400)",
        LeadStage.Qualified => "var(--blue-600)",
        LeadStage.Proposal => "var(--violet-600)",
        LeadStage.Negotiation => "var(--amber-600)",
        LeadStage.ClosedWon => "var(--green-600)",
        _ => "var(--red-600)"
    };

    public static string RoleBadge(UserRole r) => r switch
    {
        UserRole.Admin => "badge-gold",
        UserRole.SalesRep => "badge-blue",
        _ => "badge-gray"
    };

    public static string PriorityClass(TaskPriority p) => p.ToString().ToLowerInvariant();

    /// <summary>Badge colour for the admin team-performance status (see <see cref="PerformanceRating"/>).</summary>
    public static string PerformanceBadge(string status) => status switch
    {
        PerformanceRating.TopPerformer => "badge-green",
        PerformanceRating.NeedsAttention => "badge-red",
        PerformanceRating.OnTrack => "badge-blue",
        _ => "badge-gray"
    };

    /// <summary>"42.5%" or an em dash when there is nothing to measure yet.</summary>
    public static string Pct(double? v) => v.HasValue ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    public static string Days(double? v) => v.HasValue ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) + " d" : "—";

    public static string RelativeUtcOrDash(DateTime? utc) => utc.HasValue ? RelativeUtc(utc.Value) : "—";

    /// <summary>Width of a bar as a whole percentage of the biggest bar (never negative, never above 100, always at least 2 when there is a value).</summary>
    public static int BarPct(decimal value, decimal max)
    {
        if (value <= 0 || max <= 0) return 0;
        return (int)Math.Clamp(Math.Round(value / max * 100m), 2m, 100m);
    }

    public static string DateShort(DateTime? d) => d.HasValue ? d.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "—";

    public static string DateRange(DateTime start, DateTime? end)
    {
        if (end is null || end.Value.Date == start.Date) return start.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        if (start.Year == end.Value.Year && start.Month == end.Value.Month)
            return $"{start.Day}–{end.Value.Day} {start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";
        return $"{start.ToString("d MMM", CultureInfo.InvariantCulture)} – {end.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Human friendly local date: "Today, 14:00", "Tomorrow", "Fri, 31 Jul", "12 Mar 2026".</summary>
    public static string RelativeDate(DateTime? local, bool withTime = true)
    {
        if (local is null) return "—";
        var d = local.Value;
        var today = Clock.LocalToday;
        var time = withTime && d.TimeOfDay != TimeSpan.Zero ? ", " + d.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        var diff = (d.Date - today).Days;
        if (diff == 0) return "Today" + time;
        if (diff == 1) return "Tomorrow" + time;
        if (diff == -1) return "Yesterday" + time;
        if (diff is > 1 and < 7) return d.ToString("ddd, d MMM", CultureInfo.InvariantCulture) + time;
        return d.ToString(d.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture) + time;
    }

    public static string RelativeUtc(DateTime utc)
    {
        var l = Clock.ToLocal(utc);
        var diff = (Clock.LocalToday - l.Date).Days;
        var t = l.ToString("HH:mm", CultureInfo.InvariantCulture);
        return diff switch
        {
            0 => $"Today · {t}",
            1 => $"Yesterday · {t}",
            < 7 when diff > 0 => $"{l.ToString("ddd, d MMM", CultureInfo.InvariantCulture)} · {t}",
            _ => $"{l.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} · {t}"
        };
    }

    public static string Greeting()
    {
        var h = Clock.LocalNow.Hour;
        return h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
    }

    public static string FirstName(string? full) => (full ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host + (u.AbsolutePath.Length > 1 ? u.AbsolutePath.TrimEnd('/') : "")).Replace("www.", "") : url;
    }

    public static bool IsSafeHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static string NotificationIcon(NotificationType t) => t switch
    {
        NotificationType.FollowUp => "bi-clock-history",
        NotificationType.DealWon => "bi-check2-circle",
        NotificationType.LeadAssigned => "bi-person-plus",
        NotificationType.TaskDue => "bi-check2-square",
        NotificationType.CalendarReminder => "bi-calendar-event",
        _ => "bi-info-circle"
    };

    public static (string bg, string fg) NotificationColors(NotificationType t) => t switch
    {
        NotificationType.FollowUp => ("var(--amber-100)", "var(--amber-600)"),
        NotificationType.DealWon => ("var(--green-100)", "var(--green-600)"),
        NotificationType.LeadAssigned => ("var(--blue-100)", "var(--blue-600)"),
        NotificationType.TaskDue => ("var(--violet-100)", "var(--violet-600)"),
        NotificationType.CalendarReminder => ("var(--blue-100)", "var(--blue-600)"),
        _ => ("var(--gold-100)", "var(--gold-700)")
    };
}

public static class CompanySizes
{
    public static readonly string[] All = { "1–50", "51–200", "201–500", "500–1,000", "1,000+" };
}
