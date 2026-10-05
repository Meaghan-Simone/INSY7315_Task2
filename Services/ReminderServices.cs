using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;

namespace UncoveringGreatnessCRM.Services;

/// <summary>Settings under "Reminders" in configuration (all overridable with Reminders__Name environment variables).</summary>
public class ReminderOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Send each person one summary email for the reminders created in a run (needs the Email settings).</summary>
    public bool EmailEnabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
    /// <summary>Reminders are only created between these local (South African) hours, so nobody is emailed at night.</summary>
    public int ActiveFromHour { get; set; } = 6;
    public int ActiveToHour { get; set; } = 21;
    /// <summary>How long before a calendar entry starts the reminder is sent.</summary>
    public int EventLeadMinutes { get; set; } = 60;
}

public sealed record ReminderItem(int UserId, string Title, string Body, string Link);

/// <summary>Builds the plain-text and HTML summary email for a person's new reminders.</summary>
public static class ReminderEmail
{
    public static (string Subject, string Html, string Text) Build(string name, IReadOnlyList<ReminderItem> items, Func<string, string> absolute)
    {
        var first = string.IsNullOrWhiteSpace(name) ? "there" : name.Split(' ')[0];
        var subject = items.Count == 1 ? $"Reminder: {items[0].Title}" : $"{items.Count} reminders from Uncovering Greatness CRM";
        var text = new System.Text.StringBuilder($"Hi {first},\n\nHere {(items.Count == 1 ? "is your reminder" : "are your reminders")}:\n\n");
        var html = new System.Text.StringBuilder($"<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#2F3A1B;max-width:560px\"><p>Hi {WebUtility.HtmlEncode(first)},</p><p>Here {(items.Count == 1 ? "is your reminder" : "are your reminders")}:</p>");
        foreach (var i in items)
        {
            var url = absolute(i.Link);
            text.Append($"- {i.Title}\n  {i.Body}\n  {url}\n\n");
            html.Append($"<div style=\"border-left:4px solid #6B8E23;background:#F7F8F2;padding:10px 14px;margin:10px 0\"><strong>{WebUtility.HtmlEncode(i.Title)}</strong><br>{WebUtility.HtmlEncode(i.Body)}<br><a href=\"{WebUtility.HtmlEncode(url)}\" style=\"color:#4B5D2A\">Open in the CRM</a></div>");
        }
        var all = absolute("/Notifications");
        text.Append($"All notifications: {all}\n");
        html.Append($"<p style=\"font-size:13px;color:#5C6A34\">You receive these because you use Uncovering Greatness CRM. <a href=\"{WebUtility.HtmlEncode(all)}\" style=\"color:#4B5D2A\">See all notifications</a>.</p></div>");
        return (subject, html.ToString(), text.ToString());
    }
}

