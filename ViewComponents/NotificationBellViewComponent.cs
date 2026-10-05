using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.ViewComponents;

public class NotificationBellViewComponent : ViewComponent
{
    private readonly INotificationService _notifications;
    public NotificationBellViewComponent(INotificationService notifications) => _notifications = notifications;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var (unread, recent) = await _notifications.BellAsync();
        return View((unread, recent));
    }
}
