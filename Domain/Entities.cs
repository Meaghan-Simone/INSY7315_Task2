using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace UncoveringGreatnessCRM.Domain;

[Index(nameof(Email), IsUnique = true)]
public class User
{
    public int Id { get; set; }
    [MaxLength(120)] public string FullName { get; set; } = "";
    [MaxLength(200)] public string Email { get; set; } = "";
    [MaxLength(400)] public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.SalesRep;
    [MaxLength(150)] public string? JobTitle { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    /// <summary>Rotated whenever credentials/role/active flag change; invalidates existing sessions and tokens.</summary>
    [MaxLength(64)] public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Lead> AssignedLeads { get; set; } = new List<Lead>();
}

public class Company
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(150)] public string? Industry { get; set; }
    [MaxLength(40)] public string? Size { get; set; }
    [MaxLength(300)] public string? Website { get; set; }
    [MaxLength(250)] public string? AddressLine { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(60)] public string? Province { get; set; }
    [MaxLength(150)] public string? ContactPerson { get; set; }
    [MaxLength(40)] public string? ContactPhone { get; set; }
    [MaxLength(200)] public string? ContactEmail { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
}

public class MarketingEvent
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(40)] public string Type { get; set; } = "Conference";
    public int Year { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    [MaxLength(250)] public string? Location { get; set; }
    [MaxLength(4000)] public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
}