/// <summary>
/// Creates in-app notifications for follow-ups, tasks and calendar entries, then emails each person one summary of what is new.
/// Every reminder carries a dedupe key, so a reminder is created (and emailed) exactly once, however often the worker runs.
/// </summary>
public class ReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReminderWorker> _log;
    private readonly ReminderOptions _opt;
    public ReminderWorker(IServiceScopeFactory scopes, ILogger<ReminderWorker> log, IOptions<ReminderOptions> opt) { _scopes = scopes; _log = log; _opt = opt.Value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("Reminders are switched off (Reminders:Enabled=false)."); return; }
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(_opt.IntervalMinutes, 1, 60)));
        do
        {
            try
            {
                var hour = Clock.LocalNow.Hour;
                if (hour >= _opt.ActiveFromHour && hour < _opt.ActiveToHour) await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Reminder run failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass over everything that is due. Returns how many new reminders were created.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var notifier = sp.GetRequiredService<INotificationService>();
        var now = Clock.LocalNow; var today = now.Date; var tomorrow = today.AddDays(1); var dayAfter = today.AddDays(2); var key = today.ToString("yyyyMMdd");

        var people = await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.FullName, u.Email }).ToDictionaryAsync(u => u.Id, ct);
        var created = new List<ReminderItem>();
        async Task Add(int userId, NotificationType type, string title, string body, string link, string dedupe)
        {
            if (!people.ContainsKey(userId)) return;
            if (await notifier.NotifyAsync(userId, type, title, body, link, dedupe)) created.Add(new ReminderItem(userId, title, body, link));
        }
        static string At(DateTime d) => d.TimeOfDay != TimeSpan.Zero ? " at " + d.ToString("HH:mm") : "";

        // Lead follow-ups: due today, overdue, or due tomorrow (heads-up)
        var leads = await db.Leads.AsNoTracking().Where(l => l.AssignedToId != null && l.NextFollowUp != null && l.NextFollowUp < dayAfter
                && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost)
            .OrderBy(l => l.NextFollowUp).Select(l => new { l.Id, l.AssignedToId, Name = l.FirstName + " " + l.Surname, l.NextFollowUp }).Take(500).ToListAsync(ct);
        foreach (var l in leads)
        {
            var due = l.NextFollowUp!.Value; var link = $"/Leads/Details/{l.Id}";
            if (due >= tomorrow) await Add(l.AssignedToId!.Value, NotificationType.FollowUp, $"Follow-up tomorrow — {l.Name}", $"Next follow-up is scheduled for tomorrow{At(due)}.", link, $"fu1:{l.Id}:{key}");
            else
            {
                var overdue = due.Date < today;
                await Add(l.AssignedToId!.Value, NotificationType.FollowUp, $"Follow-up {(overdue ? "overdue" : "due")} — {l.Name}",
                    overdue ? $"The follow-up was scheduled for {UiHelpers.DateShort(due)}." : $"Next follow-up is scheduled for today{At(due)}.", link, $"fu:{l.Id}:{key}");
            }
        }

        // Tasks: due today, overdue, or due tomorrow (heads-up)
        var tasks = await db.Tasks.AsNoTracking().Where(t => !t.IsCompleted && t.DueAt != null && t.DueAt < dayAfter)
            .OrderBy(t => t.DueAt).Select(t => new { t.Id, t.AssignedToId, t.Title, t.DueAt }).Take(500).ToListAsync(ct);
        foreach (var t in tasks)
        {
            var due = t.DueAt!.Value;
            if (due >= tomorrow) await Add(t.AssignedToId, NotificationType.TaskDue, $"Task due tomorrow — {t.Title}", $"This task is due tomorrow{At(due)}.", "/Tasks", $"task1:{t.Id}:{key}");
            else await Add(t.AssignedToId, NotificationType.TaskDue, $"Task due — {t.Title}", due.Date < today ? "This task is overdue." : $"This task is due today{At(due)}.", "/Tasks", $"task:{t.Id}:{key}");
        }

        // Calendar entries starting within the lead time: the owner and everyone it is shared with
        var windowEnd = now.AddMinutes(Math.Clamp(_opt.EventLeadMinutes, 5, 24 * 60));
        var events = await db.CalendarEvents.AsNoTracking().Include(e => e.Shares).Where(e => e.StartsAt >= now && e.StartsAt <= windowEnd).OrderBy(e => e.StartsAt).Take(500).ToListAsync(ct);
        foreach (var e in events)
        {
            var mins = Math.Max(1, (int)Math.Round((e.StartsAt - now).TotalMinutes));
            var link = $"/Calendar?year={e.StartsAt.Year}&month={e.StartsAt.Month}";
            foreach (var uid in e.Shares.Select(s => s.UserId).Append(e.OwnerId).Distinct())
                await Add(uid, NotificationType.CalendarReminder, $"Starting soon — {e.Title}", $"Starts at {e.StartsAt:HH:mm} (in about {mins} minutes).", link, $"cal:{e.Id}:{e.StartsAt:yyyyMMddHHmm}");
        }

        if (created.Count > 0 && _opt.EmailEnabled) await EmailDigestsAsync(sp, people.ToDictionary(p => p.Key, p => (p.Value.FullName, p.Value.Email)), created, ct);
        if (created.Count > 0) _log.LogInformation("Created {Count} reminder(s) for {People} person(s).", created.Count, created.Select(c => c.UserId).Distinct().Count());
        return created.Count;
    }

    private async Task EmailDigestsAsync(IServiceProvider sp, Dictionary<int, (string Name, string Email)> people, List<ReminderItem> created, CancellationToken ct)
    {
        var email = sp.GetRequiredService<IEmailSender>();
        var urls = sp.GetRequiredService<AppUrls>();
        foreach (var group in created.GroupBy(c => c.UserId))
        {
            if (!people.TryGetValue(group.Key, out var person) || string.IsNullOrWhiteSpace(person.Email)) continue;
            try
            {
                var (subject, html, text) = ReminderEmail.Build(person.Name, group.ToList(), urls.Absolute);
                await email.SendAsync(person.Email, subject, html, text, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Could not email reminders to {Email}", person.Email); }
        }
    }
}
