using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class TaskService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    private readonly INotificationService _notify;
    public TaskService(AppDbContext db, ICurrentUser me, IAuditService audit, INotificationService notify)
    { _db = db; _me = me; _audit = audit; _notify = notify; }

    private IQueryable<CrmTask> Visible()
    {
        var me = _me.Id;
        return _me.IsAdmin ? _db.Tasks.AsNoTracking() : _db.Tasks.AsNoTracking().Where(t => t.AssignedToId == me || t.CreatedById == me);
    }

    private bool CanChange(CrmTask t) => _me.IsAdmin || (_me.CanWrite && (t.AssignedToId == _me.Id || t.CreatedById == _me.Id));

    private TaskItem ToItem(CrmTask t, DateTime today) => new(t.Id, t.Title, t.Description, t.DueAt, t.Priority, t.IsCompleted, t.AssignedToId,
        t.AssignedTo?.FullName ?? "", t.LeadId, t.Lead == null ? null : t.Lead.FirstName + " " + t.Lead.Surname, !t.IsCompleted && t.DueAt != null && t.DueAt.Value.Date < today, CanChange(t));

    public async Task<Result<TaskBoard>> BoardAsync(int? assignedToId)
    {
        if (!_me.CanWrite) return Result<TaskBoard>.From(Result.Forbidden("Tasks are available to admins and sales reps."));
        var today = Clock.LocalToday;
        var q = Visible().Include(t => t.AssignedTo).Include(t => t.Lead).AsQueryable();
        if (assignedToId.HasValue) q = q.Where(t => t.AssignedToId == assignedToId);

        var open = await q.Where(t => !t.IsCompleted).OrderBy(t => t.DueAt == null).ThenBy(t => t.DueAt).Take(500).ToListAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var done = await q.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedUtc).Take(15).ToListAsync();
        var doneMonth = await Visible().CountAsync(t => t.IsCompleted && t.CompletedUtc >= monthStart);

        var board = new TaskBoard { OpenCount = open.Count, CompletedThisMonth = doneMonth };
        foreach (var t in open)
        {
            var item = ToItem(t, today);
            var d = t.DueAt?.Date;
            if (d is null) board.Later.Add(item);
            else if (d < today) board.Overdue.Add(item);
            else if (d == today) board.DueToday.Add(item);
            else if (d <= today.AddDays(7)) board.Upcoming.Add(item);
            else board.Later.Add(item);
        }
        board.Completed = done.Select(t => ToItem(t, today)).ToList();
        board.DueTodayCount = board.DueToday.Count;
        board.OverdueCount = board.Overdue.Count;
        board.ThisWeekCount = board.DueToday.Count + board.Upcoming.Count + board.Overdue.Count;
        return Result<TaskBoard>.Success(board);
    }

    public async Task<Result<TaskInput>> GetInputAsync(int id)
    {
        var t = await Visible().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Result<TaskInput>.From(Result.NotFound("Task"));
        if (!CanChange(t)) return Result<TaskInput>.From(Result.Forbidden());
        return Result<TaskInput>.Success(new TaskInput { Title = t.Title, Description = t.Description, DueAt = t.DueAt, Priority = t.Priority, AssignedToId = t.AssignedToId, LeadId = t.LeadId });
    }

    private async Task<Result> ValidateAsync(TaskInput i)
    {
        if (i.AssignedToId.HasValue && !await _db.Users.AnyAsync(u => u.Id == i.AssignedToId && u.IsActive && u.Role != UserRole.Staff))
            return Result.Invalid(nameof(TaskInput.AssignedToId), "Tasks can only be assigned to active admins or sales reps.");
        if (i.LeadId.HasValue && !await _db.Leads.VisibleTo(_me).AnyAsync(l => l.Id == i.LeadId))
            return Result.Invalid(nameof(TaskInput.LeadId), "Selected lead does not exist.");
        return Result.Success();
    }

    public async Task<Result<int>> CreateAsync(TaskInput input)
    {
        if (!_me.CanWrite) return Result<int>.From(Result.Forbidden("Your role is read-only."));
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return Result<int>.From(check);
        var t = new CrmTask
        {
            Title = input.Title.Trim(), Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(), DueAt = input.DueAt,
            Priority = input.Priority, AssignedToId = input.AssignedToId ?? _me.Id, LeadId = input.LeadId, CreatedById = _me.Id
        };
        _db.Tasks.Add(t);
        await _db.SaveChangesAsync();
        if (t.AssignedToId != _me.Id)
            await _notify.NotifyAsync(t.AssignedToId, NotificationType.TaskDue, "Task assigned to you", $"{t.Title} — assigned by {_me.Name}", "/Tasks");
        await _audit.LogAsync("TaskCreated", "Task", t.Id, t.Title);
        return Result<int>.Success(t.Id);
    }

    public async Task<Result> UpdateAsync(int id, TaskInput input)
    {
        var t = await _db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Result.NotFound("Task");
        if (!CanChange(t)) return Result.Forbidden();
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return check;
        var oldAssignee = t.AssignedToId;
        t.Title = input.Title.Trim(); t.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        t.DueAt = input.DueAt; t.Priority = input.Priority; t.LeadId = input.LeadId; t.AssignedToId = input.AssignedToId ?? t.AssignedToId;
        await _db.SaveChangesAsync();
        if (t.AssignedToId != oldAssignee && t.AssignedToId != _me.Id)
            await _notify.NotifyAsync(t.AssignedToId, NotificationType.TaskDue, "Task assigned to you", $"{t.Title} — assigned by {_me.Name}", "/Tasks");
        await _audit.LogAsync("TaskUpdated", "Task", id, t.Title);
        return Result.Success();
    }

    public async Task<Result<bool>> SetCompletedAsync(int id, bool completed)
    {
        var t = await _db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Result<bool>.From(Result.NotFound("Task"));
        if (!CanChange(t)) return Result<bool>.From(Result.Forbidden());
        t.IsCompleted = completed;
        t.CompletedUtc = completed ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();
        return Result<bool>.Success(completed);
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var t = await _db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Result.NotFound("Task");
        if (!_me.IsAdmin && !(t.CreatedById == _me.Id && _me.CanWrite)) return Result.Forbidden("Only the creator or an admin can delete a task.");
        _db.Tasks.Remove(t);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("TaskDeleted", "Task", id, t.Title);
        return Result.Success();
    }
}

