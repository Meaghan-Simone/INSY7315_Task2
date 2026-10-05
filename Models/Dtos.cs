using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Models;

// Read models returned by services. The JSON API returns these directly; MVC views render them.

public record UserSummary(int Id, string FullName, string Email, UserRole Role, bool IsActive, bool IsLocked, int AssignedLeads, DateTime CreatedUtc, DateTime? LastLoginUtc, string? JobTitle = null, string? Phone = null);
public record UserOption(int Id, string FullName, UserRole Role);
public record Option(int Id, string Name);

public record LeadListItem(int Id, string FullName, string? JobTitle, int? CompanyId, string? CompanyName, LeadStage Stage,
    decimal DealValue, LeadSource Source, int? AssignedToId, string? AssignedToName, DateTime? NextFollowUp, bool Starred, string? EventName);

public record NoteDto(int Id, int? AuthorId, string AuthorName, string Body, DateTime CreatedUtc);
public record ActivityDto(int Id, ActivityType Type, string Title, string? Detail, string? UserName, DateTime CreatedUtc);

public class LeadDetail
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string FullName => $"{FirstName} {Surname}".Trim();
    public string? JobTitle { get; set; }
    public string? Industry { get; set; }
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? PreferredContact { get; set; }
    public int? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyIndustry { get; set; }
    public string? CompanySize { get; set; }
    public int? EventId { get; set; }
    public string? EventName { get; set; }
    public string? EventLocation { get; set; }
    public DateTime? RegistrationDate { get; set; }
    public LeadSource Source { get; set; }
    public LeadStage Stage { get; set; }
    public decimal DealValue { get; set; }
    public int? AssignedToId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? NextFollowUp { get; set; }
    public DateTime? LastContactedUtc { get; set; }
    public string? Tags { get; set; }
    public bool OptedOut { get; set; }
    public bool Starred { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public List<NoteDto> Notes { get; set; } = new();
    public List<ActivityDto> Activities { get; set; } = new();
    public IEnumerable<string> TagList => (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public record LeadQuery(string? Q = null, LeadStage? Stage = null, int? EventId = null, int? AssignedToId = null, int? CompanyId = null,
    string? View = null, string? Sort = null, int Page = 1, int PageSize = 25);

public record PipelineCard(int Id, string Name, string? Company, decimal Value, int? AssignedToId, string? AssignedToName);
public record PipelineColumn(LeadStage Stage, int Count, decimal Total, List<PipelineCard> Cards);
public record PipelineVm(List<PipelineColumn> Columns, int ActiveDeals, bool CanMove);

public record CompanyListItem(int Id, string Name, string? City, string? Industry, string? Size, int LeadCount, string? ContactPerson);
public class CompanyDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Industry { get; set; }
    public string? Size { get; set; }
    public string? Website { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? ContactPerson { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public decimal TotalPipeline { get; set; }
    public decimal WonValue { get; set; }
    public List<LeadListItem> Leads { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}
public record CompanyQuery(string? Q = null, string? Industry = null, string? Size = null, int Page = 1, int PageSize = 25);

public record EventListItem(int Id, string Name, string Type, DateTime StartDate, DateTime? EndDate, string? Location, int Attendees, bool IsUpcoming, List<string> AttendeeInitials);
public class EventDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Year { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public int Registered { get; set; }
    public int QualifiedLeads { get; set; }
    public decimal PipelineValue { get; set; }
    public int? DaysToGo { get; set; }
    public List<LeadListItem> RecentLeads { get; set; } = new();
    public bool CanEdit { get; set; }
}
public record EventQuery(string? Q = null, string? Type = null, int? Year = null, int Page = 1, int PageSize = 24);

public record TaskItem(int Id, string Title, string? Description, DateTime? DueAt, TaskPriority Priority, bool IsCompleted,
    int AssignedToId, string AssignedToName, int? LeadId, string? LeadName, bool IsOverdue, bool CanEdit);
public class TaskBoard
{
    public List<TaskItem> DueToday { get; set; } = new();
    public List<TaskItem> Overdue { get; set; } = new();
    public List<TaskItem> Upcoming { get; set; } = new();
    public List<TaskItem> Later { get; set; } = new();
    public List<TaskItem> Completed { get; set; } = new();
    public int OpenCount { get; set; }
    public int DueTodayCount { get; set; }
    public int OverdueCount { get; set; }
    public int ThisWeekCount { get; set; }
    public int CompletedThisMonth { get; set; }
}

public record CalendarItem(string Key, int? Id, string Title, DateTime StartsAt, CalendarItemType Type, string? Notes, CalendarVisibility Visibility,
    int OwnerId, string OwnerName, bool IsMine, bool IsReadOnly, string? LinkedLabel, string? LinkUrl, List<string> SharedWith);
public class CalendarMonth
{
    public int Year { get; set; }
    public int Month { get; set; }
    public List<CalendarItem> Items { get; set; } = new();
    public List<CalendarItem> Upcoming { get; set; } = new();
}

public record CampaignListItem(int Id, string Name, string Subject, CampaignTarget Target, CampaignStatus Status, int RecipientCount, DateTime? SentUtc, DateTime CreatedUtc);
public record CampaignFailure(string Email, string? Error);

public class CampaignDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public CampaignTarget Target { get; set; }
    public int? TargetYear { get; set; }
    public int? TargetEventId { get; set; }
    public string? TargetEventName { get; set; }
    public CampaignStatus Status { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public int RecipientCount { get; set; }
    public int DeliveredCount { get; set; }
    public int OpenedCount { get; set; }
    public int FailedCount { get; set; }
    public int AudienceSize { get; set; }
    public List<CampaignFailure> Failures { get; set; } = new();
    public double OpenRate => DeliveredCount == 0 ? 0 : Math.Round(OpenedCount * 100.0 / DeliveredCount, 1);
}

public record NotificationDto(int Id, NotificationType Type, string Title, string? Body, string? LinkUrl, bool IsRead, DateTime CreatedUtc);
public record AuditItem(long Id, string? UserName, string Action, string? Entity, string? EntityId, string? Details, string? IpAddress, DateTime CreatedUtc);

public class DashboardDto
{
    public int ActiveLeads { get; set; }
    public int NewLeadsThisMonth { get; set; }
    public decimal PipelineValue { get; set; }
    public decimal ClosedWonMtd { get; set; }
    public int ClosedWonCountMtd { get; set; }
    public int FollowUpsDue { get; set; }
    public int FollowUpsOverdue { get; set; }
    public double? LeadGrowthPct { get; set; }
    public List<StageStat> Stages { get; set; } = new();
    public List<LeadListItem> FollowUps { get; set; } = new();
    public List<LeadListItem> RecentLeads { get; set; } = new();
    public List<EventListItem> UpcomingEvents { get; set; } = new();
    public List<TaskItem> MyTasks { get; set; } = new();
    public int TotalLeads { get; set; }
    public int TotalCompanies { get; set; }
    public int TotalEvents { get; set; }
}
public record StageStat(LeadStage Stage, int Count, decimal Value);

public class ImportResult
{
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
}
