using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using UncoveringGreatnessCRM.Data;

namespace UncoveringGreatnessCRM.Security;

public record UserState(bool IsActive, string Stamp);

/// <summary>Short-lived cache of (active, security stamp) so every request can cheaply re-validate the session.</summary>
public interface IUserStateCache
{
    Task<UserState?> GetAsync(int userId);
    void Invalidate(int userId);
}

public class UserStateCache : IUserStateCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;

    public UserStateCache(IMemoryCache cache, IServiceScopeFactory scopes) { _cache = cache; _scopes = scopes; }

    public async Task<UserState?> GetAsync(int userId)
    {
        if (_cache.TryGetValue(Key(userId), out UserState? cached)) return cached;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new UserState(u.IsActive, u.SecurityStamp)).FirstOrDefaultAsync();
        _cache.Set(Key(userId), state, Ttl);
        return state;
    }

    public void Invalidate(int userId) => _cache.Remove(Key(userId));
    private static string Key(int id) => "userstate:" + id;
}

/// <summary>Every policy includes this: the caller must still exist, be active and hold the current security stamp.</summary>
public class ActiveUserRequirement : IAuthorizationRequirement { }

public class ActiveUserHandler : AuthorizationHandler<ActiveUserRequirement>
{
    private readonly IUserStateCache _states;
    public ActiveUserHandler(IUserStateCache states) => _states = states;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) { context.Fail(); return; }
        var state = await _states.GetAsync(id);
        var stamp = context.User.FindFirstValue(ClaimsFactory.StampClaim);
        if (state is { IsActive: true } && state.Stamp == stamp) context.Succeed(requirement);
        else context.Fail();
    }
}
