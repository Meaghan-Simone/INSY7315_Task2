namespace UncoveringGreatnessCRM.Security;

public class SecurityHeadersMiddleware
{
    private const string Csp =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
        "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!ctx.Request.Path.StartsWithSegments("/api")) h["Content-Security-Policy"] = Csp;
            // Authenticated pages must not be served from the browser cache after sign-out (back button).
            if (!Path.HasExtension(ctx.Request.Path.Value))
            {
                h["Cache-Control"] = "no-store, no-cache";
                h["Pragma"] = "no-cache";
            }
            return Task.CompletedTask;
        });
        return _next(ctx);
    }
}
