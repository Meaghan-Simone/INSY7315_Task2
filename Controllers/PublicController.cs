using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

/// <summary>Anonymous endpoints used by recipients of campaign emails: open tracking pixel and one-click unsubscribe.</summary>
[AllowAnonymous, EnableRateLimiting("public"), IgnoreAntiforgeryToken]
public class PublicController : Controller
{
    private static readonly byte[] Pixel = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private readonly AppDbContext _db;
    public PublicController(AppDbContext db) => _db = db;

    [HttpGet("/t/o/{token}")]
    public async Task<IActionResult> Open(string token)
    {
        if (token.Length is > 0 and <= 64)
        {
            var r = await _db.CampaignRecipients.Include(x => x.Campaign).FirstOrDefaultAsync(x => x.TrackingToken == token);
            if (r != null && r.OpenedUtc == null)
            {
                r.OpenedUtc = DateTime.UtcNow;
                if (r.Campaign != null) r.Campaign.OpenedCount++;
                await _db.SaveChangesAsync();
            }
        }
        Response.Headers.CacheControl = "no-store";
        return File(Pixel, "image/gif");
    }

    [HttpGet("/unsubscribe/{token}")]
    public async Task<IActionResult> Unsubscribe(string token)
    {
        var lead = token.Length is > 0 and <= 64 ? await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.UnsubscribeToken == token) : null;
        if (lead is null) return View("UnsubscribeResult", false);
        ViewData["Token"] = token;
        return View("UnsubscribeConfirm");
    }

    [HttpPost("/unsubscribe/{token}")]
    public async Task<IActionResult> UnsubscribeConfirm(string token)
    {
        var lead = token.Length is > 0 and <= 64 ? await _db.Leads.FirstOrDefaultAsync(l => l.UnsubscribeToken == token) : null;
        if (lead is null) return View("UnsubscribeResult", false);
        if (!lead.OptedOut)
        {
            lead.OptedOut = true;
            _db.LeadActivities.Add(new LeadActivity { LeadId = lead.Id, Type = ActivityType.Updated, Title = "Unsubscribed", Detail = "Opted out of marketing emails via unsubscribe link" });
            await _db.SaveChangesAsync();
        }
        return View("UnsubscribeResult", true);
    }
}
