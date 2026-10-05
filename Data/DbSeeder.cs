using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Helpers;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Data;

public class SeedUser
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "SalesRep";
    public string Password { get; set; } = "";
}

public static class DbSeeder
{
    public static async Task InitialiseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var cfg = sp.GetRequiredService<IConfiguration>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");
        var passwords = sp.GetRequiredService<PasswordService>();

        if (cfg.GetValue("Database:UseMigrations", false)) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();

        if (!await db.Users.AnyAsync())
        {
            var mustChange = cfg.GetValue("Seed:MustChangePassword", false);
            var configured = cfg.GetSection("Seed:Users").Get<List<SeedUser>>() ?? new List<SeedUser>();

            if (configured.Count == 0)
            {
                // Nothing configured: create one admin with a random one-time password that is written to the log.
                var email = AuthService.NormalizeEmail(cfg["Seed:AdminEmail"] ?? "admin@uncoveringgreatness.co.za");
                var pw = PasswordService.GenerateTemporary();
                var admin = new User { FullName = "System Administrator", Email = email, Role = UserRole.Admin, MustChangePassword = true };
                admin.PasswordHash = passwords.Hash(admin, pw);
                db.Users.Add(admin);
                await db.SaveChangesAsync();
                log.LogWarning("=== FIRST RUN === Admin account created: {Email} | one-time password: {Password} | you must change it at first sign-in.", email, pw);
            }
            else
            {
                foreach (var s in configured)
                {
                    if (string.IsNullOrWhiteSpace(s.Email) || string.IsNullOrWhiteSpace(s.FullName))
                        throw new InvalidOperationException("Every Seed:Users entry needs a FullName and Email.");
                    var problems = PasswordService.Validate(s.Password, s.Email, s.FullName);
                    if (problems.Count > 0)
                        throw new InvalidOperationException($"Seed password for {s.Email} is not acceptable: {string.Join(" ", problems)}");
                    if (!Enum.TryParse<UserRole>(s.Role, true, out var role))
                        throw new InvalidOperationException($"Seed user {s.Email} has an unknown role '{s.Role}' (use Admin, SalesRep or Staff).");
                    var u = new User { FullName = s.FullName.Trim(), Email = AuthService.NormalizeEmail(s.Email), Role = role, MustChangePassword = mustChange };
                    u.PasswordHash = passwords.Hash(u, s.Password);
                    db.Users.Add(u);
                }
                await db.SaveChangesAsync();
                log.LogWarning("Seeded {Count} user(s) from configuration{Note}. Change these passwords before going live.", configured.Count,
                    mustChange ? " (password change required at first sign-in)" : "");
            }
        }

