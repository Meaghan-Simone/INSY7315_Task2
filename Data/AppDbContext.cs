using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<MarketingEvent> Events => Set<MarketingEvent>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadStar> LeadStars => Set<LeadStar>();
    public DbSet<LeadNote> LeadNotes => Set<LeadNote>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<CrmTask> Tasks => Set<CrmTask>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarShare> CalendarShares => Set<CalendarShare>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<FormConnection> FormConnections => Set<FormConnection>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Users are deactivated, never deleted; no cascade from a user to anything (also avoids
        // SQL Server "multiple cascade paths" errors).
        foreach (var fk in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetForeignKeys())
                     .Where(fk => fk.PrincipalEntityType.ClrType == typeof(User)))
        {
            fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        // Deleting a company/event must never silently delete leads; services block it when leads exist.
        modelBuilder.Entity<Lead>().HasOne(l => l.Company).WithMany(c => c.Leads)
            .HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Lead>().HasOne(l => l.Event).WithMany(e => e.Leads)
            .HasForeignKey(l => l.EventId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CrmTask>().HasOne(t => t.Lead).WithMany()
            .HasForeignKey(t => t.LeadId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CalendarEvent>().HasOne(c => c.LinkedLead).WithMany()
            .HasForeignKey(c => c.LinkedLeadId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CalendarEvent>().HasOne(c => c.LinkedCompany).WithMany()
            .HasForeignKey(c => c.LinkedCompanyId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CalendarEvent>().HasOne(c => c.LinkedEvent).WithMany()
            .HasForeignKey(c => c.LinkedEventId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Campaign>().HasOne(c => c.TargetEvent).WithMany()
            .HasForeignKey(c => c.TargetEventId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<FormConnection>().HasOne(f => f.Event).WithMany()
            .HasForeignKey(f => f.EventId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<FormSubmission>().HasOne(x => x.Lead).WithMany()
            .HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CampaignRecipient>().HasOne(r => r.Lead).WithMany()
            .HasForeignKey(r => r.LeadId).OnDelete(DeleteBehavior.Cascade);
    }
}
