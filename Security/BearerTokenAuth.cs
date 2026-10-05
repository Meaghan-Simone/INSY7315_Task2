using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Security;

public static class AuthSchemes
{
    public const string Cookie = "Cookies";
    public const string Bearer = "Bearer";
    public const string Selector = "Selector";
}

/// <summary>
/// Issues and validates opaque, tamper-proof, expiring API tokens using ASP.NET Core Data Protection.
/// A token embeds the user id and the user's security stamp, so changing a password / role / deactivating the
/// user immediately invalidates every token issued earlier.
/// </summary>
public class ApiTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly ITimeLimitedDataProtector _protector;

    public ApiTokenService(IDataProtectionProvider dp) =>
        _protector = dp.CreateProtector("UncoveringGreatnessCRM.ApiToken.v1").ToTimeLimitedDataProtector();

    public (string Token, DateTimeOffset Expires) Issue(User user)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        return (_protector.Protect($"{user.Id}|{user.SecurityStamp}", expires), expires);
    }

    public (int UserId, string Stamp)? Validate(string token)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], out var id) ? (id, parts[1]) : null;
        }
        catch { return null; }
    }
}

public class BearerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiTokenService _tokens;
    private readonly IServiceScopeFactory _scopes;

    public BearerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        ApiTokenService tokens, IServiceScopeFactory scopes) : base(options, logger, encoder)
    { _tokens = tokens; _scopes = scopes; }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var parsed = _tokens.Validate(header["Bearer ".Length..].Trim());
        if (parsed is null) return AuthenticateResult.Fail("Invalid or expired token.");

        using var scope = _scopes.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<Services.AuthService>();
        var user = await auth.GetActiveUserAsync(parsed.Value.UserId, parsed.Value.Stamp);
        if (!user.Succeeded) return AuthenticateResult.Fail("Token is no longer valid.");
        var principal = ClaimsFactory.Create(user.Value!, AuthSchemes.Bearer);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AuthSchemes.Bearer));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(new { error = "Authentication required." });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        return Response.WriteAsJsonAsync(new { error = "You do not have permission to do that." });
    }
}
