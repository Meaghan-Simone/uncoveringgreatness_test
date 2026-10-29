using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class DashboardService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly TaskService _tasks;
    public DashboardService(AppDbContext db, ICurrentUser me, TaskService tasks) { _db = db; _me = me; _tasks = tasks; }

    public async Task<DashboardDto> GetAsync()
    {
        var myId = _me.Id;
        var today = Clock.LocalToday;
        var endOfToday = today.AddDays(1);
        var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonthStartUtc = monthStartUtc.AddMonths(-1);

        var rows = await _db.Leads.AsNoTracking().VisibleTo(_me).Select(l => new { l.Stage, l.DealValue, l.CreatedUtc, l.ClosedUtc }).ToListAsync();
        var dto = new DashboardDto
        {
            TotalLeads = rows.Count,
            TotalCompanies = await _db.Companies.CountAsync(),
            TotalEvents = await _db.Events.CountAsync()
        };
        var open = rows.Where(r => r.Stage < LeadStage.ClosedWon).ToList();
        dto.ActiveLeads = open.Count;
        dto.PipelineValue = open.Sum(r => r.DealValue);
        var wonMtd = rows.Where(r => r.Stage == LeadStage.ClosedWon && r.ClosedUtc >= monthStartUtc).ToList();
        dto.ClosedWonMtd = wonMtd.Sum(r => r.DealValue);
        dto.ClosedWonCountMtd = wonMtd.Count;
        dto.NewLeadsThisMonth = rows.Count(r => r.CreatedUtc >= monthStartUtc);
        var lastMonth = rows.Count(r => r.CreatedUtc >= lastMonthStartUtc && r.CreatedUtc < monthStartUtc);
        dto.LeadGrowthPct = lastMonth == 0 ? null : Math.Round((dto.NewLeadsThisMonth - lastMonth) * 100.0 / lastMonth, 1);
        dto.Stages = Enum.GetValues<LeadStage>().Where(s => s != LeadStage.ClosedLost)
            .Select(s => new StageStat(s, rows.Count(r => r.Stage == s), rows.Where(r => r.Stage == s).Sum(r => r.DealValue))).ToList();

        // Everything below is scoped: admins see every lead, everyone else only the leads assigned to them.
        var follow = _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.NextFollowUp != null && l.NextFollowUp < endOfToday
                                                        && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost);
        dto.FollowUpsDue = await follow.CountAsync();
        dto.FollowUpsOverdue = await follow.CountAsync(l => l.NextFollowUp < today);
        dto.FollowUps = await follow.OrderBy(l => l.NextFollowUp).Take(6).Select(Project(myId)).ToListAsync();
        dto.RecentLeads = await _db.Leads.AsNoTracking().VisibleTo(_me).OrderByDescending(l => l.CreatedUtc).ThenByDescending(l => l.Id).Take(6).Select(Project(myId)).ToListAsync();

        var events = await _db.Events.AsNoTracking().Where(e => (e.EndDate ?? e.StartDate) >= today).OrderBy(e => e.StartDate).Take(3).ToListAsync();
        var evIds = events.Select(e => e.Id).ToList();
        var counts = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.EventId != null && evIds.Contains(l.EventId.Value))
            .GroupBy(l => l.EventId).Select(g => new { Id = g.Key, C = g.Count() }).ToListAsync();
        dto.UpcomingEvents = events.Select(e => new EventListItem(e.Id, e.Name, e.Type, e.StartDate, e.EndDate, e.Location,
            counts.Where(c => c.Id == e.Id).Select(c => c.C).FirstOrDefault(), true, new List<string>())).ToList();

        if (_me.CanWrite)
        {
            var board = await _tasks.BoardAsync(_me.IsAdmin ? myId : null);
            if (board.Succeeded)
                dto.MyTasks = board.Value!.Overdue.Concat(board.Value.DueToday).Concat(board.Value.Upcoming).Where(t => t.AssignedToId == myId).Take(5).ToList();
        }

        // Only admins get the per-person breakdown; it reads every lead, so it must never run for anyone else.
        if (_me.IsAdmin) dto.Team = await BuildTeamAsync(monthStartUtc);
        return dto;
    }

    /// <summary>
    /// Admin-only: one row per person who owns (or can own) leads, plus team-wide trend and source figures.
    /// Rows are loaded as lean projections and totalled in memory because SQLite cannot SUM decimal columns in SQL.
    /// </summary>
    private async Task<TeamSummary?> BuildTeamAsync(DateTime monthStartUtc)
    {
        if (!_me.IsAdmin) return null;

        var now = DateTime.UtcNow;
        var today = Clock.LocalToday;
        var since30 = now.AddDays(-30);
        var staleBefore = now.AddDays(-PerformanceRating.StaleAfterDays);

        var users = await _db.Users.AsNoTracking().Where(u => u.Role != UserRole.Staff)
            .Select(u => new { u.Id, u.FullName, u.Role, u.IsActive, u.LastLoginUtc }).ToListAsync();
        var leads = await _db.Leads.AsNoTracking()
            .Select(l => new { l.AssignedToId, l.Stage, l.DealValue, l.Source, l.CreatedUtc, l.ClosedUtc, l.NextFollowUp, l.LastContactedUtc }).ToListAsync();
        var tasks = await _db.Tasks.AsNoTracking()
            .Select(t => new { t.AssignedToId, t.IsCompleted, t.DueAt, t.CompletedUtc }).ToListAsync();
        var acts = await _db.LeadActivities.AsNoTracking()
            .Where(a => a.UserId != null && a.CreatedUtc >= since30
                        && (a.Type == ActivityType.CallLogged || a.Type == ActivityType.NoteAdded || a.Type == ActivityType.EmailSent || a.Type == ActivityType.StageChanged))
            .Select(a => new { a.UserId, a.CreatedUtc }).ToListAsync();

        var reps = new List<RepPerformance>();
        foreach (var u in users)
        {
            var mine = leads.Where(l => l.AssignedToId == u.Id).ToList();
            var myTasks = tasks.Where(t => t.AssignedToId == u.Id).ToList();
            // Hide people who have nothing to measure (an inactive account, or an admin who doesn't own leads or tasks).
            if (mine.Count == 0 && (!u.IsActive || (u.Role == UserRole.Admin && myTasks.Count == 0))) continue;

            var open = mine.Where(l => l.Stage < LeadStage.ClosedWon).ToList();
            var won = mine.Where(l => l.Stage == LeadStage.ClosedWon).ToList();
            var lost = mine.Count(l => l.Stage == LeadStage.ClosedLost);
            var wonMtd = won.Where(l => l.ClosedUtc >= monthStartUtc).ToList();
            var closed = won.Count + lost;
            var cycle = won.Where(l => l.ClosedUtc.HasValue).Select(l => Math.Max(0, (l.ClosedUtc!.Value - l.CreatedUtc).TotalDays)).ToList();

            reps.Add(new RepPerformance
            {
                UserId = u.Id, Name = u.FullName, Role = u.Role, IsActive = u.IsActive, LastLoginUtc = u.LastLoginUtc,
                TotalLeads = mine.Count,
                OpenLeads = open.Count,
                PipelineValue = open.Sum(l => l.DealValue),
                NewThisMonth = mine.Count(l => l.CreatedUtc >= monthStartUtc),
                WonCount = won.Count,
                LostCount = lost,
                WonValue = won.Sum(l => l.DealValue),
                WonMtdCount = wonMtd.Count,
                WonMtdValue = wonMtd.Sum(l => l.DealValue),
                WinRatePct = closed == 0 ? null : Math.Round(won.Count * 100.0 / closed, 1),
                AvgDaysToWin = cycle.Count == 0 ? null : Math.Round(cycle.Average(), 1),
                OverdueFollowUps = open.Count(l => l.NextFollowUp != null && l.NextFollowUp < today),
                StaleLeads = open.Count(l => (l.LastContactedUtc ?? l.CreatedUtc) < staleBefore),
                OpenTasks = myTasks.Count(t => !t.IsCompleted),
                OverdueTasks = myTasks.Count(t => !t.IsCompleted && t.DueAt != null && t.DueAt.Value.Date < today),
                TasksCompleted30d = myTasks.Count(t => t.IsCompleted && t.CompletedUtc >= since30),
                Activity30d = acts.Count(a => a.UserId == u.Id),
                Stages = Enum.GetValues<LeadStage>().Where(st => st != LeadStage.ClosedLost)
                    .Select(st => new StageStat(st, mine.Count(l => l.Stage == st), mine.Where(l => l.Stage == st).Sum(l => l.DealValue))).ToList()
            });
        }

        // Rating needs the whole team's month first, to know who is on top.
        var bestMonth = reps.Count == 0 ? 0m : reps.Max(r => r.WonMtdValue);
        foreach (var r in reps)
        {
            var (status, reason) = PerformanceRating.Evaluate(r.TotalLeads, r.OpenLeads, r.OverdueFollowUps, r.StaleLeads,
                r.WonCount + r.LostCount, r.WinRatePct, bestMonth > 0 && r.WonMtdValue == bestMonth);
            r.Status = status; r.StatusReason = reason;
        }
        reps = reps.OrderByDescending(r => r.WonMtdValue).ThenByDescending(r => r.PipelineValue).ThenBy(r => r.Name).ToList();

        var trend = new List<MonthPoint>();
        for (var i = 5; i >= 0; i--)
        {
            var start = monthStartUtc.AddMonths(-i);
            var end = start.AddMonths(1);
            var wonInMonth = leads.Where(l => l.Stage == LeadStage.ClosedWon && l.ClosedUtc >= start && l.ClosedUtc < end).ToList();
            trend.Add(new MonthPoint(start.ToString("MMM", CultureInfo.InvariantCulture),
                leads.Count(l => l.CreatedUtc >= start && l.CreatedUtc < end), wonInMonth.Count, wonInMonth.Sum(l => l.DealValue)));
        }

        var sources = Enum.GetValues<LeadSource>()
            .Select(src => new SourceStat(src, leads.Count(l => l.Source == src), leads.Count(l => l.Source == src && l.Stage == LeadStage.ClosedWon)))
            .Where(x => x.Count > 0).OrderByDescending(x => x.Count).ToList();

        var allWon = leads.Where(l => l.Stage == LeadStage.ClosedWon).ToList();
        var allLost = leads.Count(l => l.Stage == LeadStage.ClosedLost);
        var unassigned = leads.Where(l => l.AssignedToId == null && l.Stage < LeadStage.ClosedWon).ToList();
        return new TeamSummary
        {
            Reps = reps, Trend = trend, Sources = sources,
            OpenCount = leads.Count(l => l.Stage < LeadStage.ClosedWon),
            WonCount = allWon.Count,
            LostCount = allLost,
            WinRatePct = allWon.Count + allLost == 0 ? null : Math.Round(allWon.Count * 100.0 / (allWon.Count + allLost), 1),
            AvgWonDeal = allWon.Count == 0 ? 0m : Math.Round(allWon.Sum(l => l.DealValue) / allWon.Count, 0),
            Activity30d = acts.Count,
            UnassignedOpenLeads = unassigned.Count,
            UnassignedOpenValue = unassigned.Sum(l => l.DealValue)
        };
    }

    private System.Linq.Expressions.Expression<Func<Lead, LeadListItem>> Project(int myId) =>
        l => new LeadListItem(l.Id, l.FirstName + " " + l.Surname, l.JobTitle, l.CompanyId, l.Company != null ? l.Company.Name : null, l.Stage,
            l.DealValue, l.Source, l.AssignedToId, l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp, false, l.Event != null ? l.Event.Name : null);
}

