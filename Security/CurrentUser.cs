using System.Security.Claims;
using UncoveringGreatnessCRM.Domain;

namespace UncoveringGreatnessCRM.Security;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    int Id { get; }
    string Name { get; }
    string Email { get; }
    UserRole Role { get; }
    bool IsAdmin { get; }
    /// <summary>Admins and sales reps may create/modify CRM records; staff are read-only.</summary>
    bool CanWrite { get; }
    string? IpAddress { get; }
}

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public int Id => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    public string Name => Principal?.FindFirstValue(ClaimTypes.Name) ?? "";
    public string Email => Principal?.FindFirstValue(ClaimTypes.Email) ?? "";
    public UserRole Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.Staff;
    public bool IsAdmin => IsAuthenticated && Role == UserRole.Admin;
    public bool CanWrite => IsAuthenticated && Role is UserRole.Admin or UserRole.SalesRep;
    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

public static class ClaimsFactory
{
    public const string StampClaim = "ug:stamp";

    public static ClaimsPrincipal Create(User user, string scheme)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(StampClaim, user.SecurityStamp),
            new Claim("ug:mustchange", user.MustChangePassword ? "1" : "0")
        }, scheme);
        return new ClaimsPrincipal(identity);
    }
}
