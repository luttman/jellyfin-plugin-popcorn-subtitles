using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.PopcornSubtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

const string manifest = "https://example.test/langs=swe,eng&skip=&ai=on/manifest.json";
var baseUri = PopcornApi.GetBaseUri(manifest);
Check(baseUri.AbsoluteUri.EndsWith("/langs=swe,eng&skip=&ai=on/"), "Configuration must remain in the URL path");
Check(!PopcornApi.IsTrackUri(baseUri, "https://evil.test/track/a.vtt"), "Reject track URLs from other hosts");
Check(!PopcornApi.IsTrackUri(baseUri, "https://example.test/private"), "Reject unrelated paths");
Check(!PopcornApi.IsImdbId("../movie"), "Reject malformed IMDb IDs");
try { PopcornApi.GetBaseUri("http://localhost/manifest.json"); throw new Exception("HTTP manifest accepted"); }
catch (ArgumentException) { checks++; }

var directory = Path.Combine(Path.GetTempPath(), "popcorn-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var config = new PluginConfiguration { ManifestUrl = manifest };
    var paths = Stub.For<IApplicationPaths>((_, _) => directory);
    var serializer = Stub.For<IXmlSerializer>((method, _) => method.Name.StartsWith("Deserialize") ? config : null);
    var plugin = new Plugin(paths, serializer);
    var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(directory, "The.Matrix.1999.1080p.BluRay.mkv") };
    movie.ProviderIds["Imdb"] = "tt0133093";
    MediaSourceInfo Source() => new()
    {
        Id = movie.Id.ToString("N"), Path = movie.Path,
        MediaStreams = [new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" }, new() { Index = 1, Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng" }],
        DefaultSubtitleStreamIndex = 1
    };
    var original = Stub.For<IMediaSourceManager>((method, _) => method.Name switch
    {
        "GetPlaybackMediaSources" => Task.FromResult<IReadOnlyList<MediaSourceInfo>>([Source()]),
        "GetStaticMediaSources" => (IReadOnlyList<MediaSourceInfo>)[Source()],
        "GetMediaSource" => Task.FromResult(Source()),
        "SupportsDirectStream" => true,
        _ => null
    });
    var handler = new FakeHandler(baseUri);
    using var api = new PopcornApi(handler);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(paths);
    services.AddSingleton(Stub.For<ILibraryManager>((_, _) => null));
    services.AddSingleton(original);
    services.AddSingleton<ISubtitleEncoder>(provider => Stub.For<ISubtitleEncoder>((method, arguments) =>
    {
        if (method.Name == "GetSubtitles")
        {
            var sources = provider.GetRequiredService<IMediaSourceManager>();
            var source = sources.GetStaticMediaSources(movie, false).Single();
            var selected = source.MediaStreams.Single(s => s.Index == (int)arguments![2]!);
            return Task.FromResult<Stream>(File.OpenRead(selected.Path));
        }
        if (method.Name == "GetSubtitleFilePath")
        {
            var stream = (MediaStream)arguments![0]!;
            Check(File.Exists(stream.Path), "Burn-in path must exist before delegating to Jellyfin");
            return Task.FromResult(stream.Path);
        }
        return null;
    }));
    new ServiceRegistrator().RegisterServices(services, null!);
    services.AddSingleton(api);
    using var provider = services.BuildServiceProvider();
    var manager = provider.GetRequiredService<IMediaSourceManager>();
    var encoder = provider.GetRequiredService<ISubtitleEncoder>();
    Check(manager.SupportsDirectStream("movie.mkv", MediaBrowser.Model.MediaInfo.MediaProtocol.File), "Forward existing media behavior");
    var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.GetPlaybackMediaSources(movie, null!, false, false, default)));
    var source = responses[0].Single();
    Check(handler.Searches == 1, "Concurrent playback requests must share one search");
    Check(handler.Downloads == 0, "Opening playback must not download subtitles");
    Check(source.MediaStreams.Count == 10, "Keep existing tracks and add at most four per language");
    Check(source.DefaultSubtitleStreamIndex == 1, "Keep Jellyfin's existing subtitle choice");
    Check(source.MediaStreams.Skip(2).First().Language == "swe", "Show Swedish results first");
    Check(!Directory.EnumerateFiles(directory, "*.vtt", SearchOption.AllDirectories).Any(), "Search must not create placeholder subtitle files");
    var selected = source.MediaStreams.First(s => s.Index >= 1_000_000);
    Check(Path.GetFileName(selected.Path).Length == 68, "Use a hash, not upstream filenames, for disk paths");
    await using (var content = await encoder.GetSubtitles(movie, source.Id, selected.Index, "vtt", 0, 0, true, default))
        Check(new StreamReader(content).ReadToEnd().StartsWith("WEBVTT"), "Deliver the selected track");
    Check(handler.Downloads == 1, "Download only the selected track");
    await using (var content = await encoder.GetSubtitles(movie, source.Id, selected.Index, "vtt", 0, 0, true, default)) { }
    Check(handler.Downloads == 1, "Repeated selection must use the disk cache");
    var next = source.MediaStreams.Last();
    await encoder.GetSubtitleFilePath(next, source, default);
    Check(handler.Downloads == 2, "Jellyfin's burn-in path must also fetch on demand");
    config.Enabled = false;
    Check((await manager.GetPlaybackMediaSources(movie, null!, false, false, default)).Single().MediaStreams.Count == 2, "Disabling must restore existing tracks");
    config.Enabled = true;
    var secondMovie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(directory, "Second.mkv") };
    secondMovie.ProviderIds["Imdb"] = "tt0000001";
    handler.FailSearch = true;
    await provider.GetRequiredService<SubtitleCatalog>().EnsureAsync(secondMovie, default);
    Check(provider.GetRequiredService<SubtitleCatalog>().AddStreams(secondMovie.Id, []).Count == 0, "Service failure must leave playback untouched");
    handler.FailSearch = false;
    await provider.GetRequiredService<SubtitleCatalog>().EnsureAsync(secondMovie, default);
    Check(provider.GetRequiredService<SubtitleCatalog>().AddStreams(secondMovie.Id, []).Count == 8, "Retry a failed search");
    var third = source.MediaStreams.Skip(3).First();
    handler.InvalidVtt = true;
    try
    {
        await provider.GetRequiredService<SubtitleCatalog>().MaterializeAsync(third, default);
        throw new Exception("Accepted invalid VTT");
    }
    catch (InvalidDataException) { checks++; }
    Check(!File.Exists(third.Path), "Never cache an HTML/error response as a subtitle");
    Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "Leave no partial downloads");
    Check(!SubtitleCatalog.TryIdentity(new Movie { Path = "movie.strm" }, out _, out _), "Skip external stream files");
    Check(!SubtitleCatalog.TryIdentity(new Movie { Path = "movie.mkv" }, out _, out _), "Skip movies without IMDb metadata");
    var series = new Series { Id = Guid.NewGuid() };
    series.ProviderIds["Imdb"] = "tt0903747";
    BaseItem.LibraryManager = Stub.For<ILibraryManager>((method, _) => method.Name == "GetItemById" ? series : null);
    var episode = new Episode { Path = "episode.mkv", SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 3 };
    Check(SubtitleCatalog.TryIdentity(episode, out var type, out var episodeId) && type == "series" && episodeId == "tt0903747:1:3", "Use series IMDb ID plus season and episode");
    episode.IndexNumber = null;
    Check(!SubtitleCatalog.TryIdentity(episode, out _, out _), "Skip episodes with incomplete numbering");

    if (args.Contains("--live"))
    {
        using var live = new PopcornApi();
        var liveBase = PopcornApi.GetBaseUri(new PluginConfiguration().ManifestUrl);
        var tracks = await live.SearchAsync(liveBase, "movie", "tt0133093", "The.Matrix.1999.1080p.BluRay.mkv", default);
        var swedish = tracks.First(t => t.Lang == "swe");
        var bytes = await live.DownloadAsync(swedish.Url, default);
        Check(bytes.Length > 100, "Live Swedish VTT must contain subtitle data");
        Console.WriteLine($"Live service: {tracks.Length} tracks; Swedish VTT {bytes.Length} bytes.");
    }
    Console.WriteLine($"PASS: {checks} checks.");
}
finally { Directory.Delete(directory, recursive: true); }

public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    public static T For<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var value = Create<T, Stub>();
        ((Stub)(object)value).Handler = handler;
        return value;
    }
}

sealed class FakeHandler(Uri baseUri) : HttpMessageHandler
{
    public int Searches;
    public int Downloads;
    public bool FailSearch;
    public bool InvalidVtt;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.Contains("/subtitles/"))
        {
            Interlocked.Increment(ref Searches);
            await Task.Delay(20, cancellationToken);
            if (FailSearch) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var tracks = Enumerable.Range(0, 12).Select(i => new SubtitleTrack($"track-{i}", i % 2 == 0 ? "swe" : "eng", $"WEB-DL #{i}", new Uri(baseUri, $"track/{i}.vtt").AbsoluteUri)).ToList();
            tracks.Add(new("evil", "swe", "Unsafe", "https://evil.test/track.vtt"));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { subtitles = tracks })) };
        }
        Interlocked.Increment(ref Downloads);
        return new(HttpStatusCode.OK) { Content = new StringContent(InvalidVtt ? "<html>Error</html>" : "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHej!\n", Encoding.UTF8, "text/vtt") };
    }
}