public class CalendarService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly INotificationService _notify;
    private readonly IAuditService _audit;
    public CalendarService(AppDbContext db, ICurrentUser me, INotificationService notify, IAuditService audit)
    { _db = db; _me = me; _notify = notify; _audit = audit; }

    private IQueryable<CalendarEvent> VisibleEvents()
    {
        var me = _me.Id;
        return _db.CalendarEvents.AsNoTracking()
            .Where(e => e.OwnerId == me || e.Visibility == CalendarVisibility.Team || e.Shares.Any(s => s.UserId == me));
    }

    private CalendarItem ToItem(CalendarEvent e)
    {
        string? label = null, url = null;
        if (e.LinkedLead != null) { label = e.LinkedLead.FirstName + " " + e.LinkedLead.Surname + " (Lead)"; url = $"/Leads/Details/{e.LinkedLeadId}"; }
        else if (e.LinkedCompany != null) { label = e.LinkedCompany.Name + " (Company)"; url = $"/Companies/Details/{e.LinkedCompanyId}"; }
        else if (e.LinkedEvent != null) { label = e.LinkedEvent.Name + " (Event)"; url = $"/Events/Details/{e.LinkedEventId}"; }
        return new CalendarItem("ev:" + e.Id, e.Id, e.Title, e.StartsAt, e.Type, e.Notes, e.Visibility, e.OwnerId, e.Owner?.FullName ?? "",
            e.OwnerId == _me.Id, false, label, url, e.Shares.Select(s => s.User?.FullName ?? "").Where(n => n != "").ToList());
    }

    private IQueryable<CalendarEvent> WithRefs(IQueryable<CalendarEvent> q) =>
        q.Include(e => e.Owner).Include(e => e.LinkedLead).Include(e => e.LinkedCompany).Include(e => e.LinkedEvent).Include(e => e.Shares).ThenInclude(s => s.User);

    public async Task<CalendarMonth> MonthAsync(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var gridStart = first.AddDays(-(int)first.DayOfWeek);
        var gridEnd = gridStart.AddDays(42);
        var events = await WithRefs(VisibleEvents().Where(e => e.StartsAt >= gridStart && e.StartsAt < gridEnd)).OrderBy(e => e.StartsAt).ToListAsync();
        var items = events.Select(ToItem).ToList();
        items.AddRange(await FollowUpItemsAsync(gridStart, gridEnd));

        var now = Clock.LocalToday;
        var upcomingEvents = await WithRefs(VisibleEvents().Where(e => e.StartsAt >= now)).OrderBy(e => e.StartsAt).Take(8).ToListAsync();
        var upcoming = upcomingEvents.Select(ToItem).ToList();
        upcoming.AddRange(await FollowUpItemsAsync(now, now.AddDays(30)));
        return new CalendarMonth
        {
            Year = year, Month = month,
            Items = items.OrderBy(i => i.StartsAt).ToList(),
            Upcoming = upcoming.OrderBy(i => i.StartsAt).Take(8).ToList()
        };
    }

    /// <summary>Lead follow-ups assigned to the current user appear on their calendar automatically (read-only).</summary>
    private async Task<List<CalendarItem>> FollowUpItemsAsync(DateTime from, DateTime to)
    {
        var me = _me.Id;
        var leads = await _db.Leads.AsNoTracking()
            .Where(l => l.AssignedToId == me && l.NextFollowUp != null && l.NextFollowUp >= from && l.NextFollowUp < to
                        && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost)
            .Select(l => new { l.Id, Name = l.FirstName + " " + l.Surname, l.NextFollowUp }).ToListAsync();
        return leads.Select(l => new CalendarItem("fu:" + l.Id, null, "Follow up — " + l.Name, l.NextFollowUp!.Value, CalendarItemType.FollowUp, null,
            CalendarVisibility.Private, me, _me.Name, true, true, l.Name + " (Lead)", $"/Leads/Details/{l.Id}", new List<string>())).ToList();
    }

    public async Task<Result<CalendarItem>> GetAsync(int id)
    {
        var e = await WithRefs(VisibleEvents().Where(x => x.Id == id)).FirstOrDefaultAsync();
        return e is null ? Result<CalendarItem>.From(Result.NotFound("Event")) : Result<CalendarItem>.Success(ToItem(e));
    }

    public async Task<Result<CalendarInput>> GetInputAsync(int id)
    {
        var e = await _db.CalendarEvents.AsNoTracking().Include(x => x.Shares).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null || (e.OwnerId != _me.Id && e.Visibility != CalendarVisibility.Team && !e.Shares.Any(s => s.UserId == _me.Id)))
            return Result<CalendarInput>.From(Result.NotFound("Event"));
        if (e.OwnerId != _me.Id) return Result<CalendarInput>.From(Result.Forbidden("Only the owner can edit this event."));
        string? type = null; int? linked = null;
        if (e.LinkedLeadId != null) { type = "Lead"; linked = e.LinkedLeadId; }
        else if (e.LinkedCompanyId != null) { type = "Company"; linked = e.LinkedCompanyId; }
        else if (e.LinkedEventId != null) { type = "Event"; linked = e.LinkedEventId; }
        return Result<CalendarInput>.Success(new CalendarInput
        {
            Title = e.Title, Date = e.StartsAt.Date, Time = e.StartsAt.TimeOfDay, Type = e.Type, Notes = e.Notes, Visibility = e.Visibility,
            SharedWith = e.Shares.Select(s => s.UserId).ToList(), LinkedType = type, LinkedId = linked
        });
    }

    private async Task<Result> ValidateAsync(CalendarInput i)
    {
        if (i.Visibility == CalendarVisibility.Team && !_me.IsAdmin)
            return Result.Invalid(nameof(CalendarInput.Visibility), "Only admins can create team-wide events.");
        if (i.Visibility == CalendarVisibility.Shared)
        {
            var ids = i.SharedWith.Distinct().Where(x => x != _me.Id).ToList();
            if (ids.Count == 0) return Result.Invalid(nameof(CalendarInput.SharedWith), "Choose at least one person to share with.");
            var valid = await _db.Users.CountAsync(u => ids.Contains(u.Id) && u.IsActive);
            if (valid != ids.Count) return Result.Invalid(nameof(CalendarInput.SharedWith), "One or more selected people are not valid.");
        }
        if (!string.IsNullOrEmpty(i.LinkedType))
        {
            var exists = i.LinkedId is int lid && i.LinkedType switch
            {
                "Lead" => await _db.Leads.VisibleTo(_me).AnyAsync(l => l.Id == lid),
                "Company" => await _db.Companies.AnyAsync(c => c.Id == lid),
                "Event" => await _db.Events.AnyAsync(e => e.Id == lid),
                _ => false
            };
            if (!exists) return Result.Invalid(nameof(CalendarInput.LinkedId), "The linked record does not exist.");
        }
        return Result.Success();
    }

    private void Apply(CalendarEvent e, CalendarInput i)
    {
        e.Title = i.Title.Trim(); e.StartsAt = i.Date!.Value.Date + i.Time!.Value; e.Type = i.Type;
        e.Notes = string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim(); e.Visibility = i.Visibility;
        e.LinkedLeadId = i.LinkedType == "Lead" ? i.LinkedId : null;
        e.LinkedCompanyId = i.LinkedType == "Company" ? i.LinkedId : null;
        e.LinkedEventId = i.LinkedType == "Event" ? i.LinkedId : null;
    }

    public async Task<Result<int>> CreateAsync(CalendarInput input)
    {
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return Result<int>.From(check);
        var e = new CalendarEvent { OwnerId = _me.Id };
        Apply(e, input);
        if (input.Visibility == CalendarVisibility.Shared)
            foreach (var uid in input.SharedWith.Distinct().Where(x => x != _me.Id)) e.Shares.Add(new CalendarShare { UserId = uid });
        _db.CalendarEvents.Add(e);
        await _db.SaveChangesAsync();
        foreach (var s in e.Shares)
            await _notify.NotifyAsync(s.UserId, NotificationType.System, "Calendar event shared with you", $"{_me.Name} shared “{e.Title}”.", "/Calendar?year=" + e.StartsAt.Year + "&month=" + e.StartsAt.Month);
        return Result<int>.Success(e.Id);
    }

    public async Task<Result> UpdateAsync(int id, CalendarInput input)
    {
        var e = await _db.CalendarEvents.Include(x => x.Shares).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result.NotFound("Event");
        if (e.OwnerId != _me.Id) return Result.Forbidden("Only the owner can edit this event.");
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return check;
        Apply(e, input);
        var wanted = input.Visibility == CalendarVisibility.Shared ? input.SharedWith.Distinct().Where(x => x != _me.Id).ToHashSet() : new HashSet<int>();
        foreach (var s in e.Shares.Where(s => !wanted.Contains(s.UserId)).ToList()) _db.CalendarShares.Remove(s);
        var added = wanted.Where(w => e.Shares.All(s => s.UserId != w)).ToList();
        foreach (var uid in added) e.Shares.Add(new CalendarShare { UserId = uid });
        await _db.SaveChangesAsync();
        foreach (var uid in added)
            await _notify.NotifyAsync(uid, NotificationType.System, "Calendar event shared with you", $"{_me.Name} shared “{e.Title}”.", "/Calendar?year=" + e.StartsAt.Year + "&month=" + e.StartsAt.Month);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var e = await _db.CalendarEvents.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result.NotFound("Event");
        if (e.OwnerId != _me.Id && !(e.Visibility == CalendarVisibility.Team && _me.IsAdmin)) return Result.Forbidden("Only the owner can delete this event.");
        _db.CalendarEvents.Remove(e);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CalendarEventDeleted", "CalendarEvent", id, e.Title);
        return Result.Success();
    }
}
