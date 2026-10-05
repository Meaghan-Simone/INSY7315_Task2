using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public interface ICampaignQueue
{
    void Enqueue(int campaignId);
    ChannelReader<int> Reader { get; }
}

public class CampaignQueue : ICampaignQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    public void Enqueue(int campaignId) => _channel.Writer.TryWrite(campaignId);
    public ChannelReader<int> Reader => _channel.Reader;
}

public class CampaignService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    private readonly ICampaignQueue _queue;

    public CampaignService(AppDbContext db, ICurrentUser me, IAuditService audit, ICampaignQueue queue)
    { _db = db; _me = me; _audit = audit; _queue = queue; }

    private static IQueryable<Lead> Audience(IQueryable<Lead> leads, CampaignTarget target, int? year, int? eventId)
    {
        leads = leads.Where(l => !l.OptedOut);
        return target switch
        {
            CampaignTarget.Year => leads.Where(l => l.RegistrationDate != null && l.RegistrationDate.Value.Year == year),
            CampaignTarget.Event => leads.Where(l => l.EventId == eventId),
            _ => leads
        };
    }

    public Task<int> AudienceSizeAsync(CampaignTarget target, int? year, int? eventId) =>
        Audience(_db.Leads.AsNoTracking(), target, year, eventId).CountAsync();

    public async Task<Result<PagedResult<CampaignListItem>>> ListAsync(string? q, CampaignStatus? status, int page, int pageSize)
    {
        if (!_me.IsAdmin) return Result<PagedResult<CampaignListItem>>.From(Result.Forbidden("Campaigns are available to admins."));
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        var query = _db.Campaigns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(c => c.Name.Contains(t) || c.Subject.Contains(t)); }
        if (status.HasValue) query = query.Where(c => c.Status == status);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CampaignListItem(c.Id, c.Name, c.Subject, c.Target, c.Status, c.RecipientCount, c.SentUtc, c.CreatedUtc)).ToListAsync();
        return Result<PagedResult<CampaignListItem>>.Success(new PagedResult<CampaignListItem>(items, page, pageSize, total));
    }

    public async Task<Result<CampaignDetail>> GetAsync(int id)
    {
        if (!_me.IsAdmin) return Result<CampaignDetail>.From(Result.Forbidden("Campaigns are available to admins."));
        var c = await _db.Campaigns.AsNoTracking().Include(x => x.TargetEvent).Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<CampaignDetail>.From(Result.NotFound("Campaign"));
        var failures = await _db.CampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == id && r.Status == RecipientStatus.Failed)
            .OrderBy(r => r.Id).Take(25)
            .Select(r => new CampaignFailure(r.Lead != null ? r.Lead.Email : "(deleted lead)", r.Error)).ToListAsync();
        return Result<CampaignDetail>.Success(new CampaignDetail
        {
            Failures = failures,
            Id = c.Id, Name = c.Name, Subject = c.Subject, Body = c.Body, Target = c.Target, TargetYear = c.TargetYear, TargetEventId = c.TargetEventId,
            TargetEventName = c.TargetEvent?.Name, Status = c.Status, CreatedByName = c.CreatedBy?.FullName ?? "a deleted user", CreatedUtc = c.CreatedUtc,
            SentUtc = c.SentUtc, RecipientCount = c.RecipientCount, DeliveredCount = c.DeliveredCount, OpenedCount = c.OpenedCount, FailedCount = c.FailedCount,
            AudienceSize = c.Status == CampaignStatus.Draft ? await AudienceSizeAsync(c.Target, c.TargetYear, c.TargetEventId) : c.RecipientCount
        });
    }

    private async Task<Result> ValidateAsync(CampaignInput i)
    {
        if (i.Target == CampaignTarget.Event && !await _db.Events.AnyAsync(e => e.Id == i.TargetEventId))
            return Result.Invalid(nameof(CampaignInput.TargetEventId), "Selected event does not exist.");
        return Result.Success();
    }

    public async Task<Result<int>> CreateAsync(CampaignInput input)
    {
        if (!_me.IsAdmin) return Result<int>.From(Result.Forbidden("Campaigns are available to admins."));
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return Result<int>.From(check);
        var c = new Campaign { CreatedById = _me.Id };
        Apply(c, input);
        _db.Campaigns.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CampaignCreated", "Campaign", c.Id, c.Name);
        return Result<int>.Success(c.Id);
    }

    private static void Apply(Campaign c, CampaignInput i)
    {
        c.Name = i.Name.Trim(); c.Subject = i.Subject.Trim(); c.Body = i.Body.Trim(); c.Target = i.Target;
        c.TargetYear = i.Target == CampaignTarget.Year ? i.TargetYear : null;
        c.TargetEventId = i.Target == CampaignTarget.Event ? i.TargetEventId : null;
    }

    public async Task<Result<CampaignInput>> GetInputAsync(int id)
    {
        if (!_me.IsAdmin) return Result<CampaignInput>.From(Result.Forbidden());
        var c = await _db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<CampaignInput>.From(Result.NotFound("Campaign"));
        if (c.Status != CampaignStatus.Draft) return Result<CampaignInput>.From(Result.Conflict("Only drafts can be edited."));
        return Result<CampaignInput>.Success(new CampaignInput { Name = c.Name, Subject = c.Subject, Body = c.Body, Target = c.Target, TargetYear = c.TargetYear, TargetEventId = c.TargetEventId });
    }

    public async Task<Result> UpdateAsync(int id, CampaignInput input)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Campaigns are available to admins.");
        var c = await _db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Campaign");
        if (c.Status != CampaignStatus.Draft) return Result.Conflict("Only drafts can be edited.");
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return check;
        Apply(c, input);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CampaignUpdated", "Campaign", id, c.Name);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Campaigns are available to admins.");
        var c = await _db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Campaign");
        if (c.Status == CampaignStatus.Sending) return Result.Conflict("A campaign that is sending cannot be deleted.");
        _db.Campaigns.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CampaignDeleted", "Campaign", id, c.Name);
        return Result.Success();
    }

    /// <summary>Snapshots the audience (opted-out leads excluded) and hands delivery to the background sender.</summary>
    public async Task<Result<int>> SendAsync(int id)
    {
        if (!_me.IsAdmin) return Result<int>.From(Result.Forbidden("Campaigns are available to admins."));
        var c = await _db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<int>.From(Result.NotFound("Campaign"));
        if (c.Status != CampaignStatus.Draft) return Result<int>.From(Result.Conflict("This campaign has already been sent."));

        var leadIds = await Audience(_db.Leads.AsNoTracking(), c.Target, c.TargetYear, c.TargetEventId).Select(l => l.Id).ToListAsync();
        if (leadIds.Count == 0) return Result<int>.From(Result.Conflict("No reachable leads match this audience."));

        foreach (var leadId in leadIds) c.Recipients.Add(new CampaignRecipient { LeadId = leadId });
        c.Status = CampaignStatus.Sending;
        c.RecipientCount = leadIds.Count;
        await _db.SaveChangesAsync();
        _queue.Enqueue(c.Id);
        await _audit.LogAsync("CampaignSent", "Campaign", c.Id, $"{c.Name} → {leadIds.Count} recipients");
        return Result<int>.Success(leadIds.Count);
    }

    public async Task<Result<int>> DuplicateAsync(int id)
    {
        if (!_me.IsAdmin) return Result<int>.From(Result.Forbidden());
        var c = await _db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<int>.From(Result.NotFound("Campaign"));
        var copy = new Campaign
        {
            Name = c.Name + " (copy)", Subject = c.Subject, Body = c.Body, Target = c.Target, TargetYear = c.TargetYear,
            TargetEventId = c.TargetEventId, CreatedById = _me.Id
        };
        _db.Campaigns.Add(copy);
        await _db.SaveChangesAsync();
        return Result<int>.Success(copy.Id);
    }
}

