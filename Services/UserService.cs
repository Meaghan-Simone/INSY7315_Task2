using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class UserService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly AuthService _auth;
    private readonly IAuditService _audit;
    private readonly IUserStateCache _states;
    private readonly INotificationService _notify;

    public UserService(AppDbContext db, ICurrentUser me, AuthService auth, IAuditService audit, IUserStateCache states, INotificationService notify)
    { _db = db; _me = me; _auth = auth; _audit = audit; _states = states; _notify = notify; }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static UserSummary ToSummary(User u, int leads, DateTime now) =>
        new(u.Id, u.FullName, u.Email, u.Role, u.IsActive, u.LockoutEndUtc > now, leads, u.CreatedUtc, u.LastLoginUtc, u.JobTitle, u.Phone);

    /// <summary>Active users for assignment dropdowns. Available to every signed-in user.</summary>
    public Task<List<UserOption>> OptionsAsync() =>
        _db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.FullName)
            .Select(u => new UserOption(u.Id, u.FullName, u.Role)).ToListAsync();

    public async Task<Result<List<UserSummary>>> ListAsync(string? q = null)
    {
        if (!_me.IsAdmin) return Result<List<UserSummary>>.From(Result.Forbidden());
        var query = _db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(u => u.FullName.Contains(t) || u.Email.Contains(t) || (u.JobTitle != null && u.JobTitle.Contains(t)));
        }
        var now = DateTime.UtcNow;
        var users = await query.OrderBy(u => u.FullName).ToListAsync();
        var counts = await _db.Leads.AsNoTracking().Where(l => l.AssignedToId != null)
            .GroupBy(l => l.AssignedToId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync();
        var map = counts.Where(c => c.Id.HasValue).ToDictionary(c => c.Id!.Value, c => c.Count);
        return Result<List<UserSummary>>.Success(users.Select(u => ToSummary(u, map.GetValueOrDefault(u.Id), now)).ToList());
    }

    public async Task<Result<UserSummary>> GetAsync(int id)
    {
        if (!_me.IsAdmin) return Result<UserSummary>.From(Result.Forbidden());
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Result<UserSummary>.From(Result.NotFound("User"));
        var count = await _db.Leads.CountAsync(l => l.AssignedToId == id);
        return Result<UserSummary>.Success(ToSummary(u, count, DateTime.UtcNow));
    }

    public async Task<Result<UserSummary>> CreateAsync(UserCreateInput input)
    {
        if (!_me.IsAdmin) return Result<UserSummary>.From(Result.Forbidden());
        var email = AuthService.NormalizeEmail(input.Email);
        if (await _db.Users.AnyAsync(u => u.Email == email))
            return Result<UserSummary>.From(Result.Conflict("A user with that email already exists.", nameof(UserCreateInput.Email)));
        var errors = PasswordService.Validate(input.TemporaryPassword, email, input.FullName);
        if (errors.Count > 0) return Result<UserSummary>.From(Result.Invalid(nameof(UserCreateInput.TemporaryPassword), string.Join(" ", errors)));

        var user = new User { FullName = input.FullName.Trim(), Email = email, Role = input.Role, JobTitle = Clean(input.JobTitle), Phone = Clean(input.Phone) };
        _auth.SetPassword(user, input.TemporaryPassword, mustChange: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("UserCreated", "User", user.Id, $"{user.Email} as {user.Role}");
        return Result<UserSummary>.Success(ToSummary(user, 0, DateTime.UtcNow));
    }

    /// <summary>Edits every editable detail: name, email (the sign-in name), job title, phone, role and active status.</summary>
    public async Task<Result> UpdateAsync(int id, UserUpdateInput input)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return Result.NotFound("User");

        var email = AuthService.NormalizeEmail(input.Email);
        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != id))
            return Result.Conflict("Another user already has that email address.", nameof(UserUpdateInput.Email));

        var roleOrStatusChanged = user.Role != input.Role || user.IsActive != input.IsActive;
        var emailChanged = user.Email != email;
        if (id == _me.Id && (input.Role != UserRole.Admin || !input.IsActive))
            return Result.Conflict("You cannot remove your own admin access or deactivate your own account.");
        if (user.Role == UserRole.Admin && user.IsActive && (input.Role != UserRole.Admin || !input.IsActive))
        {
            var otherAdmins = await _db.Users.CountAsync(u => u.Id != id && u.Role == UserRole.Admin && u.IsActive);
            if (otherAdmins == 0) return Result.Conflict("At least one active admin is required.");
        }

        var before = $"{user.Email} {user.Role}/{(user.IsActive ? "active" : "inactive")}";
        user.FullName = input.FullName.Trim();
        user.Email = email;
        user.JobTitle = Clean(input.JobTitle);
        user.Phone = Clean(input.Phone);
        user.Role = input.Role;
        user.IsActive = input.IsActive;
        // Role/status changes, or a changed sign-in email on someone else's account, end their sessions so they re-authenticate.
        if (roleOrStatusChanged || (emailChanged && id != _me.Id)) user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        _states.Invalidate(id);
        await _audit.LogAsync("UserUpdated", "User", id, $"{before} -> {user.Email} {user.Role}/{(user.IsActive ? "active" : "inactive")}");
        return Result.Success();
    }

    /// <summary>
    /// Permanently deletes a user. Their leads and tasks are handed to <paramref name="transferToId"/>; notes, campaigns and activity
    /// they authored are kept but shown as written by a deleted user; their personal calendar, notifications and stars are removed.
    /// Everything happens in one transaction.
    /// </summary>
    public async Task<Result> DeleteAsync(int id, int? transferToId)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        if (id == _me.Id) return Result.Conflict("You cannot delete your own account.");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return Result.NotFound("User");

        if (user.Role == UserRole.Admin && user.IsActive)
        {
            var otherAdmins = await _db.Users.CountAsync(u => u.Id != id && u.Role == UserRole.Admin && u.IsActive);
            if (otherAdmins == 0) return Result.Conflict("At least one active admin is required.");
        }
        if (transferToId is null) return Result.Invalid("transferToId", "Choose who should take over this user's leads and tasks.");
        if (transferToId == id) return Result.Invalid("transferToId", "Choose a different user to take over.");
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == transferToId && u.IsActive && u.Role != UserRole.Staff);
        if (target is null) return Result.Invalid("transferToId", "The new owner must be an active admin or sales rep.");

        var leads = await _db.Leads.Where(l => l.AssignedToId == id).ToListAsync();
        foreach (var l in leads) l.AssignedToId = target.Id;

        var assignedTasks = await _db.Tasks.Where(t => t.AssignedToId == id).ToListAsync();
        foreach (var t in assignedTasks) t.AssignedToId = target.Id;
        foreach (var t in await _db.Tasks.Where(t => t.CreatedById == id).ToListAsync()) t.CreatedById = null;

        foreach (var n in await _db.LeadNotes.Where(n => n.AuthorId == id).ToListAsync()) n.AuthorId = null;
        foreach (var a in await _db.LeadActivities.Where(a => a.UserId == id).ToListAsync()) a.UserId = null;
        foreach (var c in await _db.Campaigns.Where(c => c.CreatedById == id).ToListAsync()) c.CreatedById = null;

        foreach (var s in await _db.CalendarShares.Where(s => s.UserId == id).ToListAsync()) _db.CalendarShares.Remove(s);
        foreach (var e in await _db.CalendarEvents.Where(e => e.OwnerId == id).ToListAsync()) _db.CalendarEvents.Remove(e);
        foreach (var n in await _db.Notifications.Where(n => n.UserId == id).ToListAsync()) _db.Notifications.Remove(n);
        foreach (var s in await _db.LeadStars.Where(s => s.UserId == id).ToListAsync()) _db.LeadStars.Remove(s);
        foreach (var c in await _db.FormConnections.Where(c => c.AssignToUserId == id).ToListAsync())
        {
            c.AssignToUserId = null;
            if (c.Assignment == AssignmentMode.SpecificUser) c.Assignment = AssignmentMode.RoundRobin;
        }

        var email = user.Email; var name = user.FullName;
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(); // single SaveChanges = single transaction: all of the above or none of it
        _states.Invalidate(id);

        await _audit.LogAsync("UserDeleted", "User", id,
            $"{name} <{email}>; {leads.Count} lead(s) and {assignedTasks.Count} task(s) transferred to {target.Email}");
        if (leads.Count + assignedTasks.Count > 0)
            await _notify.NotifyAsync(target.Id, NotificationType.LeadAssigned, "Records transferred to you",
                $"{_me.Name} transferred {leads.Count} lead(s) and {assignedTasks.Count} task(s) from {name} to you.", "/Leads?view=mine");
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(int id, string temporaryPassword)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return Result.NotFound("User");
        var errors = PasswordService.Validate(temporaryPassword, user.Email, user.FullName);
        if (errors.Count > 0) return Result.Invalid(nameof(AdminResetPasswordInput.TemporaryPassword), string.Join(" ", errors));
        _auth.SetPassword(user, temporaryPassword, mustChange: true);
        await _db.SaveChangesAsync();
        _states.Invalidate(id);
        await _audit.LogAsync("UserPasswordReset", "User", id, user.Email);
        return Result.Success();
    }

    public async Task<Result> UnlockAsync(int id)
    {
        if (!_me.IsAdmin) return Result.Forbidden();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return Result.NotFound("User");
        user.LockoutEndUtc = null; user.FailedLoginCount = 0;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("UserUnlocked", "User", id, user.Email);
        return Result.Success();
    }

    public async Task<Result> UpdateProfileAsync(string fullName)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == _me.Id);
        if (user is null) return Result.NotFound("User");
        user.FullName = fullName.Trim();
        await _db.SaveChangesAsync();
        await _audit.LogAsync("ProfileUpdated", "User", user.Id);
        return Result.Success();
    }
}
