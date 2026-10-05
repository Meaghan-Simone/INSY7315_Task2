namespace UncoveringGreatnessCRM.Domain;

public enum UserRole { Admin = 1, SalesRep = 2, Staff = 3 }
public enum LeadStage { Lead = 0, Qualified = 1, Proposal = 2, Negotiation = 3, ClosedWon = 4, ClosedLost = 5 }
public enum LeadSource { Website = 0, Referral = 1, Social = 2, Event = 3, ColdCall = 4, Other = 5, WebForm = 6 }
public enum TaskPriority { Low = 0, Medium = 1, High = 2 }
public enum CampaignTarget { All = 0, Year = 1, Event = 2 }
public enum CampaignStatus { Draft = 0, Sending = 1, Sent = 2 }
public enum RecipientStatus { Pending = 0, Delivered = 1, Failed = 2 }
public enum CalendarVisibility { Private = 0, Shared = 1, Team = 2 }
public enum CalendarItemType { Meeting = 0, Call = 1, FollowUp = 2, Other = 3 }
public enum ActivityType { Created = 0, Updated = 1, StageChanged = 2, Assigned = 3, CallLogged = 4, NoteAdded = 5, EmailSent = 6 }
public enum FormProvider { Tally = 0, GoogleForms = 1, Generic = 2 }
public enum AssignmentMode { RoundRobin = 0, LeastBusy = 1, SpecificUser = 2 }
public enum SubmissionStatus { Created = 0, Duplicate = 1, Rejected = 2, Error = 3 }
public enum NotificationType { FollowUp = 0, DealWon = 1, LeadAssigned = 2, TaskDue = 3, System = 4, CalendarReminder = 5 }

public static class Roles
{
    public const string Admin = "Admin";
    public const string SalesRep = "SalesRep";
    public const string Staff = "Staff";
    public const string AdminOrRep = "Admin,SalesRep";
}
