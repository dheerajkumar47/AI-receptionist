using System.Net;
using System.Text;
using System.Text.Json;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using AiReceptionist.Infrastructure.Channels.Meta;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiReceptionist.Tests;

/// <summary>Runs the real ASP.NET Core host: webhook endpoint → queue → worker → conversation service → Messenger Send API (stubbed HTTP).</summary>
public sealed class WebhookIntegrationTests : IClassFixture<WebhookIntegrationTests.Factory>
{
    private const string Secret = "test-app-secret";
    private readonly Factory _factory;

    public WebhookIntegrationTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Meta_verification_handshake()
    {
        var client = _factory.CreateClient();
        var ok = await client.GetAsync("/webhooks/facebook?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=42");
        Assert.Equal("42", await ok.Content.ReadAsStringAsync());

        var bad = await client.GetAsync("/webhooks/facebook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);

        var unconfigured = await client.GetAsync("/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=42");
        Assert.Equal(HttpStatusCode.NotFound, unconfigured.StatusCode);
    }

    [Fact]
    public async Task Unsigned_webhook_is_rejected()
    {
        var response = await _factory.CreateClient().PostAsync("/webhooks/facebook", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_messenger_message_is_answered_through_the_send_api()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"object":"page","entry":[{"id":"PAGE","time":1,"messaging":[
              {"sender":{"id":"PSID-INT"},"recipient":{"id":"PAGE"},"timestamp":1790000000000,"message":{"mid":"m_int_1","text":"What are your opening hours?"}}]}]}
            """);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        content.Headers.Add("X-Hub-Signature-256", MetaWebhook.Sign(body, Secret));

        var response = await _factory.CreateClient().PostAsync("/webhooks/facebook", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Processing is asynchronous (the webhook returns immediately); wait for the reply.
        var request = await _factory.Graph.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("https://graph.facebook.com/v21.0/me/messages", request.Url);
        Assert.Equal("Bearer page-token", request.Authorization);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("PSID-INT", json.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Contains("Mon-Fri 09:00-17:00", json.RootElement.GetProperty("message").GetProperty("text").GetString());

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<ReceptionistDbContext>>().CreateDbContextAsync();
        var outbound = await WaitAsync(() => db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Channel == Channels.Facebook && m.Direction == MessageDirection.Outbound));
        Assert.Equal(MessageStatus.Sent, outbound.Status);
        Assert.Equal("mid.reply", outbound.ExternalId);
    }

    [Fact]
    public async Task Dashboard_requires_sign_in()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "air-tests-" + Guid.NewGuid().ToString("N"));
        public StubHandler Graph { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_dir);
            builder.UseSetting("ConnectionStrings:Receptionist", $"Data Source={Path.Combine(_dir, "test.db")}");
            builder.UseSetting("Media:Directory", Path.Combine(_dir, "media"));
            builder.UseSetting("Admin:Password", "pw");
            builder.UseSetting("Meta:AppSecret", Secret);
            builder.UseSetting("Meta:VerifyToken", "verify-me");
            builder.UseSetting("Meta:Facebook:PageAccessToken", "page-token");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(MetaChannelBase.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Graph));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    public sealed record CapturedRequest(string Url, string? Authorization, string Body);

    /// <summary>Stands in for graph.facebook.com.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<CapturedRequest> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CapturedRequest> WaitForRequestAsync(TimeSpan timeout) => await _first.Task.WaitAsync(timeout);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _first.TrySetResult(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recipient_id":"PSID-INT","message_id":"mid.reply"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
