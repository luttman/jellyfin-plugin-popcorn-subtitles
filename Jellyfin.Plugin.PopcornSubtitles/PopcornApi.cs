using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed record SubtitleTrack(string Id, string Lang, string Label, string Url,
    string ProviderName = "Popcorn", string Format = "vtt", string ProviderKey = "");
internal sealed record SubtitleResults([property: JsonPropertyName("subtitles")] SubtitleTrack[]? Subtitles);
public sealed record ProviderStatus(string Name, string Status, int Count);
public sealed record ProviderSearchResult(SubtitleTrack[] Tracks, ProviderStatus[] Providers)
{
    public bool HasFailures => Providers.Any(p => p.Status == "failed");
}

public sealed class PopcornApi : IDisposable
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ActivatorUtilitiesConstructor]
    public PopcornApi() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(3)
    }) { }

    public PopcornApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-PopcornSubtitles/" + typeof(Plugin).Assembly.GetName().Version);
    }

    public static Uri GetBaseUri(string manifestUrl, IReadOnlyList<string>? languages = null, bool? enableAiNotes = null)
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
            baseUrl = SetOption(baseUrl, "langs", setting[6..]);
        }
        if (enableAiNotes is not null)
        {
            baseUrl = SetOption(baseUrl, "ai", enableAiNotes.Value ? "on" : "off");
            // ai=off alone still allows non-AI trivia; the explicit checkbox controls both.
            var match = Regex.Match(baseUrl, @"(?<=[/&])skip=([^/&]*)");
            var existing = Uri.UnescapeDataString(match.Groups[1].Value).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var categories = new[] { "card", "recap", "trivia", "goofs", "characters", "ratings" };
            var skip = enableAiNotes.Value ? existing.Except(categories) : existing.Concat(categories).Distinct();
            baseUrl = SetOption(baseUrl, "skip", string.Join(',', skip.Select(Uri.EscapeDataString)));
        }
        return new Uri(baseUrl);
    }

    private static string SetOption(string baseUrl, string key, string value)
    {
        var pattern = @"(?<=[/&])" + key + "=[^/&]*";
        if (Regex.IsMatch(baseUrl, pattern)) return Regex.Replace(baseUrl, pattern, _ => key + "=" + value);
        var trimmed = baseUrl.TrimEnd('/');
        var lastSegment = new Uri(baseUrl).AbsolutePath.TrimEnd('/').Split('/').Last();
        return trimmed + (lastSegment.Contains('=') ? "&" : "/") + key + "=" + value + "/";
    }

    public static bool IsImdbId(string? id) => id is not null && Regex.IsMatch(id, @"^tt\d{5,12}$", RegexOptions.CultureInvariant);

    public static string CacheKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<SubtitleTrack[]> SearchAsync(Uri baseUri, string type, string id, string filename, CancellationToken token,
        IReadOnlyList<string>? languages = null, IReadOnlyList<string>? releaseGroups = null, int limit = 4, bool searchReleaseGroups = false,
        SubtitleProviderConfiguration? provider = null)
    {
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        var preferredLanguages = (languages ?? new[] { "swe", "eng" }).ToArray();
        var groups = releaseGroups ?? [];
        var patterns = groups.Select(group => new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(group) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).ToArray();
        var filenames = new[] { filename }.Concat(searchReleaseGroups ? groups.Take(3).Select(group => ReleaseHint(filename, group)) : []).Distinct();
        // All requests share the caller's budget; a failed hint must not discard a successful normal search.
        var batches = await Task.WhenAll(filenames.Select(async name =>
        {
            try
            {
                var uri = new Uri(baseUri, $"subtitles/{type}/{Uri.EscapeDataString(id)}/filename={Uri.EscapeDataString(name)}.json");
                var bytes = await ReadAsync(uri, token);
                return (Tracks: JsonSerializer.Deserialize<SubtitleResults>(bytes, JsonOptions)?.Subtitles ?? [], Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
            {
                return (Tracks: Array.Empty<SubtitleTrack>(), Error: ex);
            }
        }));
        if (batches.All(b => b.Error is not null)) throw batches[0].Error!;
        return batches.SelectMany(b => b.Tracks)
            .Where(t => t is not null && !string.IsNullOrEmpty(t.Id) && t.Id.Length <= 512 && (t.Label?.Length ?? 0) <= 2048
                && preferredLanguages.Contains(ToTerminologic(t.Lang))
                && (provider is null || provider.PopcornOptions ? IsTrackUri(baseUri, t.Url) : IsDownloadUri(baseUri, t.Url, provider.GetDownloadHosts())))
            .Select(t => t with { Lang = ToTerminologic(t.Lang), ProviderName = provider?.Name ?? "Popcorn",
                ProviderKey = provider is null ? "" : CacheKey(baseUri.AbsoluteUri),
                Format = provider is null || provider.PopcornOptions ? "vtt" : GuessFormat(t) })
            .DistinctBy(t => t.Id)
            .GroupBy(t => t.Lang)
            .OrderBy(g => Array.IndexOf(preferredLanguages, g.Key))
            .SelectMany(g => g.OrderBy(t => GroupPriority(t.Label, patterns)).Take(limit))
            .ToArray();
    }

    public async Task<ProviderSearchResult> SearchProvidersAsync(PluginConfiguration config, string type, string id, string filename, CancellationToken token)
    {
        config.Validate();
        var languages = PluginConfiguration.ParseLanguages(type == "series" ? config.SeriesLanguages : config.MovieLanguages);
        var groups = PluginConfiguration.ParseGroups(type == "series" ? config.SeriesReleaseGroups : config.MovieReleaseGroups);
        var results = await Task.WhenAll(config.GetProviders().Select(async provider =>
        {
            try
            {
                // Generic Stremio URLs may contain opaque private configuration. Never rewrite their paths.
                var baseUri = provider.PopcornOptions ? GetBaseUri(provider.ManifestUrl, languages, config.EnableAiNotes) : GetBaseUri(provider.ManifestUrl);
                var tracks = await SearchAsync(baseUri, type, id, filename, token, languages, groups, config.ResultsPerLanguage, config.SearchReleaseGroups, provider);
                return (Tracks: tracks, Status: new ProviderStatus(provider.Name, "ok", tracks.Length));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
            {
                return (Tracks: Array.Empty<SubtitleTrack>(), Status: new ProviderStatus(provider.Name, "failed", 0));
            }
        }));
        return new ProviderSearchResult(results.SelectMany(r => r.Tracks).ToArray(), results.Select(r => r.Status).ToArray());
    }

    public static bool IsDownloadUri(Uri source, string? value, IReadOnlyList<string> allowedHosts) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Authority.Equals(source.Authority, StringComparison.OrdinalIgnoreCase)
            || (uri.IsDefaultPort && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)));

    private static string GuessFormat(SubtitleTrack track)
    {
        var extension = Path.GetExtension(new Uri(track.Url).AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension is "vtt" or "srt" or "ass" or "ssa") return extension;
        var hint = Regex.Match(track.Label ?? "", @"\[(vtt|srt|ass|ssa)\]", RegexOptions.IgnoreCase);
        return hint.Success ? hint.Groups[1].Value.ToLowerInvariant() : "srt";
    }

    public static string ReleaseHint(string filename, string group)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        stem = Regex.Replace(stem, @"-[A-Za-z0-9]+$", "");
        return stem + "-" + group + Path.GetExtension(filename);
    }

    public static string ToBibliographic(string language) => language switch
    {
        "deu" => "ger", "fra" => "fre", "zho" => "chi", "ces" => "cze", "nld" => "dut", "ell" => "gre",
        "ron" => "rum", "slk" => "slo", "fas" => "per", "isl" => "ice", "msa" => "may", "mya" => "bur",
        "hye" => "arm", "eus" => "baq", "kat" => "geo", "mkd" => "mac", "mri" => "mao", "sqi" => "alb",
        "bod" => "tib", "cym" => "wel", _ => language
    };

    public static string ToTerminologic(string language) => language switch
    {
        "ger" => "deu", "fre" => "fra", "chi" => "zho", "cze" => "ces", "dut" => "nld", "gre" => "ell",
        "rum" => "ron", "slo" => "slk", "per" => "fas", "ice" => "isl", "may" => "msa", "bur" => "mya",
        "arm" => "hye", "baq" => "eus", "geo" => "kat", "mac" => "mkd", "mao" => "mri", "alb" => "sqi",
        "tib" => "bod", "wel" => "cym", _ => language
    };

    private static int GroupPriority(string? label, IReadOnlyList<Regex> patterns)
    {
        for (var index = 0; index < patterns.Count; index++)
            if (patterns[index].IsMatch(label ?? ""))
                return index;
        return patterns.Count;
    }

    public static bool IsTrackUri(Uri baseUri, string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Authority == baseUri.Authority
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith(baseUri.AbsolutePath + "track/", StringComparison.Ordinal);

    public async Task<byte[]> DownloadAsync(string url, CancellationToken token, string format = "vtt")
    {
        var bytes = await ReadAsync(new Uri(url), token);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var valid = format switch
        {
            "vtt" => text.StartsWith("WEBVTT", StringComparison.Ordinal) && text.Contains("-->", StringComparison.Ordinal),
            "srt" => Regex.IsMatch(text, @"(?m)^\s*\d+\s*\r?\n\d{1,2}:\d{2}:\d{2}[,.]\d{3}\s*-->") && !text.StartsWith("WEBVTT", StringComparison.Ordinal),
            "ass" or "ssa" => text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) && text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid) throw new InvalidDataException("The subtitle provider returned an invalid or unsupported subtitle format.");
        return bytes;
    }

    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException ex) { throw new HttpRequestException("Subtitle provider request failed. Private URL omitted.", null, ex.StatusCode); }
        using var responseScope = response;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Subtitle provider returned HTTP " + (int)response.StatusCode + ". Private URL omitted.", null, response.StatusCode);
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