public record SearchResults(string Query, List<LeadListItem> Leads, List<CompanyListItem> Companies, List<EventListItem> Events)
{
    public int Total => Leads.Count + Companies.Count + Events.Count;
}

public class SearchService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    public SearchService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    public async Task<SearchResults> SearchAsync(string? q)
    {
        var t = (q ?? "").Trim();
        if (t.Length < 2) return new SearchResults(t, new(), new(), new());
        var isAdmin = _me.IsAdmin; var myId = _me.Id;
        var leads = await _db.Leads.AsNoTracking().VisibleTo(_me)
            .Where(l => l.FirstName.Contains(t) || l.Surname.Contains(t) || l.Email.Contains(t) || (l.FirstName + " " + l.Surname).Contains(t) || (l.Company != null && l.Company.Name.Contains(t)))
            .OrderBy(l => l.FirstName).Take(10)
            .Select(l => new LeadListItem(l.Id, l.FirstName + " " + l.Surname, l.JobTitle, l.CompanyId, l.Company != null ? l.Company.Name : null, l.Stage,
                l.DealValue, l.Source, l.AssignedToId, l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp, false, l.Event != null ? l.Event.Name : null)).ToListAsync();
        var companies = await _db.Companies.AsNoTracking().Where(c => c.Name.Contains(t) || (c.ContactPerson != null && c.ContactPerson.Contains(t)))
            .OrderBy(c => c.Name).Take(8)
            .Select(c => new CompanyListItem(c.Id, c.Name, c.City, c.Industry, c.Size, c.Leads.Count(l => isAdmin || l.AssignedToId == myId), c.ContactPerson)).ToListAsync();
        var events = await _db.Events.AsNoTracking().Where(e => e.Name.Contains(t) || (e.Location != null && e.Location.Contains(t)))
            .OrderByDescending(e => e.StartDate).Take(8).ToListAsync();
        var today = Clock.LocalToday;
        return new SearchResults(t, leads, companies, events.Select(e => new EventListItem(e.Id, e.Name, e.Type, e.StartDate, e.EndDate, e.Location, 0,
            (e.EndDate ?? e.StartDate).Date >= today, new List<string>())).ToList());
    }
}
