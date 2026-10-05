using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

[Authorize(Policy = "CanWrite")]
public class TasksController : AppController
{
    private readonly TaskService _tasks;
    private readonly UserService _users;
    private readonly LeadService _leads;
    public TasksController(TaskService tasks, UserService users, LeadService leads) { _tasks = tasks; _users = users; _leads = leads; }

    private async Task LoadLookupsAsync()
    {
        ViewBag.Users = (await _users.OptionsAsync()).Where(u => u.Role != UserRole.Staff).ToList();
        var leads = await _leads.SearchAsync(new LeadQuery(PageSize: 200, Sort: "name"));
        ViewBag.Leads = leads.Items.Select(l => new Option(l.Id, l.FullName)).ToList();
    }

    public async Task<IActionResult> Index(int? assignedToId)
    {
        var r = await _tasks.BoardAsync(assignedToId);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.AssignedToId = assignedToId;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? leadId)
    {
        await LoadLookupsAsync();
        return View(new TaskInput { LeadId = leadId, DueAt = Helpers.Clock.LocalToday.AddHours(17) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(TaskInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _tasks.CreateAsync(input);
            if (r.Succeeded) { Flash("Task added."); return RedirectToAction(nameof(Index)); }
            AddErrors(r);
        }
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _tasks.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.TaskId = id;
        await LoadLookupsAsync();
        return View(r.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, TaskInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _tasks.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Task updated."); return RedirectToAction(nameof(Index)); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.TaskId = id;
        await LoadLookupsAsync();
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Toggle(int id, bool completed)
    {
        var r = await _tasks.SetCompletedAsync(id, completed);
        return Ajax(r, new { completed });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _tasks.DeleteAsync(id);
        Flash(r.Succeeded ? "Task deleted." : r.Error ?? "Could not delete task.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }
}
