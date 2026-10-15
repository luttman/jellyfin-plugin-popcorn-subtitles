using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = "https://popcorn-notes.lsoares.workers.dev/langs=swe,eng&skip=&ai=on/manifest.json";
    public string MovieLanguages { get; set; } = "swe,eng";
    public string MovieReleaseGroups { get; set; } = "";
    public string SeriesLanguages { get; set; } = "swe,eng";
    public string SeriesReleaseGroups { get; set; } = "";

    public static string[] ParseLanguages(string? value)
    {
        var languages = SplitList(value).Select(s => s.ToLowerInvariant()).Distinct().ToArray();
        if (languages.Length is < 1 or > 10 || languages.Any(s => !Regex.IsMatch(s, "^[a-z]{3}$")))
            throw new ArgumentException("Välj 1–10 språk med trebokstavskoder, till exempel swe,eng.");
        return languages;
    }

    public static string[] ParseGroups(string? value)
    {
        var groups = SplitList(value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length > 30 || groups.Any(s => !Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")))
            throw new ArgumentException("Ange högst 30 releasegrupper med bokstäver, siffror, punkt, bindestreck eller understreck.");
        return groups;
    }

    private static string[] SplitList(string? value) => (value ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }
    public override Guid Id => Guid.Parse("a4b728c6-9e52-47ab-9184-bc34d137efb2");
    public override string Name => "Popcorn Subtitles";
    public override string Description => "Subtitles on demand with separate language and release group preferences for movies and series.";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var config = (PluginConfiguration)configuration;
        PopcornApi.GetBaseUri(config.ManifestUrl);
        PluginConfiguration.ParseLanguages(config.MovieLanguages);
        PluginConfiguration.ParseLanguages(config.SeriesLanguages);
        PluginConfiguration.ParseGroups(config.MovieReleaseGroups);
        PluginConfiguration.ParseGroups(config.SeriesReleaseGroups);
        base.UpdateConfiguration(config);
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "PopcornSubtitles", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html" }
    ];
}
