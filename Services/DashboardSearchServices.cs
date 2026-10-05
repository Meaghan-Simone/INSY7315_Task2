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
        return dto;
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
