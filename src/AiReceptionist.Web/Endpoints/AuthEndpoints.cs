using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AiReceptionist.Web.Endpoints;

/// <summary>Dashboard credentials from configuration (Admin:Username / Admin:Password).
/// If no password is configured a random one is generated at start-up and written to the log.</summary>
public sealed class AdminCredentials
{
    public AdminCredentials(IConfiguration config, ILogger<AdminCredentials> log)
    {
        Username = config["Admin:Username"] is { Length: > 0 } u ? u : "admin";
        var configured = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
            log.LogWarning("Admin:Password is not configured. Temporary dashboard password for '{User}': {Password}", Username, configured);
        }
        Password = configured;
    }

    public string Username { get; }
    private string Password { get; }

    public bool Check(string? username, string? password) =>
        username is not null && password is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(Username)) &
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", (HttpContext ctx, IAntiforgery antiforgery, string? returnUrl, bool? failed) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            return Results.Content(LoginPage(tokens, returnUrl, failed == true), "text/html");
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext ctx, IAntiforgery antiforgery, AdminCredentials credentials) =>
        {
            await antiforgery.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var returnUrl = form["returnUrl"].ToString();
            if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) returnUrl = "/";

            if (!credentials.Check(form["username"], form["password"]))
                return Results.Redirect($"/login?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}");

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, credentials.Username) }, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(returnUrl);
        }).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    private static string LoginPage(AntiforgeryTokenSet tokens, string? returnUrl, bool failed) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sign in · AI Receptionist</title><link rel="stylesheet" href="/app.css"></head>
        <body class="login-body">
          <form class="card login-card" method="post" action="/login">
            <h1>AI Receptionist</h1>
            <p class="muted">Sign in to the admin dashboard</p>
            {{(failed ? "<p class=\"error\">Invalid username or password.</p>" : "")}}
            <input type="hidden" name="{{tokens.FormFieldName}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}">
            <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl ?? "/")}}">
            <label>Username<input name="username" autocomplete="username" required autofocus></label>
            <label>Password<input name="password" type="password" autocomplete="current-password" required></label>
            <button class="btn primary" type="submit">Sign in</button>
          </form>
        </body></html>
        """;
}
