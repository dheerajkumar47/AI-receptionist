using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Infrastructure.Voice;

/// <summary>
/// Text-to-speech with Azure AI Speech (Microsoft Cognitive Services) neural voices via the REST API.
/// The REST API is used rather than the native Speech SDK so the app runs unchanged on Windows, Linux and App Service.
/// </summary>
public sealed class AzureSpeechSynthesizer : IVoiceSynthesizer
{
    public const string HttpClientName = "azure-speech";
    private readonly IHttpClientFactory _http;
    private readonly SpeechOptions _options;

    public AzureSpeechSynthesizer(IHttpClientFactory http, IOptions<SpeechOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<AudioClip> SynthesizeAsync(string text, string voiceName, AudioFormat format, CancellationToken ct)
    {
        var (outputFormat, contentType, extension) = format switch
        {
            AudioFormat.OggOpus => ("ogg-24khz-16bit-mono-opus", "audio/ogg", "ogg"),
            _ => ("audio-24khz-48kbitrate-mono-mp3", "audio/mpeg", "mp3"),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_options.Region}.tts.speech.microsoft.com/cognitiveservices/v1");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.Key);
        request.Headers.Add("X-Microsoft-OutputFormat", outputFormat);
        request.Headers.UserAgent.ParseAdd("AiReceptionist/1.0");
        request.Content = new StringContent(BuildSsml(text, voiceName), Encoding.UTF8, "application/ssml+xml");

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Azure Speech TTS returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        return new AudioClip(await response.Content.ReadAsByteArrayAsync(ct), contentType, extension);
    }

    public static string BuildSsml(string text, string voiceName)
    {
        // "en-US-AvaMultilingualNeural" -> "en-US"
        var parts = voiceName.Split('-');
        var lang = parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "en-US";
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{lang}'>" +
               $"<voice name='{SecurityElement.Escape(voiceName)}'>{SecurityElement.Escape(text)}</voice></speak>";
    }
}

/// <summary>Speech-to-text for inbound voice notes using the Azure Speech short-audio REST API (≤ 60 s).</summary>
public sealed class AzureSpeechTranscriber : IAudioTranscriber
{
    private readonly IHttpClientFactory _http;
    private readonly SpeechOptions _options;
    private readonly ILogger<AzureSpeechTranscriber> _log;

    public AzureSpeechTranscriber(IHttpClientFactory http, IOptions<SpeechOptions> options, ILogger<AzureSpeechTranscriber> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<string?> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct)
    {
        // The short-audio endpoint accepts WAV (PCM) and OGG/Opus. WhatsApp voice notes are OGG/Opus.
        var mediaType = contentType.StartsWith("audio/ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg; codecs=opus" : contentType;

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{_options.Region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1" +
            $"?language={Uri.EscapeDataString(_options.RecognitionLanguage)}&format=simple");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.Key);
        request.Content = new ByteArrayContent(audio);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);

        using var response = await _http.CreateClient(AzureSpeechSynthesizer.HttpClientName).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Speech-to-text returned {Status}: {Body}", (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.TryGetProperty("RecognitionStatus", out var s) ? s.GetString() : null;
        return status == "Success" && doc.RootElement.TryGetProperty("DisplayText", out var t) ? t.GetString() : null;
    }
}

/// <summary>Writes audio clips to disk; the web host serves them at {PublicBaseUrl}/media/{file}.</summary>
public sealed class FileMediaStore : IMediaStore
{
    private readonly string _directory;
    private readonly string _baseUrl;

    public FileMediaStore(IOptions<MediaOptions> media, IOptions<AppOptions> app)
    {
        _directory = Path.GetFullPath(media.Value.Directory);
        _baseUrl = app.Value.PublicBaseUrl.TrimEnd('/');
        Directory.CreateDirectory(_directory);
    }

    public string PhysicalDirectory => _directory;

    public async Task<string> SaveAsync(AudioClip clip, CancellationToken ct)
    {
        var fileName = $"{Guid.NewGuid():N}.{clip.Extension}";
        await File.WriteAllBytesAsync(Path.Combine(_directory, fileName), clip.Data, ct);
        return $"{_baseUrl}/media/{fileName}";
    }

    /// <summary>Maps a URL produced by <see cref="SaveAsync"/> back to the file on disk (so channels can upload it directly).</summary>
    public bool TryGetLocalPath(string url, out string path)
    {
        path = "";
        var i = url.IndexOf("/media/", StringComparison.Ordinal);
        if (i < 0) return false;
        var fileName = Path.GetFileName(url[(i + "/media/".Length)..]); // no directory traversal
        if (string.IsNullOrEmpty(fileName)) return false;
        path = Path.Combine(_directory, fileName);
        return File.Exists(path);
    }
}
