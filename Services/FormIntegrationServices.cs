using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

internal static class FormSecrets
{
    public const string Purpose = "UncoveringGreatnessCRM.FormSigningSecret.v1";

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? Unprotect(IDataProtector p, string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return p.Unprotect(protectedValue); } catch { return null; }
    }

    /// <summary>Tally: base64(HMAC-SHA256(secret, raw body)) in the Tally-Signature header. The same scheme is accepted in X-Signature.</summary>
    public static bool Verify(byte[] body, string secret, string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Encoding.UTF8.GetBytes(Convert.ToBase64String(h.ComputeHash(body)));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(header.Trim()));
    }
}

/// <summary>Admin management of form connections.</summary>
public class FormConnectionService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    private readonly IDataProtector _secrets;
    private readonly AppUrls _urls;

    public FormConnectionService(AppDbContext db, ICurrentUser me, IAuditService audit, IDataProtectionProvider dp, AppUrls urls)
    { _db = db; _me = me; _audit = audit; _urls = urls; _secrets = dp.CreateProtector(FormSecrets.Purpose); }

    private string UrlFor(string token) => _urls.Absolute("/integrations/forms/" + token);

    public async Task<Result<List<FormConnectionListItem>>> ListAsync()
    {
        if (!_me.IsAdmin) return Result<List<FormConnectionListItem>>.From(Result.Forbidden());
        var items = await _db.FormConnections.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new FormConnectionListItem(c.Id, c.Name, c.Provider, c.IsActive, c.Event != null ? c.Event.Name : null,
                c.TotalReceived, c.TotalCreated, c.TotalDuplicate, c.TotalFailed, c.LastReceivedUtc)).ToListAsync();
        return Result<List<FormConnectionListItem>>.Success(items);
    }

    public async Task<Result<FormConnectionDetail>> GetAsync(int id)
    {
        if (!_me.IsAdmin) return Result<FormConnectionDetail>.From(Result.Forbidden());
        var c = await _db.FormConnections.AsNoTracking().Include(x => x.Event).Include(x => x.AssignToUser).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<FormConnectionDetail>.From(Result.NotFound("Form connection"));
        var subs = await _db.FormSubmissions.AsNoTracking().Where(s => s.ConnectionId == id).OrderByDescending(s => s.Id).Take(50)
            .Select(s => new SubmissionItem(s.Id, s.ReceivedUtc, s.Status, s.LeadId, s.Lead != null ? s.Lead.FirstName + " " + s.Lead.Surname : null, s.Error, s.Summary)).ToListAsync();
        return Result<FormConnectionDetail>.Success(new FormConnectionDetail
        {
            Id = c.Id, Name = c.Name, Provider = c.Provider, IsActive = c.IsActive, EventId = c.EventId, EventName = c.Event?.Name,
            Assignment = c.Assignment, AssignToUserId = c.AssignToUserId, AssignToUserName = c.AssignToUser?.FullName, DefaultTags = c.DefaultTags,
            FieldMapping = c.FieldMapping, HasSigningSecret = !string.IsNullOrEmpty(c.SigningSecretProtected), WebhookUrl = UrlFor(c.Token),
            TotalReceived = c.TotalReceived, TotalCreated = c.TotalCreated, TotalDuplicate = c.TotalDuplicate, TotalFailed = c.TotalFailed,
            LastReceivedUtc = c.LastReceivedUtc, CreatedUtc = c.CreatedUtc, Submissions = subs
        });
    }

    public async Task<Result<FormConnectionInput>> GetInputAsync(int id)
    {
        if (!_me.IsAdmin) return Result<FormConnectionInput>.From(Result.Forbidden());
        var c = await _db.FormConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<FormConnectionInput>.From(Result.NotFound("Form connection"));
        return Result<FormConnectionInput>.Success(new FormConnectionInput
        {
            Name = c.Name, Provider = c.Provider, IsActive = c.IsActive, EventId = c.EventId, Assignment = c.Assignment,
            AssignToUserId = c.AssignToUserId, DefaultTags = c.DefaultTags, FieldMapping = c.FieldMapping
        });
    }

    private async Task<Result> ValidateAsync(FormConnectionInput i)
    {
        if (i.EventId.HasValue && !await _db.Events.AnyAsync(e => e.Id == i.EventId))
            return Result.Invalid(nameof(FormConnectionInput.EventId), "Selected event does not exist.");
        if (i.Assignment == AssignmentMode.SpecificUser &&
            !await _db.Users.AnyAsync(u => u.Id == i.AssignToUserId && u.IsActive && u.Role != UserRole.Staff))
            return Result.Invalid(nameof(FormConnectionInput.AssignToUserId), "Leads can only be assigned to active admins or sales reps.");
        var (_, err) = FormPayloadParser.ParseMapping(i.FieldMapping);
        if (err != null) return Result.Invalid(nameof(FormConnectionInput.FieldMapping), err);
        return Result.Success();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Apply(FormConnection c, FormConnectionInput i)
    {
        c.Name = i.Name.Trim(); c.Provider = i.Provider; c.IsActive = i.IsActive; c.EventId = i.EventId; c.Assignment = i.Assignment;
        c.AssignToUserId = i.Assignment == AssignmentMode.SpecificUser ? i.AssignToUserId : null;
        c.DefaultTags = Clean(i.DefaultTags); c.FieldMapping = Clean(i.FieldMapping);
        if (i.ClearSigningSecret) c.SigningSecretProtected = null;
        else if (!string.IsNullOrWhiteSpace(i.SigningSecret)) c.SigningSecretProtected = _secrets.Protect(i.SigningSecret.Trim());
    }

    public async Task<Result<int>> CreateAsync(FormConnectionInput input)
    {
        if (!_me.IsAdmin) return Result<int>.From(Result.Forbidden());
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return Result<int>.From(check);
        var c = new FormConnection { Token = FormSecrets.NewToken(), CreatedById = _me.Id };
        Apply(c, input);
        _db.FormConnections.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("FormConnectionCreated", "FormConnection", c.Id, $"{c.Name} ({c.Provider})");
        return Result<int>.Success(c.Id);
    }

    public async Task<Result> UpdateAsync(int id, FormConnectionInput input)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var c = await _db.FormConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Form connection");
        var check = await ValidateAsync(input);
        if (!check.Succeeded) return check;
        Apply(c, input);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("FormConnectionUpdated", "FormConnection", id, c.Name);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var c = await _db.FormConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Form connection");
        foreach (var s in await _db.FormSubmissions.Where(s => s.ConnectionId == id).ToListAsync()) _db.FormSubmissions.Remove(s);
        _db.FormConnections.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("FormConnectionDeleted", "FormConnection", id, c.Name);
        return Result.Success();
    }

    /// <summary>Issues a new webhook URL; the old one stops working immediately (use if the URL leaks).</summary>
    public async Task<Result> RotateTokenAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var c = await _db.FormConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Form connection");
        c.Token = FormSecrets.NewToken();
        await _db.SaveChangesAsync();
        await _audit.LogAsync("FormConnectionTokenRotated", "FormConnection", id, c.Name);
        return Result.Success();
    }

    public async Task<Result<FormPreviewResult>> PreviewAsync(int? connectionId, string payload)
    {
        if (!_me.IsAdmin) return Result<FormPreviewResult>.From(Result.Forbidden());
        string? mapping = null;
        if (connectionId.HasValue) mapping = await _db.FormConnections.AsNoTracking().Where(c => c.Id == connectionId).Select(c => c.FieldMapping).FirstOrDefaultAsync();
        var (parsed, mapped, error) = FormPayloadParser.TryProcess(payload, mapping);
        var r = new FormPreviewResult { Parsed = error == null, Error = error, ExternalId = parsed?.ExternalId };
        if (mapped != null)
        {
            foreach (var kv in mapped.Fields) r.Fields[kv.Key] = kv.Value;
            r.Unmapped = mapped.Unmapped; r.Warnings = mapped.Warnings;
            var email = mapped.Get("Email");
            if (email == null) r.Warnings.Add("No valid email address was found: this submission would be rejected. Name the question “Email”, or add a mapping line such as  Email = Work email.");
            else
            {
                var existing = await _db.Leads.AsNoTracking().Where(l => l.Email == email).Select(l => (int?)l.Id).FirstOrDefaultAsync();
                r.ExistingLeadId = existing;
                r.WouldCreate = existing == null;
            }
        }
        return Result<FormPreviewResult>.Success(r);
    }
}

