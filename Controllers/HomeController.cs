using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class HomeController : AppController
{
    private readonly DashboardService _dash;
    private readonly SearchService _search;
    public HomeController(DashboardService dash, SearchService search) { _dash = dash; _search = search; }

    public async Task<IActionResult> Index() => View(await _dash.GetAsync());

    [HttpGet]
    public async Task<IActionResult> Search(string? q) => View(await _search.SearchAsync(q));

    [AllowAnonymous, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Error() => View();

    [AllowAnonymous]
    public IActionResult Status(int code)
    {
        ViewData["Code"] = code;
        Response.StatusCode = code;
        return View(code == 404 ? "NotFound" : "Error");
    }
}
