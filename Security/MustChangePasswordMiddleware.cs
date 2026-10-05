namespace UncoveringGreatnessCRM.Security;

/// <summary>Accounts created or reset by an admin must choose their own password before using anything else.</summary>
public class MustChangePasswordMiddleware
{
    private readonly RequestDelegate _next;
    public MustChangePasswordMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirst("ug:mustchange")?.Value == "1")
        {
            var p = ctx.Request.Path;
            var allowed = p.StartsWithSegments("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase) ||
                          p.StartsWithSegments("/Account/Logout", StringComparison.OrdinalIgnoreCase) ||
                          p.StartsWithSegments("/css") || p.StartsWithSegments("/js") || p.StartsWithSegments("/Images");
            if (!allowed)
            {
                if (p.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 403;
                    return ctx.Response.WriteAsJsonAsync(new { error = "You must change your password before continuing." });
                }
                ctx.Response.Redirect("/Account/ChangePassword");
                return Task.CompletedTask;
            }
        }
        return _next(ctx);
    }
}
