using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

// ------------------------------------------------------------------ Audit

public interface IAuditService
{
    Task LogAsync(string action, string? entity = null, object? entityId = null, string? details = null, int? userId = null, string? userName = null);
    Task<PagedResult<AuditItem>> SearchAsync(string? q, string? action, int page, int pageSize);
    Task<List<string>> ActionsAsync();
}

public class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    public AuditService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    public async Task LogAsync(string action, string? entity = null, object? entityId = null, string? details = null, int? userId = null, string? userName = null)
    {
        var uid = userId ?? (_me.IsAuthenticated ? _me.Id : null as int?);
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = uid,
            UserName = userName ?? (_me.IsAuthenticated ? _me.Name : null),
            Action = action,
            Entity = entity,
            EntityId = entityId?.ToString(),
            Details = details is { Length: > 1000 } ? details[..1000] : details,
            IpAddress = _me.IpAddress
        });
        await _db.SaveChangesAsync();
    }

    public async Task<PagedResult<AuditItem>> SearchAsync(string? q, string? action, int page, int pageSize)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 200);
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(a => (a.UserName != null && a.UserName.Contains(t)) || (a.Details != null && a.Details.Contains(t)) ||
                                     (a.Entity != null && a.Entity.Contains(t)) || (a.IpAddress != null && a.IpAddress.Contains(t)));
        }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditItem(a.Id, a.UserName, a.Action, a.Entity, a.EntityId, a.Details, a.IpAddress, a.CreatedUtc)).ToListAsync();
        return new PagedResult<AuditItem>(items, page, pageSize, total);
    }

    public Task<List<string>> ActionsAsync() => _db.AuditLogs.AsNoTracking().Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
}

// ------------------------------------------------------------------ Notifications

public interface INotificationService
{
    /// <summary>Creates a notification. Returns false when it was skipped because the same dedupe key already exists for that user.</summary>
    Task<bool> NotifyAsync(int userId, NotificationType type, string title, string? body, string? link, string? dedupeKey = null);
    Task NotifyManyAsync(IEnumerable<int> userIds, NotificationType type, string title, string? body, string? link, string? dedupeKey = null);
    Task<PagedResult<NotificationDto>> ListAsync(string? filter, int page, int pageSize);
    Task<(int Unread, List<NotificationDto> Recent)> BellAsync();
    Task<Result> MarkReadAsync(int id);
    Task<int> MarkAllReadAsync();
}

public class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    public NotificationService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    public async Task<bool> NotifyAsync(int userId, NotificationType type, string title, string? body, string? link, string? dedupeKey = null)
    {
        if (dedupeKey != null && await _db.Notifications.AnyAsync(n => n.UserId == userId && n.DedupeKey == dedupeKey)) return false;
        var n = new Notification { UserId = userId, Type = type, Title = title, Body = body, LinkUrl = link, DedupeKey = dedupeKey };
        _db.Notifications.Add(n);
        try { await _db.SaveChangesAsync(); return true; }
        catch (DbUpdateException) when (dedupeKey != null) { _db.Entry(n).State = EntityState.Detached; return false; }
    }

    public async Task NotifyManyAsync(IEnumerable<int> userIds, NotificationType type, string title, string? body, string? link, string? dedupeKey = null)
    {
        foreach (var id in userIds.Distinct()) await NotifyAsync(id, type, title, body, link, dedupeKey);
    }

    public async Task<PagedResult<NotificationDto>> ListAsync(string? filter, int page, int pageSize)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        var uid = _me.Id;
        var q = _db.Notifications.AsNoTracking().Where(n => n.UserId == uid);
        q = filter switch
        {
            "unread" => q.Where(n => !n.IsRead),
            "followups" => q.Where(n => n.Type == NotificationType.FollowUp || n.Type == NotificationType.TaskDue || n.Type == NotificationType.CalendarReminder),
            "deals" => q.Where(n => n.Type == NotificationType.DealWon || n.Type == NotificationType.LeadAssigned),
            _ => q
        };
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(n => n.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.LinkUrl, n.IsRead, n.CreatedUtc)).ToListAsync();
        return new PagedResult<NotificationDto>(items, page, pageSize, total);
    }

    public async Task<(int Unread, List<NotificationDto> Recent)> BellAsync()
    {
        var uid = _me.Id;
        var unread = await _db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead);
        var recent = await _db.Notifications.AsNoTracking().Where(n => n.UserId == uid).OrderByDescending(n => n.Id).Take(5)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.LinkUrl, n.IsRead, n.CreatedUtc)).ToListAsync();
        return (unread, recent);
    }

    public async Task<Result> MarkReadAsync(int id)
    {
        var uid = _me.Id;
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
        if (n is null) return Result.NotFound("Notification");
        n.IsRead = true;
        await _db.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<int> MarkAllReadAsync()
    {
        var uid = _me.Id;
        var unread = await _db.Notifications.Where(n => n.UserId == uid && !n.IsRead).ToListAsync();
        foreach (var n in unread) n.IsRead = true;
        await _db.SaveChangesAsync();
        return unread.Count;
    }
}

// ------------------------------------------------------------------ Email + URLs

public class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@uncoveringgreatness.co.za";
    public string FromName { get; set; } = "Uncovering Greatness";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

public interface IEmailSender
{
    bool IsSimulated { get; }
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}

public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _o;
    public SmtpEmailSender(Microsoft.Extensions.Options.IOptions<EmailOptions> o) => _o = o.Value;
    public bool IsSimulated => false;

    public async Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        if (_o.Port == 465)
            throw new InvalidOperationException("Port 465 (implicit SSL) is not supported by the built-in mail client. Use port 587 with UseSsl=true (STARTTLS), or ask your mail provider for their 587 settings.");
        using var client = new System.Net.Mail.SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.UseSsl, Timeout = 30000 };
        if (!string.IsNullOrEmpty(_o.Username)) client.Credentials = new System.Net.NetworkCredential(_o.Username, _o.Password);
        using var msg = new System.Net.Mail.MailMessage { From = new System.Net.Mail.MailAddress(_o.FromAddress, _o.FromName), Subject = subject };
        msg.To.Add(to);
        msg.Body = textBody;
        msg.AlternateViews.Add(System.Net.Mail.AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));
        await client.SendMailAsync(msg, ct);
    }
}

/// <summary>Used when no SMTP host is configured (development): nothing leaves the machine; the message is logged.</summary>
public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _log;
    public LoggingEmailSender(ILogger<LoggingEmailSender> log) => _log = log;
    public bool IsSimulated => true;

    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        _log.LogInformation("[SIMULATED EMAIL] To: {To} | Subject: {Subject}\n{Body}", to, subject, textBody);
        return Task.CompletedTask;
    }
}

public class AppUrls
{
    private readonly IConfiguration _cfg;
    private readonly IHttpContextAccessor _http;
    public AppUrls(IConfiguration cfg, IHttpContextAccessor http) { _cfg = cfg; _http = http; }

    /// <summary>Absolute URL for links in emails. Uses App:PublicBaseUrl so a spoofed Host header can never poison a link.</summary>
    public string Absolute(string path)
    {
        var baseUrl = _cfg["App:PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            var req = _http.HttpContext?.Request;
            baseUrl = req is null ? "http://localhost:5080" : $"{req.Scheme}://{req.Host}";
        }
        return baseUrl + (path.StartsWith('/') ? path : "/" + path);
    }
}
