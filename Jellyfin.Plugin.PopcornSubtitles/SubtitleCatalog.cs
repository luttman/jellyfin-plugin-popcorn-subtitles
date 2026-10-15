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
    private readonly object _gate = new();
    private readonly SemaphoreSlim[] _downloadLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly MemoryCache _paths = new(new MemoryCacheOptions { SizeLimit = 2048 });

    public SubtitleCatalog(PopcornApi api, IApplicationPaths paths, ILogger<SubtitleCatalog> logger)
    {
        _api = api;
        _logger = logger;
        _directory = System.IO.Path.Combine(paths.CachePath, "popcorn-subtitles");
    }

    public async Task EnsureAsync(BaseItem item, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !TryIdentity(item, out var type, out var id))
            return;
        Lazy<Task<AvailableSubtitle[]>> search;
        lock (_gate)
        {
            var cacheKey = (item.Id, config.ManifestUrl, item.Path);
            if (!_searches.TryGetValue(cacheKey, out search!))
            {
                search = new Lazy<Task<AvailableSubtitle[]>>(() => SearchAsync(config.ManifestUrl, type, id, item.Path));
                _searches.Set(cacheKey, search, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
            }
        }
        try
        {
            var tracks = await search.Value.WaitAsync(token);
            _searches.Set(item.Id, tracks, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(12) });
            foreach (var track in tracks)
                _paths.Set(track.Path, track, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(12) });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Failed searches must be retried; the video must remain playable.
            _searches.Remove((item.Id, config.ManifestUrl, item.Path));
            _logger.LogWarning(ex, "Popcorn subtitle search failed for {ItemId}; continuing playback", item.Id);
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

    private async Task<AvailableSubtitle[]> SearchAsync(string manifest, string type, string id, string filename)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracks = await _api.SearchAsync(PopcornApi.GetBaseUri(manifest), type, id, System.IO.Path.GetFileName(filename), timeout.Token);
        return tracks.Select(t =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(t.Id));
            var index = 1_000_000 + (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1_000_000_000);
            return new AvailableSubtitle(index, t, System.IO.Path.Combine(_directory, PopcornApi.CacheKey(t.Url) + ".vtt"));
        }).DistinctBy(t => t.Index).ToArray();
    }

    public IReadOnlyList<MediaStream> AddStreams(Guid itemId, IReadOnlyList<MediaStream> original)
    {
        if (Plugin.Instance?.Configuration.Enabled != true || !_searches.TryGetValue(itemId, out AvailableSubtitle[]? tracks) || tracks is null)
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
                Language = track.Track.Lang,
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
        var gate = _downloadLocks[(uint)track.Index % _downloadLocks.Length];
        await gate.WaitAsync(token);
        try
        {
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
        finally { gate.Release(); }
    }

    private void Cleanup()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.vtt"))
        {
            try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _searches.Dispose();
        _paths.Dispose();
        foreach (var gate in _downloadLocks) gate.Dispose();
    }
}
