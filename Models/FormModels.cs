using System.ComponentModel.DataAnnotations;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Models;

public class FormConnectionInput : IValidatableObject
{
    [Required(ErrorMessage = "Give the connection a name."), StringLength(120)] public string Name { get; set; } = "";
    public FormProvider Provider { get; set; } = FormProvider.Tally;
    public bool IsActive { get; set; } = true;
    public int? EventId { get; set; }
    public AssignmentMode Assignment { get; set; } = AssignmentMode.RoundRobin;
    public int? AssignToUserId { get; set; }
    [StringLength(300)] public string? DefaultTags { get; set; }
    [StringLength(2000)] public string? FieldMapping { get; set; }
    /// <summary>Write-only. Leave blank to keep the current secret.</summary>
    [StringLength(200)] public string? SigningSecret { get; set; }
    public bool ClearSigningSecret { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (Assignment == AssignmentMode.SpecificUser && AssignToUserId is null)
            yield return new ValidationResult("Choose who should receive the leads.", new[] { nameof(AssignToUserId) });
    }
}

public record FormConnectionListItem(int Id, string Name, FormProvider Provider, bool IsActive, string? EventName,
    int TotalReceived, int TotalCreated, int TotalDuplicate, int TotalFailed, DateTime? LastReceivedUtc);

public record SubmissionItem(long Id, DateTime ReceivedUtc, SubmissionStatus Status, int? LeadId, string? LeadName, string? Error, string? Summary);

public class FormConnectionDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public FormProvider Provider { get; set; }
    public bool IsActive { get; set; }
    public int? EventId { get; set; }
    public string? EventName { get; set; }
    public AssignmentMode Assignment { get; set; }
    public int? AssignToUserId { get; set; }
    public string? AssignToUserName { get; set; }
    public string? DefaultTags { get; set; }
    public string? FieldMapping { get; set; }
    public bool HasSigningSecret { get; set; }
    public string WebhookUrl { get; set; } = "";
    public int TotalReceived { get; set; }
    public int TotalCreated { get; set; }
    public int TotalDuplicate { get; set; }
    public int TotalFailed { get; set; }
    public DateTime? LastReceivedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<SubmissionItem> Submissions { get; set; } = new();
}

public class FormPreviewInput
{
    [Required(ErrorMessage = "Paste a sample payload."), StringLength(100000)] public string Payload { get; set; } = "";
}

public class FormPreviewResult
{
    public bool Parsed { get; set; }
    public string? Error { get; set; }
    public string? ExternalId { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();
    public List<string> Unmapped { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool WouldCreate { get; set; }
    public int? ExistingLeadId { get; set; }
}
