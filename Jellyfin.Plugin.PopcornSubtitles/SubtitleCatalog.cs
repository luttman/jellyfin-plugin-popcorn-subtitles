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
using System.Text.Json;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed record AvailableSubtitle(int Index, SubtitleTrack Track, string Path);

public sealed class SubtitleCatalog : IDisposable
{
    private sealed record SearchBatch(AvailableSubtitle[] Tracks, DateTimeOffset ExpiresAt);
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
        (Guid, string, string, string) cacheKey = default;
        try
        {
            cacheKey = (item.Id, PopcornApi.CacheKey(JsonSerializer.Serialize(config)), item.Path, id);
            if (_failures.TryGetValue(cacheKey, out _)) return;
            Lazy<Task<SearchBatch>> search;
            lock (_gate)
            {
                if (!_searches.TryGetValue(cacheKey, out search!))
                {
                    search = new Lazy<Task<SearchBatch>>(() => SearchAsync(config, type, id, item.Path));
                    _searches.Set(cacheKey, search, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
                }
            }
            var batch = await search.Value.WaitAsync(token);
            _searches.Set(cacheKey, search, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = batch.ExpiresAt });
            var tracks = batch.Tracks;
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
                    _logger.LogWarning("Subtitle search failed for {ItemId} ({ErrorType}); retrying after two minutes. Private URLs omitted.", item.Id, ex.GetType().Name);
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

    private async Task<SearchBatch> SearchAsync(PluginConfiguration config, string type, string id, string filename)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await _api.SearchProvidersAsync(config, type, id, System.IO.Path.GetFileName(filename), timeout.Token);
        foreach (var status in result.Providers.Where(p => p.Status == "failed"))
            _logger.LogWarning("Subtitle provider {Provider} did not respond successfully; partial results remain available and failed searches retry after two minutes", status.Name);
        var tracks = result.Tracks.Select(t =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(t.ProviderKey + ":" + t.Id));
            var index = 1_000_000 + (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1_000_000_000);
            return new AvailableSubtitle(index, t, System.IO.Path.Combine(_directory, PopcornApi.CacheKey(t.Url) + "." + t.Format));
        }).DistinctBy(t => t.Index).ToArray();
        return new SearchBatch(tracks, DateTimeOffset.UtcNow.Add(result.HasFailures ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(6)));
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
                Codec = track.Track.Format == "srt" ? "subrip" : track.Track.Format,
                Language = PopcornApi.ToBibliographic(track.Track.Lang),
                Title = $"{track.Track.ProviderName} · {(string.IsNullOrWhiteSpace(track.Track.Label) ? track.Track.Lang.ToUpperInvariant() : track.Track.Label)}",
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
            byte[] bytes;
            try { bytes = await _api.DownloadAsync(track.Track.Url, token, track.Track.Format); }
            catch (OperationCanceledException) { throw new OperationCanceledException("Subtitle download was canceled. Private URL omitted.", token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                throw new InvalidDataException("Subtitle download failed or returned an unsupported format. Private URL omitted.");
            }
            Directory.CreateDirectory(_directory);
            var temp = track.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, token);
                File.Move(temp, track.Path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _logger.LogInformation("Downloaded subtitle from {Provider}: {Language} ({Format})", track.Track.ProviderName, track.Track.Lang, track.Track.Format);
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
                var retention = extension == ".tmp" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
                if (extension is not (".vtt" or ".srt" or ".ass" or ".ssa" or ".tmp")) continue;
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
