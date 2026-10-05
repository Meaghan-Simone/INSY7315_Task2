using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class LeadService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    private readonly INotificationService _notify;

    public LeadService(AppDbContext db, ICurrentUser me, IAuditService audit, INotificationService notify)
    { _db = db; _me = me; _audit = audit; _notify = notify; }

    // ------------------------------------------------------------ permissions
    private bool CanEdit(Lead l) => _me.IsAdmin || (_me.CanWrite && (l.AssignedToId == null || l.AssignedToId == _me.Id));

    // ------------------------------------------------------------ queries
    private IQueryable<Lead> Filter(LeadQuery q)
    {
        var query = _db.Leads.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var t = q.Q.Trim();
            query = query.Where(l => l.FirstName.Contains(t) || l.Surname.Contains(t) || l.Email.Contains(t) ||
                                     (l.FirstName + " " + l.Surname).Contains(t) || (l.Company != null && l.Company.Name.Contains(t)));
        }
        if (q.Stage.HasValue) query = query.Where(l => l.Stage == q.Stage.Value);
        if (q.EventId.HasValue) query = query.Where(l => l.EventId == q.EventId.Value);
        if (q.CompanyId.HasValue) query = query.Where(l => l.CompanyId == q.CompanyId.Value);
        if (q.AssignedToId.HasValue) query = query.Where(l => l.AssignedToId == q.AssignedToId.Value);
        var myId = _me.Id;
        var endOfToday = Clock.LocalToday.AddDays(1);
        query = q.View switch
        {
            "mine" => query.Where(l => l.AssignedToId == myId),
            "followup" => query.Where(l => l.NextFollowUp != null && l.NextFollowUp < endOfToday && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost),
            "starred" => query.Where(l => _db.LeadStars.Any(s => s.LeadId == l.Id && s.UserId == myId)),
            _ => query
        };
        return query;
    }

    private static IQueryable<Lead> Sort(IQueryable<Lead> q, string? sort) => sort switch
    {
        "name" => q.OrderBy(l => l.FirstName).ThenBy(l => l.Surname).ThenBy(l => l.Id),
        "value" => q.OrderByDescending(l => (double)l.DealValue).ThenBy(l => l.Id),
        "followup" => q.OrderBy(l => l.NextFollowUp == null).ThenBy(l => l.NextFollowUp).ThenBy(l => l.Id),
        "stage" => q.OrderBy(l => l.Stage).ThenByDescending(l => l.Id),
        _ => q.OrderByDescending(l => l.CreatedUtc).ThenByDescending(l => l.Id)
    };

    public async Task<PagedResult<LeadListItem>> SearchAsync(LeadQuery q)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 5, 200);
        var myId = _me.Id;
        var query = Filter(q);
        var total = await query.CountAsync();
        var items = await Sort(query, q.Sort).Skip((page - 1) * size).Take(size)
            .Select(l => new LeadListItem(l.Id, l.FirstName + " " + l.Surname, l.JobTitle, l.CompanyId,
                l.Company != null ? l.Company.Name : null, l.Stage, l.DealValue, l.Source, l.AssignedToId,
                l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp,
                _db.LeadStars.Any(s => s.LeadId == l.Id && s.UserId == myId), l.Event != null ? l.Event.Name : null))
            .ToListAsync();
        return new PagedResult<LeadListItem>(items, page, size, total);
    }

    public async Task<(int All, int Mine, int FollowUp, int Starred)> TabCountsAsync()
    {
        var myId = _me.Id; var end = Clock.LocalToday.AddDays(1);
        var all = await _db.Leads.CountAsync();
        var mine = await _db.Leads.CountAsync(l => l.AssignedToId == myId);
        var fu = await _db.Leads.CountAsync(l => l.NextFollowUp != null && l.NextFollowUp < end && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost);
        var starred = await _db.LeadStars.CountAsync(s => s.UserId == myId);
        return (all, mine, fu, starred);
    }

    public async Task<Result<LeadDetail>> GetAsync(int id)
    {
        var l = await _db.Leads.AsNoTracking().Include(x => x.Company).Include(x => x.Event).Include(x => x.AssignedTo)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (l is null) return Result<LeadDetail>.From(Result.NotFound("Lead"));
        var myId = _me.Id;

        var d = new LeadDetail
        {
            Id = l.Id, FirstName = l.FirstName, Surname = l.Surname, JobTitle = l.JobTitle, Industry = l.Industry, Email = l.Email,
            Phone = l.Phone, City = l.City, Province = l.Province, LinkedInUrl = l.LinkedInUrl, PreferredContact = l.PreferredContact,
            CompanyId = l.CompanyId, CompanyName = l.Company?.Name, CompanyIndustry = l.Company?.Industry, CompanySize = l.Company?.Size,
            EventId = l.EventId, EventName = l.Event?.Name, EventLocation = l.Event?.Location, RegistrationDate = l.RegistrationDate,
            Source = l.Source, Stage = l.Stage, DealValue = l.DealValue, AssignedToId = l.AssignedToId, AssignedToName = l.AssignedTo?.FullName,
            NextFollowUp = l.NextFollowUp, LastContactedUtc = l.LastContactedUtc, Tags = l.Tags, OptedOut = l.OptedOut, CreatedUtc = l.CreatedUtc,
            CanEdit = CanEdit(l), CanDelete = _me.IsAdmin,
            Starred = await _db.LeadStars.AnyAsync(s => s.LeadId == id && s.UserId == myId)
        };
        d.Notes = await _db.LeadNotes.AsNoTracking().Where(n => n.LeadId == id).OrderByDescending(n => n.CreatedUtc)
            .Select(n => new NoteDto(n.Id, n.AuthorId, n.Author != null ? n.Author.FullName : "Deleted user", n.Body, n.CreatedUtc)).ToListAsync();
        d.Activities = await _db.LeadActivities.AsNoTracking().Where(a => a.LeadId == id).OrderByDescending(a => a.CreatedUtc).ThenByDescending(a => a.Id)
            .Select(a => new ActivityDto(a.Id, a.Type, a.Title, a.Detail, a.User != null ? a.User.FullName : null, a.CreatedUtc)).ToListAsync();
        return Result<LeadDetail>.Success(d);
    }

    public async Task<Result<LeadInput>> GetInputAsync(int id)
    {
        var l = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l is null) return Result<LeadInput>.From(Result.NotFound("Lead"));
        if (!CanEdit(l)) return Result<LeadInput>.From(Result.Forbidden("You can only edit leads assigned to you."));
        return Result<LeadInput>.Success(new LeadInput
        {
            FirstName = l.FirstName, Surname = l.Surname, JobTitle = l.JobTitle, Industry = l.Industry, Email = l.Email, Phone = l.Phone,
            City = l.City, Province = l.Province, LinkedInUrl = l.LinkedInUrl, PreferredContact = l.PreferredContact, CompanyId = l.CompanyId,
            EventId = l.EventId, RegistrationDate = l.RegistrationDate, Source = l.Source, Stage = l.Stage, DealValue = l.DealValue,
            AssignedToId = l.AssignedToId, NextFollowUp = l.NextFollowUp, Tags = l.Tags
        });
    }

    // ------------------------------------------------------------ commands
    private async Task<Result> ValidateReferencesAsync(LeadInput i, int? leadId)
    {
        var email = AuthService.NormalizeEmail(i.Email);
        if (await _db.Leads.AnyAsync(l => l.Email == email && l.Id != (leadId ?? 0)))
            return Result.Conflict("A lead with this email address already exists.", nameof(LeadInput.Email));
        if (i.LinkedInUrl != null && !UiHelpers.IsSafeHttpUrl(i.LinkedInUrl))
            return Result.Invalid(nameof(LeadInput.LinkedInUrl), "Enter a valid http(s) link.");
        if (i.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.Id == i.CompanyId))
            return Result.Invalid(nameof(LeadInput.CompanyId), "Selected company does not exist.");
        if (i.EventId.HasValue && !await _db.Events.AnyAsync(e => e.Id == i.EventId))
            return Result.Invalid(nameof(LeadInput.EventId), "Selected event does not exist.");
        if (i.AssignedToId.HasValue && !await _db.Users.AnyAsync(u => u.Id == i.AssignedToId && u.IsActive && u.Role != UserRole.Staff))
            return Result.Invalid(nameof(LeadInput.AssignedToId), "Leads can only be assigned to active admins or sales reps.");
        return Result.Success();
    }

    private static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        var list = tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Length > 30 ? t[..30] : t).Distinct(StringComparer.OrdinalIgnoreCase).Take(10);
        var joined = string.Join(", ", list);
        return joined.Length == 0 ? null : joined;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Apply(Lead l, LeadInput i)
    {
        l.FirstName = i.FirstName.Trim(); l.Surname = i.Surname.Trim(); l.JobTitle = Clean(i.JobTitle); l.Industry = Clean(i.Industry);
        l.Email = AuthService.NormalizeEmail(i.Email); l.Phone = Clean(i.Phone); l.City = Clean(i.City); l.Province = Clean(i.Province);
        l.LinkedInUrl = Clean(i.LinkedInUrl); l.PreferredContact = Clean(i.PreferredContact); l.CompanyId = i.CompanyId; l.EventId = i.EventId;
        l.RegistrationDate = i.RegistrationDate?.Date; l.Source = i.Source; l.DealValue = i.DealValue; l.NextFollowUp = i.NextFollowUp;
        l.Tags = NormalizeTags(i.Tags); l.UpdatedUtc = DateTime.UtcNow;
    }

    private async Task<int?> LeastLoadedRepAsync()
    {
        var reps = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == UserRole.SalesRep).Select(u => u.Id).ToListAsync();
        if (reps.Count == 0) return _me.CanWrite ? _me.Id : null;
        var loads = await _db.Leads.AsNoTracking().Where(l => l.AssignedToId != null && l.Stage != LeadStage.ClosedWon && l.Stage != LeadStage.ClosedLost)
            .GroupBy(l => l.AssignedToId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync();
        var map = loads.Where(x => x.Id.HasValue).ToDictionary(x => x.Id!.Value, x => x.Count);
        return reps.OrderBy(r => map.GetValueOrDefault(r)).ThenBy(r => r).First();
    }

    private static void SetStage(Lead l, LeadStage stage)
    {
        l.Stage = stage;
        l.ClosedUtc = stage is LeadStage.ClosedWon or LeadStage.ClosedLost ? DateTime.UtcNow : null;
    }

    public async Task<Result<int>> CreateAsync(LeadInput input)
    {
        if (!_me.CanWrite) return Result<int>.From(Result.Forbidden("Your role is read-only."));
        var check = await ValidateReferencesAsync(input, null);
        if (!check.Succeeded) return Result<int>.From(check);

        var lead = new Lead { CreatedById = _me.Id };
        Apply(lead, input);
        SetStage(lead, input.Stage);
        lead.AssignedToId = input.AssignedToId ?? await LeastLoadedRepAsync();
        lead.RegistrationDate ??= Clock.LocalToday;
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();

        _db.LeadActivities.Add(new LeadActivity { LeadId = lead.Id, UserId = _me.Id, Type = ActivityType.Created, Title = "Lead created", Detail = $"Source: {lead.Source.Label()}" });
        await _db.SaveChangesAsync();
        await NotifyAssignedAsync(lead);
        await _audit.LogAsync("LeadCreated", "Lead", lead.Id, lead.FullName);
        return Result<int>.Success(lead.Id);
    }

    private async Task NotifyAssignedAsync(Lead lead)
    {
        if (lead.AssignedToId is int uid && uid != _me.Id)
            await _notify.NotifyAsync(uid, NotificationType.LeadAssigned, "New lead assigned to you",
                $"{lead.FullName} was assigned to you by {_me.Name}.", $"/Leads/Details/{lead.Id}");
    }

    public async Task<Result> UpdateAsync(int id, LeadInput input)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result.NotFound("Lead");
        if (!CanEdit(lead)) return Result.Forbidden("You can only edit leads assigned to you.");
        var check = await ValidateReferencesAsync(input, id);
        if (!check.Succeeded) return check;

        var oldStage = lead.Stage; var oldAssignee = lead.AssignedToId;
        Apply(lead, input);
        if (oldStage != input.Stage) SetStage(lead, input.Stage);
        lead.AssignedToId = input.AssignedToId;
        await _db.SaveChangesAsync();

        _db.LeadActivities.Add(new LeadActivity { LeadId = id, UserId = _me.Id, Type = ActivityType.Updated, Title = "Lead updated" });
        if (oldStage != lead.Stage) await AfterStageChangeAsync(lead, oldStage);
        if (oldAssignee != lead.AssignedToId) await AfterAssignAsync(lead);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("LeadUpdated", "Lead", id, lead.FullName);
        return Result.Success();
    }

    private async Task AfterStageChangeAsync(Lead lead, LeadStage oldStage)
    {
        _db.LeadActivities.Add(new LeadActivity
        {
            LeadId = lead.Id, UserId = _me.Id, Type = ActivityType.StageChanged, Title = "Stage changed",
            Detail = $"{oldStage.Label()} → {lead.Stage.Label()}, updated by {_me.Name}"
        });
        if (lead.Stage == LeadStage.ClosedWon)
        {
            var admins = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == UserRole.Admin).Select(u => u.Id).ToListAsync();
            var recipients = admins.Concat(lead.AssignedToId is int a ? new[] { a } : Array.Empty<int>()).Where(u => u != _me.Id);
            await _db.SaveChangesAsync();
            await _notify.NotifyManyAsync(recipients, NotificationType.DealWon, "Deal moved to Closed won",
                $"{lead.FullName} — {UiHelpers.Money(lead.DealValue)}", $"/Leads/Details/{lead.Id}");
        }
    }

    private async Task AfterAssignAsync(Lead lead)
    {
        var name = lead.AssignedToId is int uid ? await _db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync() : null;
        _db.LeadActivities.Add(new LeadActivity
        {
            LeadId = lead.Id, UserId = _me.Id, Type = ActivityType.Assigned, Title = "Assigned",
            Detail = name is null ? "Lead unassigned" : $"Lead assigned to {name}"
        });
        await _db.SaveChangesAsync();
        await NotifyAssignedAsync(lead);
    }

    public async Task<Result> ChangeStageAsync(int id, LeadStage stage)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result.NotFound("Lead");
        if (!CanEdit(lead)) return Result.Forbidden("You can only move leads assigned to you.");
        if (!Enum.IsDefined(stage)) return Result.Invalid(nameof(StageInput.Stage), "Unknown stage.");
        if (lead.Stage == stage) return Result.Success();
        var old = lead.Stage;
        SetStage(lead, stage);
        lead.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await AfterStageChangeAsync(lead, old);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("LeadStageChanged", "Lead", id, $"{old} -> {stage}");
        return Result.Success();
    }

    public async Task<Result> AssignAsync(int id, int? userId)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result.NotFound("Lead");
        if (!CanEdit(lead)) return Result.Forbidden("You can only reassign leads assigned to you.");
        if (userId.HasValue && !await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.Role != UserRole.Staff))
            return Result.Invalid(nameof(AssignInput.UserId), "Leads can only be assigned to active admins or sales reps.");
        if (lead.AssignedToId == userId) return Result.Success();
        lead.AssignedToId = userId; lead.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await AfterAssignAsync(lead);
        await _audit.LogAsync("LeadAssigned", "Lead", id, userId?.ToString() ?? "unassigned");
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Only admins can delete leads.");
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result.NotFound("Lead");
        var name = lead.FullName;
        _db.Leads.Remove(lead);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("LeadDeleted", "Lead", id, name);
        return Result.Success();
    }

    public async Task<Result<NoteDto>> AddNoteAsync(int id, string body)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result<NoteDto>.From(Result.NotFound("Lead"));
        if (!CanEdit(lead)) return Result<NoteDto>.From(Result.Forbidden("You can only add notes to leads assigned to you."));
        var note = new LeadNote { LeadId = id, AuthorId = _me.Id, Body = body.Trim() };
        _db.LeadNotes.Add(note);
        _db.LeadActivities.Add(new LeadActivity { LeadId = id, UserId = _me.Id, Type = ActivityType.NoteAdded, Title = "Note added" });
        lead.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Result<NoteDto>.Success(new NoteDto(note.Id, _me.Id, _me.Name, note.Body, note.CreatedUtc));
    }

    public async Task<Result> LogCallAsync(int id, CallInput input)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return Result.NotFound("Lead");
        if (!CanEdit(lead)) return Result.Forbidden("You can only log calls on leads assigned to you.");
        _db.LeadActivities.Add(new LeadActivity
        {
            LeadId = id, UserId = _me.Id, Type = ActivityType.CallLogged, Title = "Call logged",
            Detail = $"{input.DurationMinutes}-minute call: {input.Summary.Trim()}"
        });
        lead.LastContactedUtc = DateTime.UtcNow;
        if (input.NextFollowUp.HasValue) lead.NextFollowUp = input.NextFollowUp;
        lead.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CallLogged", "Lead", id);
        return Result.Success();
    }

    public async Task<Result<bool>> ToggleStarAsync(int id)
    {
        if (!await _db.Leads.AnyAsync(l => l.Id == id)) return Result<bool>.From(Result.NotFound("Lead"));
        var myId = _me.Id;
        var star = await _db.LeadStars.FirstOrDefaultAsync(s => s.LeadId == id && s.UserId == myId);
        if (star is null) { _db.LeadStars.Add(new LeadStar { LeadId = id, UserId = myId }); await _db.SaveChangesAsync(); return Result<bool>.Success(true); }
        _db.LeadStars.Remove(star);
        await _db.SaveChangesAsync();
        return Result<bool>.Success(false);
    }

    // ------------------------------------------------------------ pipeline
    public async Task<PipelineVm> PipelineAsync(int? eventId, int? assignedToId, string? q)
    {
        var query = Filter(new LeadQuery(Q: q, EventId: eventId, AssignedToId: assignedToId));
        var rows = await query.Select(l => new
        {
            l.Id, Name = l.FirstName + " " + l.Surname, Company = l.Company != null ? l.Company.Name : null,
            l.DealValue, l.Stage, l.AssignedToId, Assignee = l.AssignedTo != null ? l.AssignedTo.FullName : null
        }).ToListAsync();

        var columns = Enum.GetValues<LeadStage>().Select(stage =>
        {
            var inStage = rows.Where(r => r.Stage == stage).OrderByDescending(r => r.DealValue).ToList();
            return new PipelineColumn(stage, inStage.Count, inStage.Sum(r => r.DealValue),
                inStage.Take(30).Select(r => new PipelineCard(r.Id, r.Name, r.Company, r.DealValue, r.AssignedToId, r.Assignee)).ToList());
        }).ToList();
        var active = rows.Count(r => r.Stage is not (LeadStage.ClosedWon or LeadStage.ClosedLost));
        return new PipelineVm(columns, active, _me.CanWrite);
    }

    // ------------------------------------------------------------ export / import
    public async Task<byte[]> ExportCsvAsync(LeadQuery q)
    {
        var leads = await Sort(Filter(q with { Page = 1 }), q.Sort).Take(20000)
            .Select(l => new
            {
                l.FirstName, l.Surname, l.Email, l.Phone, l.JobTitle, l.Industry, Company = l.Company != null ? l.Company.Name : null,
                Event = l.Event != null ? l.Event.Name : null, l.City, l.Province, l.Source, l.Stage, l.DealValue,
                Assigned = l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp, l.Tags, l.CreatedUtc
            }).ToListAsync();
        await _audit.LogAsync("LeadsExported", "Lead", null, $"{leads.Count} rows");
        return CsvHelper.Build(
            new[] { "FirstName", "Surname", "Email", "Phone", "JobTitle", "Industry", "Company", "Event", "City", "Province", "Source", "Stage", "DealValue", "AssignedTo", "NextFollowUp", "Tags", "Created" },
            leads.Select(l => new object?[] { l.FirstName, l.Surname, l.Email, l.Phone, l.JobTitle, l.Industry, l.Company, l.Event, l.City, l.Province,
                l.Source.Label(), l.Stage.Label(), l.DealValue, l.Assigned, l.NextFollowUp?.ToString("yyyy-MM-dd"), l.Tags, l.CreatedUtc }));
    }

    public async Task<Result<ImportResult>> ImportCsvAsync(Stream stream)
    {
        if (!_me.CanWrite) return Result<ImportResult>.From(Result.Forbidden("Your role is read-only."));
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var rows = CsvHelper.Parse(reader);
        var result = new ImportResult();
        if (rows.Count < 2) return Result<ImportResult>.From(Result.Invalid("file", "The file is empty or has no data rows."));

        var header = rows[0].Select(h => h.Trim().TrimStart('\uFEFF').Replace(" ", "").ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var cFirst = Col("firstname"); var cSur = Col("surname"); var cEmail = Col("email");
        if (cFirst < 0 || cSur < 0 || cEmail < 0)
            return Result<ImportResult>.From(Result.Invalid("file", "Header row must include at least: FirstName, Surname, Email."));
        string? Get(string[] r, string name) { var i = Col(name); return i >= 0 && i < r.Length && r[i].Trim().Length > 0 ? r[i].Trim() : null; }

        var existing = new HashSet<string>(await _db.Leads.AsNoTracking().Select(l => l.Email).ToListAsync());
        var companies = await _db.Companies.ToDictionaryAsync(c => c.Name.ToLower(), c => c);
        var events = await _db.Events.AsNoTracking().ToDictionaryAsync(e => e.Name.ToLower(), e => e.Id);
        var fallbackAssignee = await LeastLoadedRepAsync();

        for (var n = 1; n < rows.Count; n++)
        {
            var r = rows[n];
            var input = new LeadInput
            {
                FirstName = Get(r, "firstname") ?? "", Surname = Get(r, "surname") ?? "", Email = Get(r, "email") ?? "",
                JobTitle = Get(r, "jobtitle"), Industry = Get(r, "industry"), Phone = Get(r, "phone") ?? Get(r, "cell"),
                City = Get(r, "city"), Province = Get(r, "province"), Tags = Get(r, "tags")
            };
            if (Enum.TryParse<LeadSource>((Get(r, "source") ?? "").Replace(" ", ""), true, out var src)) input.Source = src;
            if (Enum.TryParse<LeadStage>((Get(r, "stage") ?? "").Replace(" ", ""), true, out var stage)) input.Stage = stage;
            if (decimal.TryParse(Get(r, "dealvalue"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) && val >= 0) input.DealValue = val;

            var ctx = new ValidationContext(input);
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(input, ctx, errors, true))
            {
                result.Skipped++;
                if (result.Errors.Count < 20) result.Errors.Add($"Row {n + 1}: {errors[0].ErrorMessage}");
                continue;
            }
            var email = AuthService.NormalizeEmail(input.Email);
            if (!existing.Add(email)) { result.Skipped++; if (result.Errors.Count < 20) result.Errors.Add($"Row {n + 1}: {email} already exists."); continue; }

            var lead = new Lead { CreatedById = _me.Id, AssignedToId = _me.Role == UserRole.SalesRep ? _me.Id : fallbackAssignee };
            Apply(lead, input);
            SetStage(lead, input.Stage);
            lead.RegistrationDate = Clock.LocalToday;

            var companyName = Get(r, "company");
            if (companyName != null)
            {
                if (!companies.TryGetValue(companyName.ToLower(), out var company))
                {
                    company = new Company { Name = companyName.Length > 200 ? companyName[..200] : companyName, Industry = input.Industry, City = input.City, Province = input.Province };
                    companies[companyName.ToLower()] = company;
                    _db.Companies.Add(company);
                }
                lead.Company = company;
            }
            var eventName = Get(r, "event");
            if (eventName != null && events.TryGetValue(eventName.ToLower(), out var evId)) lead.EventId = evId;
            lead.Activities.Add(new LeadActivity { UserId = _me.Id, Type = ActivityType.Created, Title = "Lead created", Detail = "Imported from CSV" });
            _db.Leads.Add(lead);
            result.Created++;
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("LeadsImported", "Lead", null, $"{result.Created} created, {result.Skipped} skipped");
        return Result<ImportResult>.Success(result);
    }
}
