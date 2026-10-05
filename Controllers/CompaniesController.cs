using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class CompaniesController : AppController
{
    private readonly CompanyService _companies;
    public CompaniesController(CompanyService companies) => _companies = companies;

    public async Task<IActionResult> Index([FromQuery] CompanyQuery query)
    {
        ViewBag.Query = query;
        ViewBag.Industries = await _companies.IndustriesAsync();
        return View(await _companies.SearchAsync(query));
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await _companies.GetAsync(id);
        return r.Succeeded ? View(r.Value) : Denied(r) ?? NotFound();
    }

    [Authorize(Policy = "CanWrite")]
    public IActionResult Create() => View(new CompanyInput());

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Create(CompanyInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _companies.CreateAsync(input);
            if (r.Succeeded) { Flash("Company created."); return RedirectToAction(nameof(Details), new { id = r.Value }); }
            AddErrors(r);
        }
        return View(input);
    }

    [Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _companies.GetInputAsync(id);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.CompanyId = id;
        return View(r.Value);
    }

    [HttpPost, Authorize(Policy = "CanWrite")]
    public async Task<IActionResult> Edit(int id, CompanyInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _companies.UpdateAsync(id, input);
            if (r.Succeeded) { Flash("Company updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        ViewBag.CompanyId = id;
        return View(input);
    }

    [HttpPost, Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _companies.DeleteAsync(id);
        if (!r.Succeeded) { Flash(r.Error ?? "Could not delete.", "error"); return RedirectToAction(nameof(Details), new { id }); }
        Flash("Company deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] CompanyQuery query) =>
        File(await _companies.ExportCsvAsync(query), "text/csv; charset=utf-8", $"companies-{Helpers.Clock.LocalToday:yyyyMMdd}.csv");
}
