using System.Net;
using AiReceptionist.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Web.Endpoints;

/// <summary>
/// Public privacy policy at /privacy. Meta requires a privacy policy URL before an app can be published,
/// and it tells people messaging the business what the receptionist stores.
/// </summary>
public static class PrivacyEndpoint
{
    public static void MapPrivacyEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/privacy", async (IDbContextFactory<ReceptionistDbContext> dbFactory, IConfiguration config) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync();
            var business = WebUtility.HtmlEncode(settings?.BusinessName ?? "This business");
            var contact = WebUtility.HtmlEncode(config["Email:OwnerAddress"] ?? "");

            return Results.Content($$"""
                <!DOCTYPE html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Privacy Policy · {{business}}</title><link rel="stylesheet" href="/app.css"></head>
                <body><main class="content" style="max-width:760px;margin:0 auto">
                <h1>Privacy Policy</h1>
                <p class="muted">{{business}} — AI receptionist for direct messages</p>
                <section class="card">
                  <h2>What we collect</h2>
                  <p>When you message us on WhatsApp, Facebook Messenger, Instagram or X, we store your messages, your
                  platform user ID or phone number, and any name or email address you choose to share, so we can reply
                  and arrange appointments.</p>
                  <h2>How we use it</h2>
                  <p>Messages are processed by an AI assistant (Microsoft Azure OpenAI) to understand your request and
                  write a reply, which may also be converted to speech (Microsoft Azure AI Speech). If you book an
                  appointment, its details are added to our calendar and may be emailed to you and to us.</p>
                  <h2>Sharing</h2>
                  <p>We do not sell your data or use it for advertising. It is only processed by the service providers
                  named above and the messaging platform you contacted us on.</p>
                  <h2>Retention and deletion</h2>
                  <p>Conversation history is kept only as long as needed to provide the service. To have your data
                  deleted, send us a message saying "delete my data"{{(contact.Length > 0 ? $" or email <a href=\"mailto:{contact}\">{contact}</a>" : "")}}
                  and we will remove it within 30 days.</p>
                </section>
                </main></body></html>
                """, "text/html");
        }).AllowAnonymous();
}
