using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

/// <summary>
/// Public webhook that Tally, Google Forms (via Apps Script), Zapier/Make or anything else POSTs to.
/// Protected by an unguessable per-connection token in the URL, optional HMAC signature, a body-size cap, rate limiting and idempotency.
/// </summary>
[AllowAnonymous, IgnoreAntiforgeryToken, EnableRateLimiting("webhook")]
public class FormWebhookController : Controller
{
    private readonly FormIngestService _ingest;
    public FormWebhookController(FormIngestService ingest) => _ingest = ingest;

    [HttpGet("/integrations/forms/{token}")]
    public async Task<IActionResult> Ping(string token) =>
        await _ingest.ExistsAsync(token) ? Ok(new { status = "ok", message = "Webhook is active. Send form submissions with POST." }) : NotFound(new { status = "not_found" });

    [HttpPost("/integrations/forms/{token}")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> Receive(string token)
    {
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }
        if (body.Length == 0) return BadRequest(new { status = "invalid", message = "Empty request body." });

        var signature = Request.Headers["Tally-Signature"].FirstOrDefault() ?? Request.Headers["X-Signature"].FirstOrDefault();
        var outcome = await _ingest.ProcessAsync(token, body, signature, HttpContext.Connection.RemoteIpAddress?.ToString());

        var payload = new { status = outcome.Status.ToString().ToLowerInvariant(), message = outcome.Message, leadId = outcome.LeadId };
        return outcome.Status switch
        {
            // 2xx so the form tool does not keep retrying things a retry cannot fix.
            IngestStatus.Created or IngestStatus.Duplicate or IngestStatus.Rejected => Ok(payload),
            IngestStatus.Unauthorized => Unauthorized(payload),
            IngestStatus.NotFound => NotFound(payload),
            IngestStatus.Inactive => StatusCode(410, payload),
            IngestStatus.Invalid => BadRequest(payload),
            _ => StatusCode(500, payload)
        };
    }
}