public enum IngestStatus { Created, Duplicate, Rejected, Unauthorized, NotFound, Inactive, Invalid, Error }
public record IngestOutcome(IngestStatus Status, string Message, int? LeadId = null);

/// <summary>Receives a webhook delivery (no signed-in user) and turns it into a lead.</summary>
public class FormIngestService
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notify;
    private readonly IDataProtector _secrets;
    private readonly ILogger<FormIngestService> _log;

    public FormIngestService(AppDbContext db, IAuditService audit, INotificationService notify, IDataProtectionProvider dp, ILogger<FormIngestService> log)
    { _db = db; _audit = audit; _notify = notify; _log = log; _secrets = dp.CreateProtector(FormSecrets.Purpose); }

    public async Task<bool> ExistsAsync(string token) =>
        token.Length is >= 20 and <= 64 && await _db.FormConnections.AnyAsync(c => c.Token == token && c.IsActive);

    public async Task<IngestOutcome> ProcessAsync(string token, byte[] body, string? signatureHeader, string? ip)
    {
        if (token.Length is < 20 or > 64) return new(IngestStatus.NotFound, "Unknown webhook.");
        var conn = await _db.FormConnections.FirstOrDefaultAsync(c => c.Token == token);
        if (conn is null) return new(IngestStatus.NotFound, "Unknown webhook.");
        if (!conn.IsActive) return new(IngestStatus.Inactive, "This form connection is switched off.");

        if (!string.IsNullOrEmpty(conn.SigningSecretProtected))
        {
            var secret = FormSecrets.Unprotect(_secrets, conn.SigningSecretProtected);
            if (secret is null || !FormSecrets.Verify(body, secret, signatureHeader))
            {
                await _audit.LogAsync("FormSignatureRejected", "FormConnection", conn.Id, $"{conn.Name}: missing or invalid signature", userName: "webhook");
                return new(IngestStatus.Unauthorized, "Invalid signature.");
            }
        }

        ParsedSubmission parsed;
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            parsed = FormPayloadParser.Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            await RecordAsync(conn, null, SubmissionStatus.Error, null, "The request body was not valid JSON.", null, ip);
            return new(IngestStatus.Invalid, "The request body was not valid JSON.");
        }

        // Retried delivery of something we already handled: acknowledge, do nothing.
        if (parsed.ExternalId != null &&
            await _db.FormSubmissions.AnyAsync(s => s.ConnectionId == conn.Id && s.ExternalId == parsed.ExternalId))
            return new(IngestStatus.Duplicate, "Already received.");

        if (parsed.Answers.Count == 0)
        {
            await RecordAsync(conn, parsed.ExternalId, SubmissionStatus.Rejected, null, "No answers found in the payload.", null, ip);
            return new(IngestStatus.Rejected, "No answers found in the payload.");
        }

        var mapped = FormPayloadParser.Map(parsed.Answers, conn.FieldMapping);
        var email = mapped.Get("Email");
        var summary = Truncate($"{mapped.Get("FullName") ?? $"{mapped.Get("FirstName")} {mapped.Get("Surname")}".Trim()} <{email ?? "no email"}>".Trim(), 300);
        if (email is null)
        {
            var why = "No valid email address found. Name the question “Email” or add a field mapping.";
            await RecordAsync(conn, parsed.ExternalId, SubmissionStatus.Rejected, null, why, summary, ip);
            return new(IngestStatus.Rejected, why);
        }
        if (mapped.Get("FirstName") is null) { mapped.Fields["FirstName"] = email.Split('@')[0]; mapped.Warnings.Add("No name was found; the email name was used."); }

        try
        {
            var answersText = Truncate(string.Join("\n", parsed.Answers.Where(a => a.Value.Length > 0)
                .Select(a => $"{(a.Label.Length > 0 ? a.Label : a.Key)}: {a.Value}")), 1800) ?? "";

            var existing = await _db.Leads.FirstOrDefaultAsync(l => l.Email == email);
            if (existing != null)
            {
                _db.LeadActivities.Add(new LeadActivity
                {
                    LeadId = existing.Id, Type = ActivityType.Updated, Title = $"Submitted “{conn.Name}” again",
                    Detail = answersText, CreatedUtc = DateTime.UtcNow
                });
                existing.UpdatedUtc = DateTime.UtcNow;
                conn.TotalReceived++; conn.TotalDuplicate++; conn.LastReceivedUtc = DateTime.UtcNow;
                _db.FormSubmissions.Add(NewSubmission(conn, parsed.ExternalId, SubmissionStatus.Duplicate, existing.Id, null, summary, ip));
                await _db.SaveChangesAsync();
                if (existing.AssignedToId is int owner)
                    await _notify.NotifyAsync(owner, NotificationType.System, $"{existing.FirstName} {existing.Surname} submitted a form again",
                        $"Form: {conn.Name}", $"/Leads/Details/{existing.Id}");
                await PruneAsync(conn.Id);
                return new(IngestStatus.Duplicate, "A lead with this email already exists; the submission was added to its activity.", existing.Id);
            }

            var lead = new Lead
            {
                FirstName = mapped.Get("FirstName")!, Surname = mapped.Get("Surname") ?? "", Email = email, Phone = mapped.Get("Phone"),
                JobTitle = mapped.Get("JobTitle"), Industry = mapped.Get("Industry"), City = mapped.Get("City"), Province = mapped.Get("Province"),
                LinkedInUrl = mapped.Get("LinkedIn"), PreferredContact = "Email", Source = LeadSource.WebForm, Stage = LeadStage.Lead,
                EventId = conn.EventId, RegistrationDate = Clock.LocalToday, Tags = NormalizeTags(conn.DefaultTags)
            };
            if (mapped.Get("Company") is { } companyName)
            {
                var lower = companyName.ToLower();
                var company = await _db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == lower);
                if (company is null)
                {
                    company = new Company { Name = companyName, Industry = lead.Industry, City = lead.City, Province = lead.Province };
                    _db.Companies.Add(company);
                }
                lead.Company = company;
            }
            lead.AssignedToId = await PickAssigneeAsync(conn);
            var detail = $"Via form “{conn.Name}” ({conn.Provider}).\n{answersText}";
            lead.Activities.Add(new LeadActivity { Type = ActivityType.Created, Title = "Lead created from a form submission", Detail = Truncate(detail, 2000), CreatedUtc = DateTime.UtcNow });
            _db.Leads.Add(lead);

            conn.TotalReceived++; conn.TotalCreated++; conn.LastReceivedUtc = DateTime.UtcNow;
            var sub = NewSubmission(conn, parsed.ExternalId, SubmissionStatus.Created, null, mapped.Warnings.Count > 0 ? string.Join(" ", mapped.Warnings) : null, summary, ip);
            sub.Lead = lead;
            _db.FormSubmissions.Add(sub);
            await _db.SaveChangesAsync();

            var leadName = $"{lead.FirstName} {lead.Surname}".Trim();
            if (lead.AssignedToId is int assignee)
                await _notify.NotifyAsync(assignee, NotificationType.LeadAssigned, $"New form lead: {leadName}", $"From “{conn.Name}”.", $"/Leads/Details/{lead.Id}");
            else
            {
                var admins = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == UserRole.Admin).Select(u => u.Id).ToListAsync();
                await _notify.NotifyManyAsync(admins, NotificationType.LeadAssigned, $"New unassigned form lead: {leadName}", $"From “{conn.Name}”.", $"/Leads/Details/{lead.Id}");
            }
            await _audit.LogAsync("FormLeadCreated", "Lead", lead.Id, $"{leadName} via {conn.Name}", userName: "webhook");
            await PruneAsync(conn.Id);
            return new(IngestStatus.Created, "Lead created.", lead.Id);
        }
        catch (DbUpdateException ex)
        {
            // Two deliveries raced (same email / same submission id). The provider will retry and hit the duplicate path.
            _log.LogWarning(ex, "Form ingest conflict for connection {Id}", conn.Id);
            _db.ChangeTracker.Clear();
            return new(IngestStatus.Error, "Temporary conflict, please retry.");
        }
    }

    private static FormSubmission NewSubmission(FormConnection c, string? externalId, SubmissionStatus status, int? leadId, string? error, string? summary, string? ip) =>
        new() { ConnectionId = c.Id, ExternalId = externalId, Status = status, LeadId = leadId, Error = Truncate(error, 500), Summary = summary, IpAddress = ip };

    private async Task RecordAsync(FormConnection c, string? externalId, SubmissionStatus status, int? leadId, string error, string? summary, string? ip)
    {
        c.TotalReceived++; c.TotalFailed++; c.LastReceivedUtc = DateTime.UtcNow;
        _db.FormSubmissions.Add(NewSubmission(c, externalId, status, leadId, error, summary, ip));
        try { await _db.SaveChangesAsync(); } catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
    }

    /// <summary>Round robin (fair rotation) over active admins and sales reps, least-busy, or one named person.</summary>
    private async Task<int?> PickAssigneeAsync(FormConnection c)
    {
        if (c.Assignment == AssignmentMode.SpecificUser && c.AssignToUserId is int sid &&
            await _db.Users.AnyAsync(u => u.Id == sid && u.IsActive && u.Role != UserRole.Staff))
            return sid;

        var team = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.Role != UserRole.Staff).OrderBy(u => u.Id).Select(u => u.Id).ToListAsync();
        if (team.Count == 0) return null;

        if (c.Assignment == AssignmentMode.LeastBusy)
        {
            var loads = await _db.Leads.AsNoTracking().Where(l => l.AssignedToId != null && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost)
                .GroupBy(l => l.AssignedToId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync();
            var map = loads.Where(x => x.Id.HasValue).ToDictionary(x => x.Id!.Value, x => x.Count);
            var pick = team.OrderBy(t => map.GetValueOrDefault(t)).ThenBy(t => t).First();
            c.LastAssignedUserId = pick;
            return pick;
        }

        var last = c.LastAssignedUserId ?? 0;
        var next = team.Where(t => t > last).DefaultIfEmpty(team[0]).First();
        c.LastAssignedUserId = next;
        return next;
    }

    private async Task PruneAsync(int connectionId)
    {
        var cutoff = await _db.FormSubmissions.AsNoTracking().Where(s => s.ConnectionId == connectionId).OrderByDescending(s => s.Id)
            .Skip(200).Select(s => s.Id).FirstOrDefaultAsync();
        if (cutoff == 0) return;
        foreach (var old in await _db.FormSubmissions.Where(s => s.ConnectionId == connectionId && s.Id <= cutoff).ToListAsync()) _db.FormSubmissions.Remove(old);
        await _db.SaveChangesAsync();
    }

    private static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        var list = tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Length > 30 ? t[..30] : t).Distinct(StringComparer.OrdinalIgnoreCase).Take(10);
        var joined = string.Join(", ", list);
        return joined.Length == 0 ? null : joined;
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
