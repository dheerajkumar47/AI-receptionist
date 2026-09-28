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

// Azure App Service: only %HOME% survives restarts and redeployments, so default relative data paths to %HOME%/data.
if (Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null &&
    Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
{
    var dataRoot = Path.Combine(home, "data");
    string? Rebase(string? path) => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
        ? null
        : Path.Combine(dataRoot, path.StartsWith("data/") || path.StartsWith("data\\") ? path[5..] : path);

    var overrides = new Dictionary<string, string?>();
    var connection = builder.Configuration.GetConnectionString("Receptionist") ?? "Data Source=data/receptionist.db";
    const string prefix = "Data Source=";
    if (connection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Rebase(connection[prefix.Length..]) is { } db)
        overrides["ConnectionStrings:Receptionist"] = prefix + db;
    if (Rebase(builder.Configuration["Media:Directory"]) is { } mediaDir) overrides["Media:Directory"] = mediaDir;
    if (Rebase(builder.Configuration["Microsoft365:TokenCachePath"] ?? "data/outlook-token-cache.bin") is { } cache)
        overrides["Microsoft365:TokenCachePath"] = cache;
    builder.Configuration.AddInMemoryCollection(overrides);
}

builder.Services.AddAiReceptionist(builder.Configuration);
builder.Services.AddHostedService<InboundProcessingWorker>();
builder.Services.AddHostedService<ChannelPollingWorker>();
builder.Services.AddHostedService<CalendarSignInWorker>();
builder.Services.AddHostedService<ScheduledMessagesWorker>();

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
// Relative paths in appsettings resolve against the working directory; make sure the database folder exists.
var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Receptionist") ?? "Data Source=data/receptionist.db");
if (Path.GetDirectoryName(Path.GetFullPath(sqlite.DataSource)) is { } dbDir) Directory.CreateDirectory(dbDir);
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
app.MapPrivacyEndpoint();
app.MapWebhookEndpoints();
app.MapAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
