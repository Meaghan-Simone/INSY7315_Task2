using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

public class NotificationsController : AppController
{
    private readonly INotificationService _notifications;
    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    public async Task<IActionResult> Index(string? filter, int page = 1)
    {
        ViewBag.Filter = filter;
        ViewBag.Unread = (await _notifications.BellAsync()).Unread;
        return View(await _notifications.ListAsync(filter, page, 25));
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead(int id) => Ajax(await _notifications.MarkReadAsync(id));

    [HttpPost]
    public async Task<IActionResult> MarkAllRead(string? returnTo)
    {
        await _notifications.MarkAllReadAsync();
        if (Request.Headers.XRequestedWith == "XMLHttpRequest") return Json(new { ok = true });
        return Url.IsLocalUrl(returnTo) ? LocalRedirect(returnTo!) : RedirectToAction(nameof(Index));
    }
}
