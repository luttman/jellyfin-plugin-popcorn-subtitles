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
Check(PluginConfiguration.ParseLanguages(" ENG, swe,eng ").SequenceEqual(new[] { "eng", "swe" }), "Normalize and deduplicate languages without changing priority");
Check(PluginConfiguration.ParseGroups("NTb;FLUX\nntb").SequenceEqual(new[] { "NTb", "FLUX" }), "Normalize group lists and preserve priority");
try { PluginConfiguration.ParseLanguages("sv,en"); throw new Exception("Accepted two-letter language codes"); }
catch (ArgumentException) { checks++; }
try { PluginConfiguration.ParseGroups(".*"); throw new Exception("Accepted wildcard group"); }
catch (ArgumentException) { checks++; }
Check(PopcornApi.GetBaseUri(manifest, new[] { "dan", "eng" }).AbsoluteUri.Contains("langs=dan,eng&skip=&ai=on"), "Replace remote language setting while preserving other options");
Check(PopcornApi.GetBaseUri(manifest, new[] { "eng" }, false).AbsoluteUri.Contains("ai=off"), "AI notes must override old URLs to off");
Check(PluginConfiguration.ParseLanguages("ger,fre,deu").SequenceEqual(new[] { "deu", "fra" }), "Normalize ISO 639-2/B aliases for the provider");
Check(PopcornApi.ToBibliographic("deu") == "ger" && PopcornApi.ToBibliographic("fra") == "fre", "Use ISO 639-2/B codes for Jellyfin tracks");
Check(PopcornApi.ReleaseHint("Movie.1080p-ETHEL.mkv", "NTb") == "Movie.1080p-NTb.mkv", "Replace the group suffix when creating a search hint");
Check(PopcornApi.GetBaseUri(new PluginConfiguration().ManifestUrl, new[] { "eng" }, true).AbsoluteUri.Contains("skip=&ai=on"), "Explicit note opt-in must enable categories even on the default opt-out URL");

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
    var handler = new FakeHandler();
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
    var cacheDirectory = Path.Combine(directory, "popcorn-subtitles");
    Directory.CreateDirectory(cacheDirectory);
    var oldTemp = Path.Combine(cacheDirectory, "crashed.tmp");
    File.WriteAllText(oldTemp, "incomplete");
    File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-2));
    var recentTemp = Path.Combine(cacheDirectory, "active.tmp");
    File.WriteAllText(recentTemp, "in progress");
    var manager = provider.GetRequiredService<IMediaSourceManager>();
    Check(!File.Exists(oldTemp) && File.Exists(recentTemp), "Clean crashed temporary files without deleting active downloads");
    File.Delete(recentTemp);
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
    var failedSearches = handler.Searches;
    await provider.GetRequiredService<SubtitleCatalog>().EnsureAsync(secondMovie, default);
    Check(handler.Searches == failedSearches, "Cache failures instead of retrying every playback-info request");
    config.ResultsPerLanguage = 3;
    await provider.GetRequiredService<SubtitleCatalog>().EnsureAsync(secondMovie, default);
    Check(provider.GetRequiredService<SubtitleCatalog>().AddStreams(secondMovie.Id, []).Count == 6, "Changed settings bypass the cached failure");
    config.ResultsPerLanguage = 4;
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
    config.MovieLanguages = "eng,swe";
    config.MovieReleaseGroups = "framestor,NTb";
    var searchesBeforeSettings = handler.Searches;
    var updated = (await manager.GetPlaybackMediaSources(movie, null!, false, false, default)).Single();
    Check(handler.Searches == searchesBeforeSettings + 1, "Changing preferences must not reuse old search results");
    var updatedTracks = updated.MediaStreams.Where(s => s.Index >= 1_000_000).ToArray();
    Check(updatedTracks[0].Language == "eng", "Honor configured language order");
    Check(updatedTracks.First(s => s.Language == "swe").Title.Contains("FraMeSToR"), "Promote preferred groups before limiting to four results");
    var ordered = await api.SearchAsync(baseUri, "movie", "tt0133093", "movie.mkv", default, new[] { "swe" }, new[] { "NTb" });
    Check(ordered[0].Id == "track-6", "Match a complete group token, not NTbX");
    Check(ordered.Length == 4, "Retain fallback results alongside matching groups");
    var fallback = await api.SearchAsync(baseUri, "movie", "tt0133093", "movie.mkv", default, new[] { "swe" }, new[] { "MissingGroup" });
    Check(fallback[0].Id == "track-0" && fallback.Length == 4, "No group match must preserve provider order");
    config.MovieLanguages = "eng";
    var englishOnly = (await manager.GetPlaybackMediaSources(movie, null!, false, false, default)).Single();
    Check(englishOnly.MediaStreams.Where(s => s.Index >= 1_000_000).All(s => s.Language == "eng"), "Exclude languages not selected for movies");
    Check(handler.LastSearch!.AbsoluteUri.Contains("langs=eng&") && handler.LastSearch.AbsoluteUri.Contains("ai=off"), "Send languages and explicit note opt-out to the provider");
    config.SeriesLanguages = "swe";
    config.SeriesReleaseGroups = "NTb";
    episode.IndexNumber = 3;
    episode.Id = Guid.NewGuid();
    await provider.GetRequiredService<SubtitleCatalog>().EnsureAsync(episode, default);
    var episodeTracks = provider.GetRequiredService<SubtitleCatalog>().AddStreams(episode.Id, []);
    Check(episodeTracks.Count == 4 && episodeTracks.All(s => s.Language == "swe") && episodeTracks[0].Title.Contains("NTb"), "Series must use their own languages and release groups");
    var danishBase = PopcornApi.GetBaseUri(manifest, new[] { "dan" });
    var danish = await api.SearchAsync(danishBase, "movie", "tt0133093", "movie.mkv", default, new[] { "dan" });
    Check(danish.Length == 1 && danish[0].Lang == "dan", "Allow configured languages beyond Swedish and English");
    var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
    using var writer = new StringWriter();
    xml.Serialize(writer, config);
    var restored = (PluginConfiguration)xml.Deserialize(new StringReader(writer.ToString()))!;
    Check(restored.MovieLanguages == "eng" && restored.SeriesLanguages == "swe" && restored.SeriesReleaseGroups == "NTb", "Persist separate movie and series settings");
    handler.InvalidVtt = false;
    var beforeHints = handler.Searches;
    await api.SearchAsync(baseUri, "movie", "tt0133093", "Movie-ETHEL.mkv", default, new[] { "eng" }, new[] { "NTb", "FLUX", "FGT", "RARBG" }, 2, true);
    Check(handler.Searches == beforeHints + 4, "Limit release-hint expansion to three parallel requests plus the original filename");
    Check(handler.Filenames.Contains("Movie-NTb.mkv") && handler.Filenames.Contains("Movie-ETHEL.mkv"), "Send release hints upstream, not just local sort rules");
    var limited = await api.SearchAsync(baseUri, "movie", "tt0133093", "movie.mkv", default, new[] { "swe" }, null, 2);
    Check(limited.Length == 2, "Honor the configured hit limit");
    var beforeConcurrent = handler.Downloads;
    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetRequiredService<SubtitleCatalog>().MaterializeAsync(third, default)));
    Check(handler.Downloads == beforeConcurrent + 1, "Coalesce concurrent downloads of the same path");
    var missingServices = new ServiceCollection();
    missingServices.AddLogging();
    new ServiceRegistrator().RegisterServices(missingServices, null!);
    using var missingProvider = missingServices.BuildServiceProvider();
    foreach (var warning in missingProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()) await warning.StartAsync(default);
    Check(missingProvider.GetService<IMediaSourceManager>() is null, "Missing host services must disable integration without crashing registration");

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