        if (cfg.GetValue("Seed:SampleData", false) && !await db.Leads.AnyAsync()) await SeedSampleDataAsync(db);
    }

    private static async Task SeedSampleDataAsync(AppDbContext db)
    {
        // Everything is shared out across whoever can own leads (active admins and sales reps), so each seeded user gets leads.
        var team = await db.Users.Where(u => u.IsActive && u.Role != UserRole.Staff).OrderBy(u => u.Id).ToListAsync();
        if (team.Count == 0) return;
        User T(int i) => team[i % team.Count];
        var today = Clock.LocalToday;

        Company C(string name, string industry, string size, string city, string web, string contact, string phone, string province = "Gauteng") =>
            new() { Name = name, Industry = industry, Size = size, City = city, Province = province, Website = web, ContactPerson = contact, ContactPhone = phone,
                    ContactEmail = contact.ToLower().Replace(' ', '.') + "@" + web.Replace("https://", ""), AddressLine = "14 Rivonia Road" };
        var summit = C("Summit Retail Group", "Retail & FMCG", "500–1,000", "Johannesburg", "https://summitretail.co.za", "Naledi Khumalo", "+27 11 555 0100");
        var northwind = C("Northwind Media", "Media & Publishing", "51–200", "Cape Town", "https://northwindmedia.co.za", "Amara Okafor", "+27 21 555 0142", "Western Cape");
        var vantage = C("Vantage Logistics", "Logistics", "1,000+", "Durban", "https://vantagelogistics.co.za", "Riaan Botha", "+27 31 555 0177", "KwaZulu-Natal");
        var baobab = C("Baobab Finance", "Financial Services", "201–500", "Sandton", "https://baobabfinance.co.za", "Zanele Mahlangu", "+27 11 555 0166");
        var highveld = C("Highveld Tech", "Technology", "51–200", "Pretoria", "https://highveldtech.co.za", "David Pretorius", "+27 12 555 0119");
        var kaya = C("Kaya Wellness", "Health & Wellness", "1–50", "Johannesburg", "https://kayawellness.co.za", "Lerato Mabaso", "+27 82 555 0121");
        db.Companies.AddRange(summit, northwind, vantage, baobab, highveld, kaya);

        var expo = new MarketingEvent { Name = "Summit Leadership Expo 2026", Type = "Conference", Year = 2026, StartDate = new DateTime(2026, 10, 14), EndDate = new DateTime(2026, 10, 16), Location = "Sandton Convention Centre, Johannesburg", Description = "A three-day flagship conference bringing together operations and procurement leaders from across Southern Africa. Includes keynote sessions, an exhibitor hall, and curated networking breakfasts." };
        var breakfast = new MarketingEvent { Name = "Founders & Funders Breakfast", Type = "Networking", Year = 2026, StartDate = new DateTime(2026, 11, 5), Location = "The Venue, Rosebank", Description = "An intimate breakfast connecting founders with investors and growth partners." };
        var forum = new MarketingEvent { Name = "Women in Business Forum", Type = "Forum", Year = 2026, StartDate = new DateTime(2026, 3, 19), Location = "Century City, Cape Town", Description = "A day of talks and workshops celebrating women driving commercial growth." };
        var roadshow2025 = new MarketingEvent { Name = "Growth Roadshow 2025", Type = "Roadshow", Year = 2025, StartDate = new DateTime(2025, 9, 9), EndDate = new DateTime(2025, 9, 12), Location = "Multiple cities", Description = "A four-city sales training roadshow." };
        db.Events.AddRange(expo, breakfast, forum, roadshow2025);
        await db.SaveChangesAsync();

        var rng = new Random(2026);
        Lead L(int owner, string first, string sur, string title, string email, Company co, MarketingEvent ev, LeadStage stage, decimal value, LeadSource src, DateTime? follow, string tags = "")
        {
            var rep = T(owner);
            var l = new Lead
            {
                FirstName = first, Surname = sur, JobTitle = title, Email = email, Company = co, Event = ev, Stage = stage, DealValue = value, Source = src,
                AssignedToId = rep.Id, NextFollowUp = follow, Tags = string.IsNullOrEmpty(tags) ? null : tags, Industry = co.Industry,
                City = co.City, Province = co.Province, Phone = "+27 82 555 0" + rng.Next(100, 999), PreferredContact = "Email",
                RegistrationDate = today.AddDays(-rng.Next(10, 120)), CreatedUtc = DateTime.UtcNow.AddDays(-rng.Next(1, 60)),
                LastContactedUtc = DateTime.UtcNow.AddDays(-rng.Next(0, 9)),
                ClosedUtc = stage is LeadStage.ClosedWon or LeadStage.ClosedLost ? DateTime.UtcNow.AddDays(-rng.Next(0, 12)) : null
            };
            l.Activities.Add(new LeadActivity { User = team[0], Type = ActivityType.Created, Title = "Lead created", Detail = $"Source: {src.Label()}", CreatedUtc = l.CreatedUtc });
            l.Activities.Add(new LeadActivity { User = team[0], Type = ActivityType.Assigned, Title = "Assigned", Detail = $"Lead assigned to {rep.FullName}", CreatedUtc = l.CreatedUtc.AddMinutes(5) });
            return l;
        }

        var naledi = L(0, "Naledi", "Khumalo", "Head of Procurement", "naledi.khumalo@summitretail.co.za", summit, expo, LeadStage.Qualified, 84000, LeadSource.Website, today.AddHours(14), "Enterprise, Priority");
        naledi.LinkedInUrl = "https://linkedin.com/in/naledikhumalo";
        naledi.Notes.Add(new LeadNote { Author = naledi.AssignedToId == team[0].Id ? team[0] : T(0), Body = "Spoke with Naledi about rolling the platform out across 6 regional stores. She wants a formal proposal before end of month.", CreatedUtc = DateTime.UtcNow.AddDays(-1) });
        naledi.Notes.Add(new LeadNote { Author = T(2), Body = "Confirmed budget range is R70k–R100k for the initial rollout phase.", CreatedUtc = DateTime.UtcNow.AddDays(-8) });
        naledi.Activities.Add(new LeadActivity { User = T(0), Type = ActivityType.CallLogged, Title = "Call logged", Detail = "18-minute call: discovery call regarding regional rollout", CreatedUtc = DateTime.UtcNow.AddDays(-1) });
        naledi.Activities.Add(new LeadActivity { User = T(0), Type = ActivityType.StageChanged, Title = "Stage changed", Detail = $"Lead → Qualified, updated by {T(0).FullName}", CreatedUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(4) });

        var leads = new List<Lead>
        {
            naledi,
            L(1, "Thabo", "Sithole", "Regional Buyer", "thabo.sithole@summitretail.co.za", summit, expo, LeadStage.Lead, 46000, LeadSource.Event, today.AddDays(2)),
            L(2, "Palesa", "Mokwena", "Store Ops Lead", "palesa.mokwena@summitretail.co.za", summit, expo, LeadStage.Proposal, 92000, LeadSource.Referral, today.AddDays(4), "Enterprise"),
            L(3, "Amara", "Okafor", "Marketing Director", "amara.okafor@northwindmedia.co.za", northwind, breakfast, LeadStage.Lead, 32500, LeadSource.WebForm, today.AddDays(1)),
            L(0, "Riaan", "Botha", "Operations Manager", "riaan.botha@vantagelogistics.co.za", vantage, forum, LeadStage.ClosedWon, 184000, LeadSource.Event, null, "Enterprise"),
            L(1, "Mpho", "Sithole", "Fleet Director", "mpho.sithole@vantagelogistics.co.za", vantage, forum, LeadStage.ClosedWon, 96000, LeadSource.Website, null),
            L(2, "Zanele", "Mahlangu", "Head of Learning", "zanele.mahlangu@baobabfinance.co.za", baobab, expo, LeadStage.Proposal, 210000, LeadSource.Website, today.AddDays(-1), "Priority"),
            L(3, "Kagiso", "Tau", "HR Business Partner", "kagiso.tau@baobabfinance.co.za", baobab, expo, LeadStage.Proposal, 74000, LeadSource.Social, today.AddDays(5)),
            L(0, "David", "Pretorius", "CTO", "david.pretorius@highveldtech.co.za", highveld, breakfast, LeadStage.Negotiation, 156000, LeadSource.Referral, today.AddDays(3), "Priority"),
            L(1, "Johan", "van Wyk", "Sales Director", "johan.vanwyk@highveldtech.co.za", highveld, expo, LeadStage.Qualified, 64000, LeadSource.ColdCall, today),
            L(2, "Lerato", "Mabaso", "Founder", "lerato.mabaso@kayawellness.co.za", kaya, forum, LeadStage.Lead, 18000, LeadSource.WebForm, today.AddDays(6)),
            L(3, "Sanele", "Zulu", "Operations Lead", "sanele.zulu@kayawellness.co.za", kaya, roadshow2025, LeadStage.ClosedLost, 22000, LeadSource.Other, null),
            L(0, "Nomsa", "Dlamini", "Learning & Development", "nomsa.dlamini@northwindmedia.co.za", northwind, expo, LeadStage.Qualified, 58000, LeadSource.Website, today.AddDays(-2)),
            L(1, "Ethan", "Naidoo", "Commercial Manager", "ethan.naidoo@vantagelogistics.co.za", vantage, expo, LeadStage.Negotiation, 132000, LeadSource.Event, today.AddDays(1)),
            L(2, "Keabetswe", "Molefe", "Events Coordinator", "keabetswe.molefe@summitretail.co.za", summit, breakfast, LeadStage.Qualified, 41000, LeadSource.WebForm, today.AddDays(7)),
            L(3, "Pieter", "Jacobs", "Head of Sales", "pieter.jacobs@highveldtech.co.za", highveld, forum, LeadStage.Lead, 27000, LeadSource.Social, today.AddDays(3))
        };
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync();

        db.LeadStars.Add(new LeadStar { LeadId = naledi.Id, UserId = T(0).Id });
        db.LeadStars.Add(new LeadStar { LeadId = leads[8].Id, UserId = T(0).Id });

        db.Tasks.AddRange(
            new CrmTask { Title = "Call Naledi Khumalo", Description = "Pricing discussion follow-up", DueAt = today.AddHours(9), Priority = TaskPriority.Low, AssignedToId = T(0).Id, CreatedById = T(0).Id, LeadId = naledi.Id, IsCompleted = true, CompletedUtc = DateTime.UtcNow.AddHours(-3) },
            new CrmTask { Title = "Send proposal to Baobab Finance", Description = "Finalise pricing for regional rollout", DueAt = today.AddHours(12), Priority = TaskPriority.High, AssignedToId = T(2).Id, CreatedById = T(0).Id, LeadId = leads[6].Id },
            new CrmTask { Title = "Prep Summit Leadership Expo booth brief", Description = "Coordinate with events team", DueAt = today.AddHours(15.5), Priority = TaskPriority.Medium, AssignedToId = T(1).Id, CreatedById = T(0).Id },
            new CrmTask { Title = "Update Highveld Tech contract terms", Description = "Legal review needed first", DueAt = today.AddDays(1), Priority = TaskPriority.Low, AssignedToId = T(0).Id, CreatedById = T(0).Id, LeadId = leads[8].Id },
            new CrmTask { Title = "Follow up with Vantage Logistics finance", Description = "Confirm PO number", DueAt = today.AddDays(3), Priority = TaskPriority.Medium, AssignedToId = T(3).Id, CreatedById = T(3).Id },
            new CrmTask { Title = "Send revised quote to Nomsa Dlamini", DueAt = today.AddDays(-1), Priority = TaskPriority.High, AssignedToId = T(0).Id, CreatedById = T(0).Id, LeadId = leads[12].Id });

        var ce1 = new CalendarEvent { Title = "Discovery call — Summit Retail", StartsAt = today.AddDays(1).AddHours(10), Type = CalendarItemType.Call, OwnerId = T(0).Id, LinkedLeadId = naledi.Id, Visibility = team.Count > 1 ? CalendarVisibility.Shared : CalendarVisibility.Private };
        if (team.Count > 1) ce1.Shares.Add(new CalendarShare { UserId = T(1).Id });
        db.CalendarEvents.AddRange(ce1,
            new CalendarEvent { Title = "Summit Leadership Expo — booth setup", StartsAt = new DateTime(2026, 10, 13, 8, 0, 0), Type = CalendarItemType.Meeting, OwnerId = T(0).Id, Visibility = CalendarVisibility.Team, LinkedEventId = expo.Id },
            new CalendarEvent { Title = "Pipeline review", StartsAt = today.AddDays(2).AddHours(14), Type = CalendarItemType.Meeting, OwnerId = T(1).Id, Visibility = CalendarVisibility.Private },
            new CalendarEvent { Title = "Quarterly planning", StartsAt = today.AddDays(5).AddHours(9), Type = CalendarItemType.Other, OwnerId = T(0).Id, Visibility = CalendarVisibility.Private });

        db.Campaigns.AddRange(
            new Campaign
            {
                Name = "Early bird reminder", Subject = "Your Summit Leadership Expo seat is waiting", Body = "Hi {{FirstName}},\n\nThe Summit Leadership Expo is just weeks away. We've saved your seat for three days of keynotes, workshops and networking with Southern Africa's operations leaders.\n\nSee you there!\nThe Uncovering Greatness team",
                Target = CampaignTarget.Event, TargetEventId = expo.Id, Status = CampaignStatus.Sent, CreatedById = T(0).Id, CreatedUtc = DateTime.UtcNow.AddDays(-12), SentUtc = DateTime.UtcNow.AddDays(-11),
                RecipientCount = 9, DeliveredCount = 9, OpenedCount = 6, FailedCount = 0
            },
            new Campaign
            {
                Name = "VIP breakfast invite", Subject = "An exclusive invitation for our top accounts", Body = "Hi {{FirstName}},\n\nWe would love to host you and {{Company}} at our Founders & Funders Breakfast on 5 November.\n\nWarm regards,\nUncovering Greatness",
                Target = CampaignTarget.Year, TargetYear = 2026, Status = CampaignStatus.Draft, CreatedById = T(0).Id
            });

        // a couple of in-app notifications for every team member
        foreach (var member in team)
        {
            var own = leads.FirstOrDefault(l => l.AssignedToId == member.Id);
            db.Notifications.Add(new Notification { UserId = member.Id, Type = NotificationType.LeadAssigned, Title = "Leads assigned to you", Body = "Your sample leads are ready in the Leads list.", LinkUrl = "/Leads?view=mine" });
            if (own != null)
                db.Notifications.Add(new Notification { UserId = member.Id, Type = NotificationType.FollowUp, Title = $"Follow-up coming up — {own.FullName}", Body = "Check the next step for this lead.", LinkUrl = $"/Leads/Details/{own.Id}", CreatedUtc = DateTime.UtcNow.AddHours(-2) });
        }
        await db.SaveChangesAsync();
    }
}
