using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Tests;

public class ReminderEmailTests
{
    private static string Abs(string p) => "https://crm.example.com" + p;

    [Fact]
    public void Single_reminder_uses_its_title_as_the_subject()
    {
        var (subject, html, text) = ReminderEmail.Build("Michaela Smith", new[] { new ReminderItem(1, "Task due — Call Acme", "This task is due today.", "/Tasks") }, Abs);
        Assert.Equal("Reminder: Task due — Call Acme", subject);
        Assert.Contains("Hi Michaela,", text);
        Assert.Contains("https://crm.example.com/Tasks", html);
    }

    [Fact]
    public void Several_reminders_are_combined_into_one_email()
    {
        var items = new[] { new ReminderItem(1, "A", "a", "/Tasks"), new ReminderItem(1, "B", "b", "/Leads/Details/5") };
        var (subject, _, text) = ReminderEmail.Build("Sule", items, Abs);
        Assert.Equal("2 reminders from Uncovering Greatness CRM", subject);
        Assert.Contains("/Leads/Details/5", text);
    }

    [Fact]
    public void Html_in_titles_is_encoded()
    {
        var (_, html, _) = ReminderEmail.Build("Barry", new[] { new ReminderItem(1, "<script>alert(1)</script>", "x", "/Tasks") }, Abs);
        Assert.DoesNotContain("<script>", html);
    }
}

[Collection("crm")]
public class ReminderWorkerTests
{
    private readonly CrmFactory _f;
    public ReminderWorkerTests(CrmFactory f) => _f = f;

    private ReminderWorker NewWorker() =>
        new(_f.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReminderWorker>.Instance, Options.Create(new ReminderOptions()));

    [Fact]
    public async Task A_task_due_today_creates_one_notification_even_if_the_worker_runs_twice()
    {
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var title = "Call back " + Guid.NewGuid().ToString("N")[..8];
        var created = await rep.PostAsJsonAsync("/api/v1/tasks", new { title, dueAt = Helpers.Clock.LocalToday.AddHours(9) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await NewWorker().RunOnceAsync(CancellationToken.None);
        await NewWorker().RunOnceAsync(CancellationToken.None);

        var list = await rep.GetFromAsync("/api/v1/notifications?pageSize=100");
        var matches = list.GetProperty("items").EnumerateArray().Count(n => (n.GetProperty("title").GetString() ?? "").Contains(title));
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task A_calendar_entry_starting_soon_creates_a_reminder_for_its_owner()
    {
        var rep = await _f.ClientForAsync(CrmFactory.RepEmail);
        var title = "Demo " + Guid.NewGuid().ToString("N")[..8];
        var start = Helpers.Clock.LocalNow.AddMinutes(30);
        var created = await rep.PostAsJsonAsync("/api/v1/calendar", new { title, date = start.Date, time = start.TimeOfDay.ToString(@"hh\:mm\:ss") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await NewWorker().RunOnceAsync(CancellationToken.None);

        var list = await rep.GetFromAsync("/api/v1/notifications?pageSize=100");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), n => (n.GetProperty("title").GetString() ?? "").Contains("Starting soon") && (n.GetProperty("title").GetString() ?? "").Contains(title));
    }
}

internal static class HttpExtensions
{
    public static async Task<JsonElement> GetFromAsync(this HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }
}