sealed class FakeHandler : HttpMessageHandler
{
    public int Searches;
    public int Downloads;
    public bool FailSearch;
    public bool InvalidVtt;
    public Uri? LastSearch;
    public System.Collections.Concurrent.ConcurrentBag<string> Filenames = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.Contains("/subtitles/"))
        {
            Interlocked.Increment(ref Searches);
            LastSearch = request.RequestUri;
            var encodedFilename = request.RequestUri.AbsoluteUri.Split("/filename=")[1];
            Filenames.Add(Uri.UnescapeDataString(encodedFilename[..^5]));
            await Task.Delay(20, cancellationToken);
            if (FailSearch) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var requestBase = new Uri(request.RequestUri.AbsoluteUri[..(request.RequestUri.AbsoluteUri.IndexOf("/subtitles/", StringComparison.Ordinal) + 1)]);
            var tracks = Enumerable.Range(0, 12).Select(i => new SubtitleTrack($"track-{i}", i % 2 == 0 ? "swe" : "eng", i switch { 10 => "Blu-ray · FraMeSToR", 8 => "WEB-DL · NTbX", 6 => "WEB-DL · NTb", _ => $"WEB-DL #{i}" }, new Uri(requestBase, $"track/{i}.vtt").AbsoluteUri)).ToList();
            tracks.Add(new("danish", "dan", "WEB-DL", new Uri(requestBase, "track/dan.vtt").AbsoluteUri));
            tracks.Add(new("evil", "swe", "Unsafe", "https://evil.test/track.vtt"));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { subtitles = tracks })) };
        }
        Interlocked.Increment(ref Downloads);
        await Task.Delay(20, cancellationToken);
        return new(HttpStatusCode.OK) { Content = new StringContent(InvalidVtt ? "<html>Error</html>" : "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHej!\n", Encoding.UTF8, "text/vtt") };
    }
}
