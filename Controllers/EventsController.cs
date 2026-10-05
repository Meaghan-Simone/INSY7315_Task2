using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class EventsController : AppController
{
    private readonly EventService _events;
    public EventsController(EventService events) => _events = events;

    public async Task<IActionResult> Index([FromQuery] EventQuery query)
    {
        ViewBag.Query = query;
        ViewBag.Years = await _events.YearsAsync();
        return View(await _events.SearchAsync(query));
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _events.GetAsync(id);
        return r.Succeeded ? View(r.Value) : Denied(r) ?? NotFound();
    }

    [Authorize(Policy = "AdminOnly")]
    public IActionResult Create() => View(new EventInput { Year = DateTime.Today.Year });

    [HttpPost, Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Create(EventInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _events.CreateAsync(input);
            if (r.Succeeded) { Flash("Event created."); return RedirectToAction(nameof(Details), new { id = r.Value }); }
            AddErrors(r);
        }
        return View(input);
    }

    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _events.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.EventId = id;
        return View(r.Value);
    }

    [HttpPost, Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Edit(int id, EventInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _events.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Event updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.EventId = id;
        return View(input);
    }

    [HttpPost, Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _events.DeleteAsync(id);
        if (!r.Succeeded) { Flash(r.Error ?? "Could not delete.", "error"); return RedirectToAction(nameof(Details), new { id }); }
        Flash("Event deleted.");
        return RedirectToAction(nameof(Index));
    }
}
