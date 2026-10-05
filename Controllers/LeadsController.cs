using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class LeadsController : AppController
{
    private readonly LeadService _leads;
    private readonly CompanyService _companies;
    private readonly EventService _events;
    private readonly UserService _users;

    public LeadsController(LeadService leads, CompanyService companies, EventService events, UserService users)
    { _leads = leads; _companies = companies; _events = events; _users = users; }

    /// <summary>After handing a lead to someone else a rep can no longer open it, so send them to their list instead of a 404.</summary>
    private async Task<IActionResult> ToLeadOrListAsync(int id) =>
        (await _leads.GetAsync(id)).Succeeded ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Index));

    private async Task LoadLookupsAsync()
    {
        ViewBag.Companies = await _companies.OptionsAsync();
        ViewBag.Events = await _events.OptionsAsync();
        ViewBag.Users = (await _users.OptionsAsync()).Where(u => u.Role != UserRole.Staff).ToList();
    }

    public async Task<IActionResult> Index([FromQuery] LeadQuery query)
    {
        ViewBag.Result = await _leads.SearchAsync(query);
        ViewBag.Tabs = await _leads.TabCountsAsync();
        ViewBag.Query = query;
        await LoadLookupsAsync();
        return View();
    }

    public async Task<IActionResult> Pipeline(int? eventId, int? assignedToId, string? q)
    {
        ViewBag.Query = q; ViewBag.EventId = eventId; ViewBag.AssignedToId = assignedToId;
        await LoadLookupsAsync();
        return View(await _leads.PipelineAsync(eventId, assignedToId, q));
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _leads.GetAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Create(int? companyId, int? eventId)
    {
        await LoadLookupsAsync();
        return View(new LeadInput { CompanyId = companyId, EventId = eventId, RegistrationDate = Helpers.Clock.LocalToday });
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Create(LeadInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _leads.CreateAsync(input);
            if (r.Succeeded) { Flash("Lead created."); return await ToLeadOrListAsync(r.Value); }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _leads.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.LeadId = id;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Edit(int id, LeadInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _leads.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Lead updated."); return await ToLeadOrListAsync(id); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.LeadId = id;
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpPost, Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _leads.DeleteAsync(id);
        if (!r.Succeeded) { Flash(r.Error ?? "Could not delete.", "error"); return RedirectToAction(nameof(Details), new { id }); }
        Flash("Lead deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> AddNote(int id, NoteInput input)
    {
        if (!ModelState.IsValid) { Flash(ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage, "error"); return RedirectToAction(nameof(Details), new { id }); }
        var r = await _leads.AddNoteAsync(id, input.Body);
        Flash(r.Succeeded ? "Note added." : r.Error ?? "Could not add note.", r.Succeeded ? "success" : "error");
        return Redirect(Url.Action(nameof(Details), new { id }) + "#notes");
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> LogCall(int id, CallInput input)
    {
        if (!ModelState.IsValid) { Flash(ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage, "error"); return RedirectToAction(nameof(Details), new { id }); }
        var r = await _leads.LogCallAsync(id, input);
        Flash(r.Succeeded ? "Call logged." : r.Error ?? "Could not log call.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Assign(int id, int? userId)
    {
        var r = await _leads.AssignAsync(id, userId);
        Flash(r.Succeeded ? "Lead reassigned." : r.Error ?? "Could not reassign.", r.Succeeded ? "success" : "error");
        return await ToLeadOrListAsync(id);
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> SetStage(int id, LeadStage stage)
    {
        var r = await _leads.ChangeStageAsync(id, stage);
        Flash(r.Succeeded ? "Stage updated." : r.Error ?? "Could not change stage.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- AJAX (antiforgery token sent in the RequestVerificationToken header)
    [HttpPost]
    public async Task<IActionResult> ToggleStar(int id)
    {
        var r = await _leads.ToggleStarAsync(id);
        return Ajax(r, new { starred = r.Value });
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> MoveStage(int id, [FromBody] StageInput input) => Ajax(await _leads.ChangeStageAsync(id, input.Stage));

    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] LeadQuery query) =>
        File(await _leads.ExportCsvAsync(query), "text/csv; charset=utf-8", $"leads-{Helpers.Clock.LocalToday:yyyyMMdd}.csv");

    [HttpPost, Authorize(Policy = "CanWrite")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        if (file is null || file.Length == 0) { Flash("Choose a CSV file to import.", "error"); return RedirectToAction(nameof(Index)); }
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)) { Flash("Only .csv files are accepted.", "error"); return RedirectToAction(nameof(Index)); }
        await using var s = file.OpenReadStream();
        var r = await _leads.ImportCsvAsync(s);
        if (!r.Succeeded) { Flash(r.Error ?? "Import failed.", "error"); return RedirectToAction(nameof(Index)); }
        TempData["importErrors"] = string.Join("\n", r.Value!.Errors);
        Flash($"Import finished: {r.Value.Created} created, {r.Value.Skipped} skipped.", r.Value.Created > 0 ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }
}
