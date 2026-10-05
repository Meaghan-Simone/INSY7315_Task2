using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

[Authorize(Policy = "AdminOnly")]
public class CampaignsController : AppController
{
    private readonly CampaignService _campaigns;
    private readonly EventService _events;
    private readonly IEmailSender _email;
    public CampaignsController(CampaignService campaigns, EventService events, IEmailSender email) { _campaigns = campaigns; _events = events; _email = email; }

    private async Task LoadLookupsAsync()
    {
        ViewBag.Events = await _events.OptionsAsync();
        ViewBag.Years = await _events.YearsAsync();
        ViewBag.Simulated = _email.IsSimulated;
    }

    public async Task<IActionResult> Index(string? q, CampaignStatus? status, int page = 1)
    {
        var r = await _campaigns.ListAsync(q, status, page, 20);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.Q = q; ViewBag.Status = status;
        return View(r.Value);
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _campaigns.GetAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.Simulated = _email.IsSimulated;
        return View(r.Value);
    }

    [HttpGet]
    public async Task<IActionResult> Create() { await LoadLookupsAsync(); return View(new CampaignInput()); }

    [HttpPost]
    public async Task<IActionResult> Create(CampaignInput input, string? submit)
    {
        if (ModelState.IsValid)
        {
            var r = await _campaigns.CreateAsync(input);
            if (r.Succeeded)
            {
                if (submit == "send") return await SendInternal(r.Value);
                Flash("Draft saved."); return RedirectToAction(nameof(Details), new { id = r.Value });
            }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _campaigns.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.CampaignId = id;
        await LoadLookupsAsync();
        return View("Create", r.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CampaignInput input, string? submit)
    {
        if (ModelState.IsValid)
        {
            var r = await _campaigns.UpdateAsync(id, input);
            if (r.Succeeded)
            {
                if (submit == "send") return await SendInternal(id);
                Flash("Draft updated."); return RedirectToAction(nameof(Details), new { id });
            }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.CampaignId = id;
        await LoadLookupsAsync();
        return View("Create", input);
    }

    private async Task<IActionResult> SendInternal(int id)
    {
        var r = await _campaigns.SendAsync(id);
        Flash(r.Succeeded ? $"Campaign is sending to {r.Value} recipients." : r.Error ?? "Could not send campaign.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost] public Task<IActionResult> Send(int id) => SendInternal(id);

    [HttpPost]
    public async Task<IActionResult> Duplicate(int id)
    {
        var r = await _campaigns.DuplicateAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        Flash("Campaign duplicated as a draft.");
        return RedirectToAction(nameof(Details), new { id = r.Value });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _campaigns.DeleteAsync(id);
        Flash(r.Succeeded ? "Campaign deleted." : r.Error ?? "Could not delete campaign.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Audience(CampaignTarget target, int? year, int? eventId) =>
        Json(new { count = await _campaigns.AudienceSizeAsync(target, year, eventId) });
}
