using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class CompanyService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    public CompanyService(AppDbContext db, ICurrentUser me, IAuditService audit) { _db = db; _me = me; _audit = audit; }

    private IQueryable<Company> Filter(CompanyQuery q)
    {
        var query = _db.Companies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var t = q.Q.Trim();
            query = query.Where(c => c.Name.Contains(t) || (c.City != null && c.City.Contains(t)) || (c.ContactPerson != null && c.ContactPerson.Contains(t)));
        }
        if (!string.IsNullOrWhiteSpace(q.Industry)) query = query.Where(c => c.Industry == q.Industry);
        if (!string.IsNullOrWhiteSpace(q.Size)) query = query.Where(c => c.Size == q.Size);
        return query;
    }

    public async Task<PagedResult<CompanyListItem>> SearchAsync(CompanyQuery q)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 5, 200);
        var query = Filter(q);
        var isAdmin = _me.IsAdmin; var myId = _me.Id;
        var total = await query.CountAsync();
        var items = await query.OrderBy(c => c.Name).Skip((page - 1) * size).Take(size)
            .Select(c => new CompanyListItem(c.Id, c.Name, c.City, c.Industry, c.Size, c.Leads.Count(l => isAdmin || l.AssignedToId == myId), c.ContactPerson)).ToListAsync();
        return new PagedResult<CompanyListItem>(items, page, size, total);
    }

    public Task<List<string>> IndustriesAsync() =>
        _db.Companies.AsNoTracking().Where(c => c.Industry != null).Select(c => c.Industry!).Distinct().OrderBy(x => x).ToListAsync();

    public Task<List<Option>> OptionsAsync() =>
        _db.Companies.AsNoTracking().OrderBy(c => c.Name).Select(c => new Option(c.Id, c.Name)).ToListAsync();

    public async Task<Result<CompanyDetail>> GetAsync(int id)
    {
        var c = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<CompanyDetail>.From(Result.NotFound("Company"));
        var myId = _me.Id;
        var leads = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.CompanyId == id).OrderByDescending(l => (double)l.DealValue)
            .Select(l => new LeadListItem(l.Id, l.FirstName + " " + l.Surname, l.JobTitle, l.CompanyId, c.Name, l.Stage, l.DealValue, l.Source,
                l.AssignedToId, l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp, false, l.Event != null ? l.Event.Name : null)).ToListAsync();
        return Result<CompanyDetail>.Success(new CompanyDetail
        {
            Id = c.Id, Name = c.Name, Industry = c.Industry, Size = c.Size, Website = c.Website, AddressLine = c.AddressLine, City = c.City,
            Province = c.Province, ContactPerson = c.ContactPerson, ContactPhone = c.ContactPhone, ContactEmail = c.ContactEmail, Notes = c.Notes,
            CreatedUtc = c.CreatedUtc, Leads = leads,
            TotalPipeline = leads.Where(l => l.Stage < LeadStage.ClosedWon).Sum(l => l.DealValue),
            WonValue = leads.Where(l => l.Stage == LeadStage.ClosedWon).Sum(l => l.DealValue),
            CanEdit = _me.CanWrite, CanDelete = _me.IsAdmin
        });
    }

    public async Task<Result<CompanyInput>> GetInputAsync(int id)
    {
        if (!_me.CanWrite) return Result<CompanyInput>.From(Result.Forbidden());
        var c = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result<CompanyInput>.From(Result.NotFound("Company"));
        return Result<CompanyInput>.Success(new CompanyInput
        {
            Name = c.Name, Industry = c.Industry, Size = c.Size, Website = c.Website, AddressLine = c.AddressLine, City = c.City, Province = c.Province,
            ContactPerson = c.ContactPerson, ContactPhone = c.ContactPhone, ContactEmail = c.ContactEmail, Notes = c.Notes
        });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<Result> ValidateAsync(CompanyInput i, int? id)
    {
        if (i.Website != null && !UiHelpers.IsSafeHttpUrl(i.Website)) return Result.Invalid(nameof(CompanyInput.Website), "Enter a valid http(s) link.");
        var name = i.Name.Trim();
        if (await _db.Companies.AnyAsync(c => c.Name == name && c.Id != (id ?? 0)))
            return Result.Conflict("A company with this name already exists.", nameof(CompanyInput.Name));
        return Result.Success();
    }

    private static void Apply(Company c, CompanyInput i)
    {
        c.Name = i.Name.Trim(); c.Industry = Clean(i.Industry); c.Size = Clean(i.Size); c.Website = Clean(i.Website);
        c.AddressLine = Clean(i.AddressLine); c.City = Clean(i.City); c.Province = Clean(i.Province); c.ContactPerson = Clean(i.ContactPerson);
        c.ContactPhone = Clean(i.ContactPhone); c.ContactEmail = Clean(i.ContactEmail); c.Notes = Clean(i.Notes);
    }

    public async Task<Result<int>> CreateAsync(CompanyInput input)
    {
        if (!_me.CanWrite) return Result<int>.From(Result.Forbidden("Your role is read-only."));
        var check = await ValidateAsync(input, null);
        if (!check.Succeeded) return Result<int>.From(check);
        var c = new Company(); Apply(c, input);
        _db.Companies.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CompanyCreated", "Company", c.Id, c.Name);
        return Result<int>.Success(c.Id);
    }

    public async Task<Result> UpdateAsync(int id, CompanyInput input)
    {
        if (!_me.CanWrite) return Result.Forbidden("Your role is read-only.");
        var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Company");
        var check = await ValidateAsync(input, id);
        if (!check.Succeeded) return check;
        Apply(c, input);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CompanyUpdated", "Company", id, c.Name);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Only admins can delete companies.");
        var c = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Result.NotFound("Company");
        if (await _db.Leads.AnyAsync(l => l.CompanyId == id))
            return Result.Conflict("This company still has linked leads. Reassign or delete them first.");
        _db.Companies.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CompanyDeleted", "Company", id, c.Name);
        return Result.Success();
    }

    public async Task<byte[]> ExportCsvAsync(CompanyQuery q)
    {
        var rows = await Filter(q).OrderBy(c => c.Name).Take(20000).Select(c => new
        { c.Name, c.Industry, c.Size, c.Website, c.AddressLine, c.City, c.Province, c.ContactPerson, c.ContactPhone, c.ContactEmail, Leads = c.Leads.Count }).ToListAsync();
        await _audit.LogAsync("CompaniesExported", "Company", null, $"{rows.Count} rows");
        return CsvHelper.Build(new[] { "Name", "Industry", "Size", "Website", "Address", "City", "Province", "ContactPerson", "ContactPhone", "ContactEmail", "Leads" },
            rows.Select(c => new object?[] { c.Name, c.Industry, c.Size, c.Website, c.AddressLine, c.City, c.Province, c.ContactPerson, c.ContactPhone, c.ContactEmail, c.Leads }));
    }
}

