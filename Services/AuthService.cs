using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UncoveringGreatnessCRM.Data;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;

namespace UncoveringGreatnessCRM.Services;

public class AuthService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly PasswordService _passwords;
    private readonly IAuditService _audit;
    private readonly IUserStateCache _states;
    private readonly ITimeLimitedDataProtector _resetProtector;
    private readonly IEmailSender _email;
    private readonly AppUrls _urls;
    private readonly ILogger<AuthService> _log;

    public AuthService(AppDbContext db, PasswordService passwords, IAuditService audit, IUserStateCache states,
        IDataProtectionProvider dp, IEmailSender email, AppUrls urls, ILogger<AuthService> log)
    {
        _db = db; _passwords = passwords; _audit = audit; _states = states; _email = email; _urls = urls; _log = log;
        _resetProtector = dp.CreateProtector("UncoveringGreatnessCRM.PasswordReset.v1").ToTimeLimitedDataProtector();
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Validates credentials with lockout + timing-safe handling of unknown accounts. Never reveals which part was wrong.</summary>
    public async Task<Result<User>> AuthenticateAsync(string email, string password)
    {
        const string generic = "Invalid email or password.";
        var normalized = NormalizeEmail(email);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized);

        if (user is null)
        {
            _passwords.BurnTime(password);
            await _audit.LogAsync("LoginFailed", "User", null, $"Unknown account '{Trunc(normalized)}'", userName: normalized);
            return Result<User>.From(Result.Fail(ResultStatus.Invalid, generic));
        }

        if (user.LockoutEndUtc is { } end && end > DateTime.UtcNow)
        {
            var mins = (int)Math.Ceiling((end - DateTime.UtcNow).TotalMinutes);
            await _audit.LogAsync("LoginBlocked", "User", user.Id, "Account temporarily locked", user.Id, user.FullName);
            return Result<User>.From(Result.Fail(ResultStatus.Forbidden, $"Too many failed attempts. Try again in {mins} minute{(mins == 1 ? "" : "s")}."));
        }

        var ok = _passwords.Verify(user, password, out var rehash);
        if (!ok || !user.IsActive)
        {
            if (!ok)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedAttempts)
                {
                    user.LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedLoginCount = 0;
                    _log.LogWarning("Account {Email} locked after repeated failed sign-ins", user.Email);
                }
                await _db.SaveChangesAsync();
            }
            await _audit.LogAsync("LoginFailed", "User", user.Id, ok ? "Deactivated account" : "Wrong password", user.Id, user.FullName);
            return Result<User>.From(Result.Fail(ResultStatus.Invalid, generic));
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = DateTime.UtcNow;
        if (rehash) user.PasswordHash = _passwords.Hash(user, password);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("LoginSuccess", "User", user.Id, null, user.Id, user.FullName);
        return Result<User>.Success(user);
    }

    public async Task<Result<User>> GetActiveUserAsync(int id, string? stamp)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null || !user.IsActive || (stamp != null && user.SecurityStamp != stamp))
            return Result<User>.From(Result.Fail(ResultStatus.Invalid, "Session is no longer valid."));
        return Result<User>.Success(user);
    }

    public async Task<Result<User>> ChangePasswordAsync(int userId, string current, string next)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Result<User>.From(Result.NotFound("User"));
        if (!_passwords.Verify(user, current, out _))
        {
            await _audit.LogAsync("PasswordChangeFailed", "User", user.Id, "Wrong current password");
            return Result<User>.From(Result.Invalid(nameof(ChangePasswordInput.CurrentPassword), "Your current password is incorrect."));
        }
        var errors = PasswordService.Validate(next, user.Email, user.FullName);
        if (errors.Count > 0) return Result<User>.From(Result.Invalid(nameof(ChangePasswordInput.NewPassword), string.Join(" ", errors)));
        if (_passwords.Verify(user, next, out _))
            return Result<User>.From(Result.Invalid(nameof(ChangePasswordInput.NewPassword), "Choose a password you have not used just now."));

        SetPassword(user, next, mustChange: false);
        await _db.SaveChangesAsync();
        _states.Invalidate(user.Id);
        await _audit.LogAsync("PasswordChanged", "User", user.Id);
        return Result<User>.Success(user);
    }

    public void SetPassword(User user, string password, bool mustChange)
    {
        user.PasswordHash = _passwords.Hash(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // signs out every other session/token
        user.MustChangePassword = mustChange;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
    }

    /// <summary>Ends every session and API token of a user (they all carry the old security stamp).</summary>
    public async Task<Result> RevokeSessionsAsync(int userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Result.NotFound("User");
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        _states.Invalidate(userId);
        await _audit.LogAsync("SessionsRevoked", "User", userId, "Signed out everywhere", userId, user.FullName);
        return Result.Success();
    }

    // ---- password reset (always responds identically so accounts cannot be enumerated)

    public async Task RequestPasswordResetAsync(string email)
    {
        var normalized = NormalizeEmail(email);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized && u.IsActive);
        if (user is null) { await _audit.LogAsync("PasswordResetRequested", "User", null, "Unknown or inactive account", userName: normalized); return; }

        var token = _resetProtector.Protect($"{user.Id}|{user.SecurityStamp}", ResetLifetime);
        var link = _urls.Absolute("/Account/ResetPassword?token=" + Uri.EscapeDataString(token));
        var text = $"Hi {user.FullName},\n\nUse the link below to choose a new password. It expires in 1 hour and can only be used once.\n\n{link}\n\nIf you did not request this, you can ignore this email.";
        var html = $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.FullName)},</p><p>Use the link below to choose a new password. It expires in 1 hour and can only be used once.</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Reset my password</a></p><p>If you did not request this, you can ignore this email.</p>";
        try { await _email.SendAsync(user.Email, "Reset your Uncovering Greatness CRM password", html, text); }
        catch (Exception ex) { _log.LogError(ex, "Failed to send password reset email"); }
        await _audit.LogAsync("PasswordResetRequested", "User", user.Id, null, user.Id, user.FullName);
    }

    public async Task<Result<User>> ResolveResetTokenAsync(string token)
    {
        try
        {
            var payload = _resetProtector.Unprotect(token);
            var parts = payload.Split('|');
            if (parts.Length == 2 && int.TryParse(parts[0], out var id))
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
                if (user is not null && user.SecurityStamp == parts[1]) return Result<User>.Success(user);
            }
        }
        catch { /* expired or tampered */ }
        return Result<User>.From(Result.Fail(ResultStatus.Invalid, "This reset link is invalid or has expired."));
    }

    public async Task<Result> ResetPasswordAsync(string token, string newPassword)
    {
        var resolved = await ResolveResetTokenAsync(token);
        if (!resolved.Succeeded) return resolved;
        var user = resolved.Value!;
        var errors = PasswordService.Validate(newPassword, user.Email, user.FullName);
        if (errors.Count > 0) return Result.Invalid(nameof(ResetPasswordInput.NewPassword), string.Join(" ", errors));
        SetPassword(user, newPassword, mustChange: false);
        await _db.SaveChangesAsync();
        _states.Invalidate(user.Id);
        await _audit.LogAsync("PasswordReset", "User", user.Id, null, user.Id, user.FullName);
        return Result.Success();
    }

    private static string Trunc(string s) => s.Length > 80 ? s[..80] : s;
}
