using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed record AvailableSubtitle(int Index, SubtitleTrack Track, string Path);

public sealed class SubtitleCatalog : IDisposable
{
    private readonly PopcornApi _api;
    private readonly ILogger<SubtitleCatalog> _logger;
    private readonly string _directory;
    private readonly MemoryCache _searches = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly MemoryCache _results = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly MemoryCache _failures = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadLock> _downloadLocks = new(StringComparer.Ordinal);
    private readonly object _downloadGate = new();
    private readonly MemoryCache _paths = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private long _lastCleanup;
    private sealed class DownloadLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1);
        public int Users { get; set; }
    }

    public SubtitleCatalog(PopcornApi api, IApplicationPaths paths, ILogger<SubtitleCatalog> logger)
    {
        _api = api;
        _logger = logger;
        _directory = System.IO.Path.Combine(paths.CachePath, "popcorn-subtitles");
        Cleanup();
    }

    public async Task EnsureAsync(BaseItem item, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !TryIdentity(item, out var type, out var id))
            return;
        (Guid, string, string, string, string, int, bool) cacheKey = default;
        try
        {
            var languages = PluginConfiguration.ParseLanguages(type == "series" ? config.SeriesLanguages : config.MovieLanguages);
            var groups = PluginConfiguration.ParseGroups(type == "series" ? config.SeriesReleaseGroups : config.MovieReleaseGroups);
            var baseUri = PopcornApi.GetBaseUri(config.ManifestUrl, languages, config.EnableAiNotes);
            var limit = config.ResultsPerLanguage;
            var hints = config.SearchReleaseGroups;
            cacheKey = (item.Id, baseUri.AbsoluteUri, item.Path, id, string.Join(',', groups), limit, hints);
            if (_failures.TryGetValue(cacheKey, out _)) return;
            Lazy<Task<AvailableSubtitle[]>> search;
            lock (_gate)
            {
                if (!_searches.TryGetValue(cacheKey, out search!))
                {
                    search = new Lazy<Task<AvailableSubtitle[]>>(() => SearchAsync(baseUri, type, id, item.Path, languages, groups, limit, hints));
                    _searches.Set(cacheKey, search, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
                }
            }
            var tracks = await search.Value.WaitAsync(token);
            _results.Set(item.Id, tracks, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(12) });
            foreach (var track in tracks)
                _paths.Set(track.Path, track, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(12) });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_failures.TryGetValue(cacheKey, out _))
                    _logger.LogWarning(ex, "Popcorn subtitle search failed for {ItemId}; retrying after two minutes", item.Id);
                _failures.Set(cacheKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
                _searches.Remove(cacheKey);
                _results.Remove(item.Id);
            }
        }
    }

    public static bool TryIdentity(BaseItem item, out string type, out string id)
    {
        type = "movie";
        id = "";
        if (string.IsNullOrEmpty(item.Path) || System.IO.Path.GetExtension(item.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase)
            || item.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;
        if (item is Episode episode)
        {
            type = "series";
            if (episode.ParentIndexNumber is not int season || episode.IndexNumber is not int number
                || episode.Series is not { } series || !series.ProviderIds.TryGetValue("Imdb", out var seriesId)
                || !PopcornApi.IsImdbId(seriesId))
                return false;
            id = $"{seriesId}:{season}:{number}";
            return true;
        }
        if (item is not Movie || !item.ProviderIds.TryGetValue("Imdb", out var imdb) || !PopcornApi.IsImdbId(imdb))
            return false;
        id = imdb;
        return true;
    }

    private async Task<AvailableSubtitle[]> SearchAsync(Uri baseUri, string type, string id, string filename, string[] languages, string[] groups, int limit, bool hints)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tracks = await _api.SearchAsync(baseUri, type, id, System.IO.Path.GetFileName(filename), timeout.Token, languages, groups, limit, hints);
        return tracks.Select(t =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(t.Id));
            var index = 1_000_000 + (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1_000_000_000);
            return new AvailableSubtitle(index, t, System.IO.Path.Combine(_directory, PopcornApi.CacheKey(t.Url) + ".vtt"));
        }).DistinctBy(t => t.Index).ToArray();
    }

    public IReadOnlyList<MediaStream> AddStreams(Guid itemId, IReadOnlyList<MediaStream> original)
    {
        if (Plugin.Instance?.Configuration.Enabled != true || !_results.TryGetValue(itemId, out AvailableSubtitle[]? tracks) || tracks is null)
            return original;
        var streams = original.ToList();
        foreach (var track in tracks)
        {
            if (streams.Any(s => s.Index == track.Index))
                continue;
            streams.Add(new MediaStream
            {
                Index = track.Index,
                Type = MediaStreamType.Subtitle,
                Codec = "vtt",
                Language = PopcornApi.ToBibliographic(track.Track.Lang),
                Title = $"Popcorn · {track.Track.Label}",
                Path = track.Path,
                IsExternal = true,
                SupportsExternalStream = true,
                IsForced = track.Track.Label?.Contains("Forced", StringComparison.OrdinalIgnoreCase) == true,
                IsHearingImpaired = track.Track.Label?.Contains("SDH", StringComparison.OrdinalIgnoreCase) == true
            });
        }
        return streams;
    }

    public async Task MaterializeAsync(MediaStream stream, CancellationToken token)
    {
        if (stream.Path is null || !_paths.TryGetValue(stream.Path, out AvailableSubtitle? track) || track is null)
            return;
        DownloadLock gate;
        lock (_downloadGate)
        {
            if (!_downloadLocks.TryGetValue(track.Path, out gate!))
                _downloadLocks.Add(track.Path, gate = new DownloadLock());
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(token);
            entered = true;
            if (File.Exists(track.Path))
            {
                File.SetLastWriteTimeUtc(track.Path, DateTime.UtcNow);
                return;
            }
            var bytes = await _api.DownloadAsync(track.Track.Url, token);
            Directory.CreateDirectory(_directory);
            var temp = track.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, token);
                File.Move(temp, track.Path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _logger.LogInformation("Downloaded Popcorn subtitle {Language}: {Label}", track.Track.Lang, track.Track.Label);
            Cleanup();
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (_downloadGate)
            {
                if (--gate.Users == 0)
                {
                    _downloadLocks.Remove(track.Path);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        var previous = Interlocked.Read(ref _lastCleanup);
        if (now.Ticks - previous < TimeSpan.TicksPerHour || Interlocked.CompareExchange(ref _lastCleanup, now.Ticks, previous) != previous) return;
        if (!Directory.Exists(_directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                var extension = System.IO.Path.GetExtension(file);
                var retention = extension == ".vtt" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
                if (extension is not (".vtt" or ".tmp")) continue;
                try { if (File.GetLastWriteTimeUtc(file) < now - retention) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException ex) { _logger.LogDebug(ex, "Could not clean the subtitle cache"); }
        catch (UnauthorizedAccessException ex) { _logger.LogDebug(ex, "Could not clean the subtitle cache"); }
    }

    public void Dispose()
    {
        _searches.Dispose();
        _results.Dispose();
        _failures.Dispose();
        _paths.Dispose();
    }
}
