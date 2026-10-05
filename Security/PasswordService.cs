using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Security;

/// <summary>Password hashing (ASP.NET Core Identity PBKDF2 hasher, versioned + upgradeable) and the password policy.</summary>
public class PasswordService
{
    public const int MinLength = 10;
    private readonly PasswordHasher<User> _hasher = new();
    private readonly string _dummyHash;

    public PasswordService()
    {
        // Verified against when an account does not exist so response timing does not reveal which emails are registered.
        _dummyHash = _hasher.HashPassword(new User(), Guid.NewGuid().ToString("N") + "Aa1!");
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string password, out bool needsRehash)
    {
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }

    public void BurnTime(string password) => _hasher.VerifyHashedPassword(new User(), _dummyHash, password);

    public static List<string> Validate(string? password, string? email = null, string? fullName = null)
    {
        var errors = new List<string>();
        password ??= "";
        if (password.Length < MinLength) errors.Add($"Password must be at least {MinLength} characters long.");
        if (password.Length > 128) errors.Add("Password must be at most 128 characters long.");
        if (!password.Any(char.IsUpper)) errors.Add("Password must contain an uppercase letter.");
        if (!password.Any(char.IsLower)) errors.Add("Password must contain a lowercase letter.");
        if (!password.Any(char.IsDigit)) errors.Add("Password must contain a number.");
        if (!password.Any(c => !char.IsLetterOrDigit(c))) errors.Add("Password must contain a symbol (e.g. ! ? # $).");
        var local = email?.Split('@')[0];
        if (!string.IsNullOrWhiteSpace(local) && local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            errors.Add("Password must not contain your email name.");
        foreach (var part in (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 4))
            if (password.Contains(part, StringComparison.OrdinalIgnoreCase)) { errors.Add("Password must not contain your name."); break; }
        if (CommonPasswords.Contains(password)) errors.Add("That password is too common.");
        return errors;
    }

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password123!", "password1234", "welcome1234", "qwerty12345", "admin12345!", "letmein1234", "changeme123"
    };

    /// <summary>Cryptographically random password that always satisfies the policy.</summary>
    public static string GenerateTemporary(int length = 16)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@#$%*?-_";
        var all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)], lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)], symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }
}
