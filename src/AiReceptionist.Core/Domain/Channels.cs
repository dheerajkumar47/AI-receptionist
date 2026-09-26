namespace AiReceptionist.Core.Domain;

/// <summary>
/// Well-known channel ids. Channels are identified by plain strings (not an enum) so a new
/// connector can be added in its own assembly without touching the core.
/// </summary>
public static class Channels
{
    public const string Facebook = "facebook";
    public const string Instagram = "instagram";
    public const string WhatsApp = "whatsapp";
    public const string Twitter = "twitter";
    public const string Simulator = "simulator";
}