public class EventService
{
    public static readonly string[] Types = { "Conference", "Workshop", "Networking", "Forum", "Roadshow" };

    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    public EventService(AppDbContext db, ICurrentUser me, IAuditService audit) { _db = db; _me = me; _audit = audit; }

    public async Task<PagedResult<EventListItem>> SearchAsync(EventQuery q)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 6, 100);
        var query = _db.Events.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Q)) { var t = q.Q.Trim(); query = query.Where(e => e.Name.Contains(t) || (e.Location != null && e.Location.Contains(t))); }
        if (!string.IsNullOrWhiteSpace(q.Type)) query = query.Where(e => e.Type == q.Type);
        if (q.Year.HasValue) query = query.Where(e => e.Year == q.Year);
        var total = await query.CountAsync();
        var today = Clock.LocalToday;
        var rows = await query.OrderByDescending(e => e.StartDate).Skip((page - 1) * size).Take(size).ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.EventId != null && ids.Contains(l.EventId.Value))
            .GroupBy(l => l.EventId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync();
        var countMap = counts.Where(c => c.Id.HasValue).ToDictionary(c => c.Id!.Value, c => c.Count);
        var names = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.EventId != null && ids.Contains(l.EventId.Value))
            .OrderByDescending(l => l.Id).Select(l => new { l.EventId, l.FirstName, l.Surname }).Take(600).ToListAsync();
        var items = rows.Select(e => new EventListItem(e.Id, e.Name, e.Type, e.StartDate, e.EndDate, e.Location, countMap.GetValueOrDefault(e.Id),
            (e.EndDate ?? e.StartDate).Date >= today,
            names.Where(n => n.EventId == e.Id).Take(3).Select(n => UiHelpers.Initials(n.FirstName + " " + n.Surname)).ToList())).ToList();
        return new PagedResult<EventListItem>(items, page, size, total);
    }

    public Task<List<Option>> OptionsAsync() =>
        _db.Events.AsNoTracking().OrderByDescending(e => e.StartDate).Select(e => new Option(e.Id, e.Name)).ToListAsync();

    public Task<List<int>> YearsAsync() => _db.Events.AsNoTracking().Select(e => e.Year).Distinct().OrderByDescending(y => y).ToListAsync();

    public async Task<Result<EventDetail>> GetAsync(int id)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result<EventDetail>.From(Result.NotFound("Event"));
        var leads = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.EventId == id).Select(l => new { l.Stage, l.DealValue }).ToListAsync();
        var recent = await _db.Leads.AsNoTracking().VisibleTo(_me).Where(l => l.EventId == id).OrderByDescending(l => l.Id).Take(8)
            .Select(l => new LeadListItem(l.Id, l.FirstName + " " + l.Surname, l.JobTitle, l.CompanyId, l.Company != null ? l.Company.Name : null, l.Stage,
                l.DealValue, l.Source, l.AssignedToId, l.AssignedTo != null ? l.AssignedTo.FullName : null, l.NextFollowUp, false, e.Name)).ToListAsync();
        var days = (e.StartDate.Date - Clock.LocalToday).Days;
        return Result<EventDetail>.Success(new EventDetail
        {
            Id = e.Id, Name = e.Name, Type = e.Type, Year = e.Year, StartDate = e.StartDate, EndDate = e.EndDate, Location = e.Location, Description = e.Description,
            Registered = leads.Count,
            QualifiedLeads = leads.Count(l => l.Stage >= LeadStage.Qualified && l.Stage != LeadStage.ClosedLost),
            PipelineValue = leads.Where(l => l.Stage < LeadStage.ClosedLost).Sum(l => l.DealValue),
            DaysToGo = days >= 0 ? days : null, RecentLeads = recent, CanEdit = _me.IsAdmin
        });
    }

    public async Task<Result<EventInput>> GetInputAsync(int id)
    {
        if (!_me.IsAdmin) return Result<EventInput>.From(Result.Forbidden("Only admins can manage events."));
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result<EventInput>.From(Result.NotFound("Event"));
        return Result<EventInput>.Success(new EventInput { Name = e.Name, Type = e.Type, Year = e.Year, StartDate = e.StartDate, EndDate = e.EndDate, Location = e.Location, Description = e.Description });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Result? Validate(EventInput i) =>
        Array.IndexOf(Types, i.Type) < 0 ? Result.Invalid(nameof(EventInput.Type), "Choose a valid event type.") : null;

    private static void Apply(MarketingEvent e, EventInput i)
    {
        e.Name = i.Name.Trim(); e.Type = i.Type; e.StartDate = i.StartDate!.Value.Date; e.EndDate = i.EndDate?.Date;
        e.Year = i.Year ?? e.StartDate.Year; e.Location = Clean(i.Location); e.Description = Clean(i.Description);
    }

    public async Task<Result<int>> CreateAsync(EventInput input)
    {
        if (!_me.IsAdmin) return Result<int>.From(Result.Forbidden("Only admins can manage events."));
        var bad = Validate(input); if (bad != null) return Result<int>.From(bad);
        var e = new MarketingEvent(); Apply(e, input);
        _db.Events.Add(e);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("EventCreated", "Event", e.Id, e.Name);
        return Result<int>.Success(e.Id);
    }

    public async Task<Result> UpdateAsync(int id, EventInput input)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Only admins can manage events.");
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result.NotFound("Event");
        var bad = Validate(input); if (bad != null) return bad;
        Apply(e, input);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("EventUpdated", "Event", id, e.Name);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden("Only admins can manage events.");
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return Result.NotFound("Event");
        if (await _db.Leads.AnyAsync(l => l.EventId == id))
            return Result.Conflict("This event has registered leads and cannot be deleted.");
        _db.Events.Remove(e);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("EventDeleted", "Event", id, e.Name);
        return Result.Success();
    }
}
