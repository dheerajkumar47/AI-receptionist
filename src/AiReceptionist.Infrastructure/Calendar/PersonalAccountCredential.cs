using System.Security.Cryptography;
using Azure.Core;
using Microsoft.Identity.Client;

namespace AiReceptionist.Infrastructure.Calendar;

/// <summary>Thrown when the Outlook account has not signed in yet, or its refresh token is no longer valid.</summary>
public sealed class CalendarSignInRequiredException : Exception
{
    public CalendarSignInRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Device-code sign-in for personal Microsoft accounts (Outlook.com), built directly on MSAL.
/// The MSAL token cache (which holds the long-lived refresh token) is saved to a file after every change,
/// encrypted with Windows DPAPI when available, so the owner signs in once and restarts stay signed in.
/// </summary>
public sealed class PersonalAccountCredential : TokenCredential
{
    public static readonly string[] Scopes = { "https://graph.microsoft.com/Calendars.ReadWrite" };

    private readonly IPublicClientApplication _app;
    private readonly string _cacheFile;
    private readonly object _fileLock = new();

    public PersonalAccountCredential(string clientId, string tenant, string cacheFile)
    {
        _cacheFile = Path.GetFullPath(cacheFile);
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .WithDefaultRedirectUri()
            .Build();

        _app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (_fileLock)
            {
                if (File.Exists(_cacheFile))
                    args.TokenCache.DeserializeMsalV3(Unprotect(File.ReadAllBytes(_cacheFile)), shouldClearExistingCache: true);
            }
        });
        _app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                File.WriteAllBytes(_cacheFile, Protect(args.TokenCache.SerializeMsalV3()));
            }
        });
    }

    /// <summary>True if a previously signed-in account is in the saved cache.</summary>
    public async Task<bool> HasAccountAsync() => (await _app.GetAccountsAsync()).Any();

    /// <summary>Runs the device-code flow; <paramref name="onPrompt"/> receives "open https://microsoft.com/link and enter code ...".
    /// Returns the signed-in username.</summary>
    public async Task<string> SignInAsync(Func<string, Task> onPrompt, CancellationToken ct)
    {
        var result = await _app.AcquireTokenWithDeviceCode(Scopes, code => onPrompt(code.Message)).ExecuteAsync(ct);
        return result.Account.Username;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var account = (await _app.GetAccountsAsync()).FirstOrDefault()
                      ?? throw new CalendarSignInRequiredException("Outlook calendar is not signed in.");
        try
        {
            var result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new CalendarSignInRequiredException("Outlook calendar sign-in expired: " + ex.Message, ex);
        }
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser) : data;

    private static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return data;
        try { return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return Array.Empty<byte>(); } // written by another user/machine: sign in again
    }
}
