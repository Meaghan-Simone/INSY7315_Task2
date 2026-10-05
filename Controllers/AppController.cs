using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public abstract class AppController : Controller
{
    protected void AddErrors(Result r)
    {
        if (r.Errors is { Count: > 0 }) foreach (var (k, v) in r.Errors) foreach (var m in v) ModelState.AddModelError(k, m);
        else if (r.Error != null) ModelState.AddModelError(string.Empty, r.Error);
    }

    /// <summary>Non-form failures (not found / forbidden) become a friendly page; form failures re-render with errors.</summary>
    protected IActionResult? Denied(Result r) => r.Status switch
    {
        ResultStatus.NotFound => NotFound(),
        ResultStatus.Forbidden => RedirectToAction("AccessDenied", "Account"),
        _ => null
    };

    protected void Flash(string message, string kind = "success")
    {
        TempData["flash"] = message;
        TempData["flashKind"] = kind;
    }

    protected IActionResult Ajax(Result r, object? data = null) =>
        r.Succeeded ? Json(new { ok = true, data }) : StatusCode(r.Status switch
        {
            ResultStatus.NotFound => 404, ResultStatus.Forbidden => 403, ResultStatus.Conflict => 409, _ => 400
        }, new { ok = false, error = r.Error });
}
