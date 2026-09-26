namespace AiReceptionist.Infrastructure;

// Strongly-typed configuration bound from appsettings.json / environment variables / user-secrets.
// Section names are given by the SectionName constants; see docs/SETUP.md for where each value comes from.

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public HTTPS base URL of this app (e.g. https://receptionist.azurewebsites.net or an ngrok/dev-tunnel URL).
    /// Used to build URLs of voice clips that Meta downloads.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5001";
}

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>"AzureOpenAI" (default) or "OpenAI".</summary>
    public string Provider { get; set; } = "AzureOpenAI";

    /// <summary>Azure OpenAI resource endpoint, e.g. https://my-resource.openai.azure.com/</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Azure deployment name, or OpenAI model name (e.g. gpt-4.1-mini, gpt-5-mini).</summary>
    public string Deployment { get; set; } = "gpt-4.1-mini";

    /// <summary>Optional. Leave empty for reasoning models (gpt-5 family), which only accept the default.</summary>
    public float? Temperature { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        (Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(Endpoint));
}

public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    /// <summary>Azure AI Speech key.</summary>
    public string? Key { get; set; }

    /// <summary>Azure region of the Speech resource, e.g. "eastus".</summary>
    public string? Region { get; set; }

    /// <summary>Recognition language for inbound voice notes.</summary>
    public string RecognitionLanguage { get; set; } = "en-US";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Region);
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Folder where synthesized audio is written; served at /media.</summary>
    public string Directory { get; set; } = "data/media";
}

public sealed class GraphCalendarOptions
{
    public const string SectionName = "Microsoft365";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>UPN or object id of the mailbox whose calendar receives appointments, e.g. owner@contoso.com.</summary>
    public string? CalendarUser { get; set; }

    /// <summary>Send Outlook invitations to the customer when their email is known.</summary>
    public bool InviteCustomer { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(CalendarUser);
}

public sealed class SmtpOptions
{
    public const string SectionName = "Email";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>"StartTls" (587), "SslOnConnect" (465) or "None".</summary>
    public string Security { get; set; } = "StartTls";

    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "AI Receptionist";

    /// <summary>Business owner mailbox that receives fallback confirmations.</summary>
    public string? OwnerAddress { get; set; }

    /// <summary>Development option: when set and <see cref="Host"/> is empty, emails are written as .eml files
    /// to this folder instead of being sent (open them with Outlook to see the .ics attachment).</summary>
    public string? PickupDirectory { get; set; }

    public bool IsConfigured =>
        (!string.IsNullOrWhiteSpace(Host) || !string.IsNullOrWhiteSpace(PickupDirectory)) &&
        !string.IsNullOrWhiteSpace(FromAddress) && !string.IsNullOrWhiteSpace(OwnerAddress);
}

public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>Meta App Secret, used to verify X-Hub-Signature-256 on every webhook call.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Any random string; entered in the Meta developer console when subscribing the webhook.</summary>
    public string? VerifyToken { get; set; }

    public string GraphApiVersion { get; set; } = "v21.0";
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";

    public FacebookOptions Facebook { get; set; } = new();
    public InstagramOptions Instagram { get; set; } = new();
    public WhatsAppOptions WhatsApp { get; set; } = new();
}

public sealed class FacebookOptions
{
    /// <summary>Long-lived Page access token with pages_messaging permission.</summary>
    public string? PageAccessToken { get; set; }
}

public sealed class InstagramOptions
{
    /// <summary>Page access token of the Facebook Page linked to the Instagram professional account
    /// (instagram_manage_messages), or an Instagram User token when <see cref="ApiBaseUrl"/> is graph.instagram.com.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Leave empty to use Meta:GraphBaseUrl (Messenger API for Instagram). Set to https://graph.instagram.com
    /// for "Instagram API with Instagram Login".</summary>
    public string? ApiBaseUrl { get; set; }
}

public sealed class WhatsAppOptions
{
    /// <summary>System-user permanent token with whatsapp_business_messaging.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Phone number id (not the phone number) from WhatsApp &gt; API Setup.</summary>
    public string? PhoneNumberId { get; set; }
}

public sealed class TwitterOptions
{
    public const string SectionName = "Twitter";

    public string? ConsumerKey { get; set; }
    public string? ConsumerSecret { get; set; }

    /// <summary>OAuth 1.0a user access token of the business account (app permission: Read, write and Direct Messages).</summary>
    public string? AccessToken { get; set; }

    public string? AccessTokenSecret { get; set; }

    /// <summary>Numeric id of the business account; looked up via /2/users/me when empty.</summary>
    public string? UserId { get; set; }

    public int PollSeconds { get; set; } = 60;
    public string ApiBaseUrl { get; set; } = "https://api.x.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConsumerKey) && !string.IsNullOrWhiteSpace(ConsumerSecret) &&
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(AccessTokenSecret);
}
