using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiReceptionist.Infrastructure.Channels.Twitter;

/// <summary>
/// OAuth 1.0a HMAC-SHA1 request signing (RFC 5849) as required by the X (Twitter) API for user-context calls.
/// JSON request bodies are not part of the signature; query-string parameters are.
/// </summary>
public static class OAuth1Signer
{
    public static string BuildAuthorizationHeader(
        string method, Uri url, string consumerKey, string consumerSecret, string token, string tokenSecret,
        string? nonce = null, long? timestamp = null, IEnumerable<KeyValuePair<string, string>>? extraParameters = null)
    {
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = timestamp.Value.ToString(CultureInfo.InvariantCulture),
            ["oauth_token"] = token,
            ["oauth_version"] = "1.0",
        };

        var signature = ComputeSignature(method, url, oauth, consumerSecret, tokenSecret, extraParameters);
        oauth["oauth_signature"] = signature;

        return "OAuth " + string.Join(", ", oauth.Select(kv => $"{Encode(kv.Key)}=\"{Encode(kv.Value)}\""));
    }

    public static string ComputeSignature(
        string method, Uri url, IEnumerable<KeyValuePair<string, string>> oauthParameters, string consumerSecret, string tokenSecret,
        IEnumerable<KeyValuePair<string, string>>? extraParameters = null)
    {
        var all = new List<KeyValuePair<string, string>>(oauthParameters);
        all.AddRange(ParseQuery(url.Query));
        if (extraParameters is not null) all.AddRange(extraParameters);

        var normalized = string.Join("&", all
            .Select(kv => (Key: Encode(kv.Key), Value: Encode(kv.Value)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ThenBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        var baseUrl = $"{url.Scheme.ToLowerInvariant()}://{url.Host.ToLowerInvariant()}{(url.IsDefaultPort ? "" : ":" + url.Port)}{url.AbsolutePath}";
        var baseString = $"{method.ToUpperInvariant()}&{Encode(baseUrl)}&{Encode(normalized)}";
        var key = $"{Encode(consumerSecret)}&{Encode(tokenSecret)}";

        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(baseString)));
    }

    /// <summary>RFC 3986 percent-encoding (unreserved characters are left as-is).</summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var k = Uri.UnescapeDataString(i < 0 ? part : part[..i]);
            var v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            yield return new(k, v);
        }
    }
}