public static class CampaignEmail
{
    /// <summary>Personalises the body. All user-supplied text is HTML-encoded before being placed in the HTML part.</summary>
    public static (string Html, string Text) Build(Campaign c, Lead lead, string openUrl, string unsubscribeUrl)
    {
        string Fill(string s, Func<string, string> enc) => s
            .Replace("{{FirstName}}", enc(lead.FirstName), StringComparison.OrdinalIgnoreCase)
            .Replace("{{FullName}}", enc(lead.FullName), StringComparison.OrdinalIgnoreCase)
            .Replace("{{Company}}", enc(lead.Company?.Name ?? ""), StringComparison.OrdinalIgnoreCase);

        var text = Fill(c.Body, s => s) + $"\n\n--\nYou are receiving this because you registered with Uncovering Greatness.\nUnsubscribe: {unsubscribeUrl}";
        var htmlBody = WebUtility.HtmlEncode(c.Body).Replace("\r\n", "\n").Replace("\n", "<br>");
        htmlBody = Fill(htmlBody, s => WebUtility.HtmlEncode(s));
        var html = $"<div style=\"font-family:Arial,sans-serif;font-size:15px;line-height:1.6;color:#222;max-width:560px\">{htmlBody}" +
                   $"<hr style=\"border:0;border-top:1px solid #ddd;margin:28px 0 12px\"><p style=\"font-size:12px;color:#777\">You are receiving this because you registered with Uncovering Greatness. " +
                   $"<a href=\"{WebUtility.HtmlEncode(unsubscribeUrl)}\">Unsubscribe</a></p>" +
                   $"<img src=\"{WebUtility.HtmlEncode(openUrl)}\" width=\"1\" height=\"1\" alt=\"\" style=\"display:none\"></div>";
        return (html, text);
    }
}

