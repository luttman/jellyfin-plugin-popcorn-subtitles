using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed record SubtitleTrack(string Id, string Lang, string Label, string Url);
internal sealed record SubtitleResults([property: JsonPropertyName("subtitles")] SubtitleTrack[]? Subtitles);

public sealed class PopcornApi : IDisposable
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PopcornApi() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    public PopcornApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-PopcornSubtitles/0.2.0");
    }

    public static Uri GetBaseUri(string manifestUrl, IReadOnlyList<string>? languages = null)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTPS URL ending in /manifest.json.");
        var baseUrl = manifestUrl[..^"manifest.json".Length];
        if (languages is not null)
        {
            var setting = "langs=" + string.Join(',', PluginConfiguration.ParseLanguages(string.Join(',', languages)));
            baseUrl = Regex.IsMatch(baseUrl, @"(?<=[/&])langs=[^/&]*")
                ? Regex.Replace(baseUrl, @"(?<=[/&])langs=[^/&]*", setting)
                : baseUrl + setting + "/";
        }
        return new Uri(baseUrl);
    }

    public static bool IsImdbId(string? id) => id is not null && Regex.IsMatch(id, @"^tt\d{5,12}$", RegexOptions.CultureInvariant);

    public static string CacheKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<SubtitleTrack[]> SearchAsync(Uri baseUri, string type, string id, string filename, CancellationToken token,
        IReadOnlyList<string>? languages = null, IReadOnlyList<string>? releaseGroups = null)
    {
        var uri = new Uri(baseUri, $"subtitles/{type}/{Uri.EscapeDataString(id)}/filename={Uri.EscapeDataString(filename)}.json");
        var bytes = await ReadAsync(uri, token);
        var result = JsonSerializer.Deserialize<SubtitleResults>(bytes, JsonOptions);
        var preferredLanguages = (languages ?? new[] { "swe", "eng" }).ToArray();
        var groups = releaseGroups ?? [];
        return (result?.Subtitles ?? [])
            .Where(t => t is not null && !string.IsNullOrEmpty(t.Id) && preferredLanguages.Contains(t.Lang) && IsTrackUri(baseUri, t.Url))
            .DistinctBy(t => t.Id)
            .GroupBy(t => t.Lang)
            .OrderBy(g => Array.IndexOf(preferredLanguages, g.Key))
            .SelectMany(g => g.OrderBy(t => GroupPriority(t.Label, groups)).Take(4))
            .ToArray();
    }

    private static int GroupPriority(string? label, IReadOnlyList<string> groups)
    {
        for (var index = 0; index < groups.Count; index++)
            if (Regex.IsMatch(label ?? "", @"(?<![\p{L}\p{N}])" + Regex.Escape(groups[index]) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return index;
        return groups.Count;
    }

    public static bool IsTrackUri(Uri baseUri, string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Authority == baseUri.Authority
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith(baseUri.AbsolutePath + "track/", StringComparison.Ordinal);

    public async Task<byte[]> DownloadAsync(string url, CancellationToken token)
    {
        var bytes = await ReadAsync(new Uri(url), token);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (!text.StartsWith("WEBVTT", StringComparison.Ordinal) || !text.Contains("-->", StringComparison.Ordinal))
            throw new InvalidDataException("The subtitle service did not return a WebVTT subtitle.");
        return bytes;
    }

    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("Subtitle response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaxResponseBytes)
                throw new InvalidDataException("Subtitle response is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