[Index(nameof(Email), IsUnique = true)]
[Index(nameof(Stage))]
public class Lead
{
    public int Id { get; set; }
    [MaxLength(100)] public string FirstName { get; set; } = "";
    [MaxLength(100)] public string Surname { get; set; } = "";
    [MaxLength(150)] public string? JobTitle { get; set; }
    [MaxLength(150)] public string? Industry { get; set; }
    [MaxLength(200)] public string Email { get; set; } = "";
    [MaxLength(40)] public string? Phone { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(60)] public string? Province { get; set; }
    [MaxLength(300)] public string? LinkedInUrl { get; set; }
    [MaxLength(30)] public string? PreferredContact { get; set; }
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? EventId { get; set; }
    public MarketingEvent? Event { get; set; }
    public DateTime? RegistrationDate { get; set; }
    public LeadSource Source { get; set; } = LeadSource.Website;
    public LeadStage Stage { get; set; } = LeadStage.Lead;
    [Precision(18, 2)] public decimal DealValue { get; set; }
    public int? AssignedToId { get; set; }
    public User? AssignedTo { get; set; }
    public DateTime? NextFollowUp { get; set; }
    public DateTime? LastContactedUtc { get; set; }
    [MaxLength(300)] public string? Tags { get; set; }
    public bool OptedOut { get; set; }
    [MaxLength(64)] public string UnsubscribeToken { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? ClosedUtc { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<LeadNote> Notes { get; set; } = new List<LeadNote>();
    public ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();

    [NotMapped] public string FullName => $"{FirstName} {Surname}".Trim();
}

[PrimaryKey(nameof(LeadId), nameof(UserId))]
public class LeadStar
{
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}

public class LeadNote
{
    public int Id { get; set; }
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? AuthorId { get; set; }
    public User? Author { get; set; }
    [MaxLength(4000)] public string Body { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class LeadActivity
{
    public int Id { get; set; }
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public ActivityType Type { get; set; }
    [MaxLength(500)] public string Title { get; set; } = "";
    [MaxLength(2000)] public string? Detail { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

[Index(nameof(AssignedToId), nameof(IsCompleted))]
public class CrmTask
{
    public int Id { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(2000)] public string? Description { get; set; }
    public DateTime? DueAt { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public bool IsCompleted { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int AssignedToId { get; set; }
    public User? AssignedTo { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class Campaign
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Subject { get; set; } = "";
    [MaxLength(20000)] public string Body { get; set; } = "";
    public CampaignTarget Target { get; set; } = CampaignTarget.All;
    public int? TargetYear { get; set; }
    public int? TargetEventId { get; set; }
    public MarketingEvent? TargetEvent { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
    public int RecipientCount { get; set; }
    public int DeliveredCount { get; set; }
    public int OpenedCount { get; set; }
    public int FailedCount { get; set; }
    public ICollection<CampaignRecipient> Recipients { get; set; } = new List<CampaignRecipient>();
}

[Index(nameof(TrackingToken), IsUnique = true)]
public class CampaignRecipient
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    [MaxLength(64)] public string TrackingToken { get; set; } = Guid.NewGuid().ToString("N");
    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;
    [MaxLength(500)] public string? Error { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? OpenedUtc { get; set; }
}

[Index(nameof(OwnerId), nameof(StartsAt))]
public class CalendarEvent
{
    public int Id { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public CalendarItemType Type { get; set; } = CalendarItemType.Meeting;
    [MaxLength(2000)] public string? Notes { get; set; }
    public CalendarVisibility Visibility { get; set; } = CalendarVisibility.Private;
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public int? LinkedLeadId { get; set; }
    public Lead? LinkedLead { get; set; }
    public int? LinkedCompanyId { get; set; }
    public Company? LinkedCompany { get; set; }
    public int? LinkedEventId { get; set; }
    public MarketingEvent? LinkedEvent { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<CalendarShare> Shares { get; set; } = new List<CalendarShare>();
}

[PrimaryKey(nameof(CalendarEventId), nameof(UserId))]
public class CalendarShare
{
    public int CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}

[Index(nameof(UserId), nameof(IsRead))]
[Index(nameof(UserId), nameof(DedupeKey), IsUnique = true)]
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public NotificationType Type { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(500)] public string? Body { get; set; }
    [MaxLength(300)] public string? LinkUrl { get; set; }
    [MaxLength(120)] public string? DedupeKey { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

[Index(nameof(CreatedUtc))]
public class AuditLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    [MaxLength(80)] public string Action { get; set; } = "";
    [MaxLength(80)] public string? Entity { get; set; }
    [MaxLength(40)] public string? EntityId { get; set; }
    [MaxLength(1000)] public string? Details { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A linked online form (Tally, Google Forms or any webhook sender) whose submissions automatically become leads.</summary>
[Index(nameof(Token), IsUnique = true)]
public class FormConnection
{
    public int Id { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    public FormProvider Provider { get; set; } = FormProvider.Tally;
    /// <summary>Unguessable secret that is part of the webhook URL (256-bit random, URL-safe).</summary>
    [MaxLength(64)] public string Token { get; set; } = "";
    /// <summary>Optional HMAC signing secret, encrypted at rest with Data Protection.</summary>
    [MaxLength(800)] public string? SigningSecretProtected { get; set; }
    public bool IsActive { get; set; } = true;
    public int? EventId { get; set; }
    public MarketingEvent? Event { get; set; }
    public AssignmentMode Assignment { get; set; } = AssignmentMode.RoundRobin;
    public int? AssignToUserId { get; set; }
    public User? AssignToUser { get; set; }
    public int? LastAssignedUserId { get; set; }
    [MaxLength(300)] public string? DefaultTags { get; set; }
    /// <summary>Optional overrides, one per line: LeadField = Exact question label.</summary>
    [MaxLength(2000)] public string? FieldMapping { get; set; }
    public int TotalReceived { get; set; }
    public int TotalCreated { get; set; }
    public int TotalDuplicate { get; set; }
    public int TotalFailed { get; set; }
    public DateTime? LastReceivedUtc { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<FormSubmission> Submissions { get; set; } = new List<FormSubmission>();
}

[Index(nameof(ConnectionId), nameof(ExternalId), IsUnique = true)]
public class FormSubmission
{
    public long Id { get; set; }
    public int ConnectionId { get; set; }
    public FormConnection? Connection { get; set; }
    /// <summary>The form provider's submission/response id, used to ignore retried deliveries.</summary>
    [MaxLength(120)] public string? ExternalId { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public SubmissionStatus Status { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    /// <summary>Short human summary (name and email). The raw payload is deliberately not stored.</summary>
    [MaxLength(300)] public string? Summary { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
}