/// <summary>Delivers queued campaigns in the background so a large send never blocks a web request.</summary>
public class CampaignSenderWorker : BackgroundService
{
    private readonly ICampaignQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CampaignSenderWorker> _log;

    public CampaignSenderWorker(ICampaignQueue queue, IServiceScopeFactory scopes, ILogger<CampaignSenderWorker> log)
    { _queue = queue; _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resume anything interrupted by a restart.
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var id in await db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Sending).Select(c => c.Id).ToListAsync(stoppingToken))
                _queue.Enqueue(id);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not resume in-flight campaigns"); }

        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DeliverAsync(id, stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Campaign {Id} delivery failed", id); }
        }
    }

    private async Task DeliverAsync(int campaignId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var urls = scope.ServiceProvider.GetRequiredService<AppUrls>();

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null || campaign.Status != CampaignStatus.Sending) return;

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.CampaignRecipients.Include(r => r.Lead).ThenInclude(l => l!.Company)
                .Where(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Pending).OrderBy(r => r.Id).Take(50).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var r in batch)
            {
                try
                {
                    if (r.Lead is null || r.Lead.OptedOut) { r.Status = RecipientStatus.Failed; r.Error = "Recipient opted out"; continue; }
                    var (html, text) = CampaignEmail.Build(campaign, r.Lead, urls.Absolute("/t/o/" + r.TrackingToken), urls.Absolute("/unsubscribe/" + r.Lead.UnsubscribeToken));
                    await email.SendAsync(r.Lead.Email, campaign.Subject, html, text, ct);
                    r.Status = RecipientStatus.Delivered; r.SentUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    r.Status = RecipientStatus.Failed;
                    var msg = ex.Message;
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) msg += " | " + inner.Message;
                    r.Error = msg.Length > 480 ? msg[..480] : msg;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        campaign.DeliveredCount = await db.CampaignRecipients.CountAsync(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Delivered, ct);
        campaign.FailedCount = await db.CampaignRecipients.CountAsync(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Failed, ct);
        campaign.Status = CampaignStatus.Sent;
        campaign.SentUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Campaign {Id} finished: {Delivered} delivered, {Failed} failed", campaignId, campaign.DeliveredCount, campaign.FailedCount);
    }
}
