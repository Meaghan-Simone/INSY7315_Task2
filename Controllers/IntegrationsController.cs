using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

[Authorize(Policy = "AdminOnly")]
public class IntegrationsController : AppController
{
    private readonly FormConnectionService _forms;
    private readonly EventService _events;
    private readonly UserService _users;
    public IntegrationsController(FormConnectionService forms, EventService events, UserService users) { _forms = forms; _events = events; _users = users; }

    private async Task LoadLookupsAsync()
    {
        ViewBag.Events = await _events.OptionsAsync();
        ViewBag.Users = (await _users.OptionsAsync()).Where(u => u.Role != UserRole.Staff).ToList();
    }

    public async Task<IActionResult> Index()
    {
        var r = await _forms.ListAsync();
        return r.Succeeded ? View(r.Value) : Denied(r) ?? NotFound();
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _forms.GetAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        return View(r.Value);
    }

    [HttpGet]
    public async Task<IActionResult> Create() { await LoadLookupsAsync(); return View(new FormConnectionInput()); }

    [HttpPost]
    public async Task<IActionResult> Create(FormConnectionInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _forms.CreateAsync(input);
            if (r.Succeeded) { Flash("Form connected. Copy the webhook URL below into your form tool."); return RedirectToAction(nameof(Details), new { id = r.Value }); }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _forms.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        var detail = await _forms.GetAsync(id);
        ViewBag.ConnectionId = id; ViewBag.HasSecret = detail.Value?.HasSigningSecret ?? false;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, FormConnectionInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _forms.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Connection updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        var detail = await _forms.GetAsync(id);
        ViewBag.ConnectionId = id; ViewBag.HasSecret = detail.Value?.HasSigningSecret ?? false;
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _forms.DeleteAsync(id);
        Flash(r.Succeeded ? "Connection deleted. Leads it created were kept." : r.Error ?? "Could not delete.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Rotate(int id)
    {
        var r = await _forms.RotateTokenAsync(id);
        Flash(r.Succeeded ? "New webhook URL issued. Update it in your form tool: the old one no longer works." : r.Error ?? "Could not rotate.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Preview(int id, FormPreviewInput input)
    {
        var detail = await _forms.GetAsync(id);
        if (!detail.Succeeded) return Denied(detail) ?? NotFound();
        if (ModelState.IsValid)
        {
            var p = await _forms.PreviewAsync(id, input.Payload);
            ViewBag.Preview = p.Value;
        }
        ViewBag.SamplePayload = input.Payload;
        return View("Details", detail.Value);
    }
}
