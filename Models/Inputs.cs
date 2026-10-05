using System.ComponentModel.DataAnnotations;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Models;

// Request models shared by the MVC forms and the JSON API. Optional values are nullable on purpose:
// with nullable reference types enabled, MVC treats non-nullable strings as required.

public class LoginInput
{
    [Required(ErrorMessage = "Enter your email address."), EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";
    [Required(ErrorMessage = "Enter your password."), StringLength(128), DataType(DataType.Password)]
    public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
}

public class ForgotPasswordInput
{
    [Required(ErrorMessage = "Enter your email address."), EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";
}

public class ResetPasswordInput
{
    [Required] public string Token { get; set; } = "";
    [Required, StringLength(128), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}

public class ChangePasswordInput
{
    [Required(ErrorMessage = "Enter your current password."), StringLength(128), DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";
    [Required(ErrorMessage = "Choose a new password."), StringLength(128), DataType(DataType.Password)]
    public string NewPassword { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}

public class ProfileInput
{
    [Required(ErrorMessage = "Enter your name."), StringLength(120)]
    public string FullName { get; set; } = "";
}

public class LeadInput
{
    [Required(ErrorMessage = "First name is required."), StringLength(100)] public string FirstName { get; set; } = "";
    [Required(ErrorMessage = "Surname is required."), StringLength(100)] public string Surname { get; set; } = "";
    [StringLength(150)] public string? JobTitle { get; set; }
    [StringLength(150)] public string? Industry { get; set; }
    [Required(ErrorMessage = "Email is required."), EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [Phone, StringLength(40)] public string? Phone { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(60)] public string? Province { get; set; }
    [Url, StringLength(300)] public string? LinkedInUrl { get; set; }
    [StringLength(30)] public string? PreferredContact { get; set; }
    public int? CompanyId { get; set; }
    public int? EventId { get; set; }
    [DataType(DataType.Date)] public DateTime? RegistrationDate { get; set; }
    public LeadSource Source { get; set; } = LeadSource.Website;
    public LeadStage Stage { get; set; } = LeadStage.Lead;
    [Range(0, 1_000_000_000, ErrorMessage = "Deal value must be between 0 and 1,000,000,000.")] public decimal DealValue { get; set; }
    public int? AssignedToId { get; set; }
    [DataType(DataType.Date)] public DateTime? NextFollowUp { get; set; }
    [StringLength(300)] public string? Tags { get; set; }
}

public class NoteInput
{
    [Required(ErrorMessage = "Write something first."), StringLength(4000)] public string Body { get; set; } = "";
}

public class CallInput
{
    [Range(0, 600, ErrorMessage = "Duration must be between 0 and 600 minutes.")] public int DurationMinutes { get; set; } = 10;
    [Required(ErrorMessage = "Add a short summary of the call."), StringLength(2000)] public string Summary { get; set; } = "";
    [DataType(DataType.Date)] public DateTime? NextFollowUp { get; set; }
}

public class StageInput { public LeadStage Stage { get; set; } }
public class AssignInput { public int? UserId { get; set; } }

public class CompanyInput
{
    [Required(ErrorMessage = "Company name is required."), StringLength(200)] public string Name { get; set; } = "";
    [StringLength(150)] public string? Industry { get; set; }
    [StringLength(40)] public string? Size { get; set; }
    [Url, StringLength(300)] public string? Website { get; set; }
    [StringLength(250)] public string? AddressLine { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(60)] public string? Province { get; set; }
    [StringLength(150)] public string? ContactPerson { get; set; }
    [Phone, StringLength(40)] public string? ContactPhone { get; set; }
    [EmailAddress, StringLength(200)] public string? ContactEmail { get; set; }
    [StringLength(2000)] public string? Notes { get; set; }
}

public class EventInput : IValidatableObject
{
    [Required(ErrorMessage = "Event name is required."), StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(40)] public string Type { get; set; } = "Conference";
    [Range(2000, 2100, ErrorMessage = "Enter a valid year.")] public int? Year { get; set; }
    [Required(ErrorMessage = "Event date is required."), DataType(DataType.Date)] public DateTime? StartDate { get; set; }
    [DataType(DataType.Date)] public DateTime? EndDate { get; set; }
    [StringLength(250)] public string? Location { get; set; }
    [StringLength(4000)] public string? Description { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate < StartDate)
            yield return new ValidationResult("End date cannot be before the start date.", new[] { nameof(EndDate) });
    }
}

public class TaskInput
{
    [Required(ErrorMessage = "Give the task a title."), StringLength(200)] public string Title { get; set; } = "";
    [StringLength(2000)] public string? Description { get; set; }
    public DateTime? DueAt { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public int? AssignedToId { get; set; }
    public int? LeadId { get; set; }
}

public class CalendarInput
{
    [Required(ErrorMessage = "Give the event a title."), StringLength(200)] public string Title { get; set; } = "";
    [Required(ErrorMessage = "Pick a date."), DataType(DataType.Date)] public DateTime? Date { get; set; }
    [Required(ErrorMessage = "Pick a time."), DataType(DataType.Time)] public TimeSpan? Time { get; set; }
    public CalendarItemType Type { get; set; } = CalendarItemType.Meeting;
    [StringLength(2000)] public string? Notes { get; set; }
    public CalendarVisibility Visibility { get; set; } = CalendarVisibility.Private;
    public List<int> SharedWith { get; set; } = new();
    /// <summary>Optional link to a record: Lead, Company or Event.</summary>
    [StringLength(20)] public string? LinkedType { get; set; }
    public int? LinkedId { get; set; }
}

public class CampaignInput : IValidatableObject
{
    [Required(ErrorMessage = "Give the campaign a name."), StringLength(200)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "A subject line is required."), StringLength(300)] public string Subject { get; set; } = "";
    [Required(ErrorMessage = "Write the email body."), StringLength(20000)] public string Body { get; set; } = "";
    public CampaignTarget Target { get; set; } = CampaignTarget.All;
    [Range(2000, 2100)] public int? TargetYear { get; set; }
    public int? TargetEventId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (Target == CampaignTarget.Year && TargetYear is null)
            yield return new ValidationResult("Choose a registration year.", new[] { nameof(TargetYear) });
        if (Target == CampaignTarget.Event && TargetEventId is null)
            yield return new ValidationResult("Choose a target event.", new[] { nameof(TargetEventId) });
    }
}

public class UserCreateInput
{
    [Required(ErrorMessage = "Full name is required."), StringLength(120)] public string FullName { get; set; } = "";
    [Required(ErrorMessage = "Email is required."), EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [StringLength(150)] public string? JobTitle { get; set; }
    [Phone, StringLength(40)] public string? Phone { get; set; }
    public UserRole Role { get; set; } = UserRole.SalesRep;
    [Required(ErrorMessage = "Set a temporary password."), StringLength(128), DataType(DataType.Password)] public string TemporaryPassword { get; set; } = "";
}

public class UserUpdateInput
{
    [Required(ErrorMessage = "Full name is required."), StringLength(120)] public string FullName { get; set; } = "";
    [Required(ErrorMessage = "Email is required."), EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [StringLength(150)] public string? JobTitle { get; set; }
    [Phone, StringLength(40)] public string? Phone { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AdminResetPasswordInput
{
    [Required, StringLength(128), DataType(DataType.Password)] public string TemporaryPassword { get; set; } = "";
}
