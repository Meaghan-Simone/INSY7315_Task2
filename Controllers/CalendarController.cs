using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class CalendarController : AppController
{
    private readonly CalendarService _calendar;
    private readonly UserService _users;
    private readonly LeadService _leads;
    private readonly CompanyService _companies;
    private readonly EventService _events;

    public CalendarController(CalendarService calendar, UserService users, LeadService leads, CompanyService companies, EventService events)
    { _calendar = calendar; _users = users; _leads = leads; _companies = companies; _events = events; }

    private async Task LoadLookupsAsync()
    {
        var me = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        ViewBag.Users = (await _users.OptionsAsync()).Where(u => u.Id.ToString() != me).ToList();
        ViewBag.Leads = (await _leads.SearchAsync(new LeadQuery(PageSize: 200, Sort: "name"))).Items.Select(l => new Option(l.Id, l.FullName)).ToList();
        ViewBag.Companies = await _companies.OptionsAsync();
        ViewBag.Events = await _events.OptionsAsync();
    }

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var today = Helpers.Clock.LocalToday;
        var y = year is >= 2000 and <= 2100 ? year.Value : today.Year;
        var m = month is >= 1 and <= 12 ? month.Value : today.Month;
        return View(await _calendar.MonthAsync(y, m));
    }

    [HttpGet]
    public async Task<IActionResult> Create(DateTime? date)
    {
        await LoadLookupsAsync();
        return View(new CalendarInput { Date = (date ?? Helpers.Clock.LocalToday).Date, Time = new TimeSpan(11, 0, 0) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CalendarInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _calendar.CreateAsync(input);
            if (r.Succeeded) { Flash("Event created."); return RedirectToAction(nameof(Index), new { year = input.Date!.Value.Year, month = input.Date.Value.Month }); }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _calendar.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.EventId = id;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CalendarInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _calendar.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Event updated."); return RedirectToAction(nameof(Index), new { year = input.Date!.Value.Year, month = input.Date.Value.Month }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.EventId = id;
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, int? year, int? month)
    {
        var r = await _calendar.DeleteAsync(id);
        Flash(r.Succeeded ? "Event deleted." : r.Error ?? "Could not delete event.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index), new { year, month });
    }
}
