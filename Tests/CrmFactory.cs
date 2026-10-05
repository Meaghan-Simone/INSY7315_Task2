using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UncoveringGreatnessCRM.Tests;

/// <summary>
/// Boots the real application (all middleware, EF Core, security) against a throw-away SQLite file seeded with the
/// sample data and the four seeded users from appsettings.json.
/// </summary>
public class CrmFactory : WebApplicationFactory<Program>
{
    public const string SeedPassword = "P@ssword4321";
    public const string AdminEmail = "admin@uncoveringgreatness.co.za";
    public const string RepEmail = "michaela@uncoveringgreatness.co.za";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ug-crm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, Task<Session>> _sessions = new();

    public record Session(string Token, int UserId);

    public CrmFactory()
    {
        Directory.CreateDirectory(_dir);
        // Read by WebApplication.CreateBuilder at start-up, so they must be set before the host is created.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={Path.Combine(_dir, "crm.db")}");
        Environment.SetEnvironmentVariable("DataProtection__KeyPath", Path.Combine(_dir, "keys"));
        Environment.SetEnvironmentVariable("Seed__SampleData", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    /// <summary>Signs in through the API once per user and caches the token (the sign-in endpoint is rate limited).</summary>
    public Task<Session> SessionAsync(string email) => _sessions.GetOrAdd(email, async e =>
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { email = e, password = SeedPassword });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(json.GetProperty("accessToken").GetString()!, json.GetProperty("user").GetProperty("id").GetInt32());
    });

    public async Task<HttpClient> ClientForAsync(string email)
    {
        var session = await SessionAsync(email);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch { /* SQLite may still hold the file briefly; the temp folder is disposable */ }
    }
}

// One shared host for every integration test (the start-up settings are process-wide environment variables).
[CollectionDefinition("crm")]
public class CrmCollection : ICollectionFixture<CrmFactory> { }
