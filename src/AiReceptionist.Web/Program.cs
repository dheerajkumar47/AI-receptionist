using AiReceptionist.Core.Data;
using AiReceptionist.Infrastructure;
using AiReceptionist.Infrastructure.Voice;
using AiReceptionist.Web.Components;
using AiReceptionist.Web.Endpoints;
using AiReceptionist.Web.Workers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Optional untracked override file for local secrets (in addition to user-secrets and environment variables).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddAiReceptionist(builder.Configuration);
builder.Services.AddHostedService<InboundProcessingWorker>();
builder.Services.AddHostedService<ChannelPollingWorker>();
builder.Services.AddHostedService<CalendarSignInWorker>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<AdminCredentials>();

var app = builder.Build();

// Create the database (SQLite file by default) and seed default intents, rules and settings.
Directory.CreateDirectory("data"); // relative paths in appsettings resolve against the working directory
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ReceptionistDbContext>>().CreateDbContextAsync())
{
    await SeedData.InitializeAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();

// Synthesized voice clips must be publicly downloadable by Meta; file names are random GUIDs.
var media = app.Services.GetRequiredService<FileMediaStore>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(media.PhysicalDirectory),
    RequestPath = "/media",
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWebhookEndpoints();
app.MapAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
