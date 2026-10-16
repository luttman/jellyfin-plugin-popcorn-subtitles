using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System.Text.RegularExpressions;
using System.Reflection;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = "https://popcorn-notes.lsoares.workers.dev/langs=swe,eng&skip=card,recap,trivia,goofs,characters,ratings&ai=off/manifest.json";
    public bool EnableAiNotes { get; set; }
    public bool EnablePopcorn { get; set; } = true;
    public SubtitleProviderConfiguration[] AdditionalProviders { get; set; } =
    [
        new() { Name = "OpenSubtitles", ManifestUrl = "https://opensubtitles-v3.strem.io/manifest.json", DownloadHosts = "subs5.strem.io" },
        new() { Name = "SubSense", Enabled = false, DownloadHosts = "dl.opensubtitles.org,sub.wyzie.io" }
    ];
    public bool SearchReleaseGroups { get; set; }
    public int ResultsPerLanguage { get; set; } = 4;
    public string MovieLanguages { get; set; } = "swe,eng";
    public string MovieReleaseGroups { get; set; } = "";
    public string SeriesLanguages { get; set; } = "swe,eng";
    public string SeriesReleaseGroups { get; set; } = "";

    public static string[] ParseLanguages(string? value)
    {
        var languages = SplitList(value).Select(s => PopcornApi.ToTerminologic(s.ToLowerInvariant())).Distinct().ToArray();
        if (languages.Length is < 1 or > 10 || languages.Any(s => !Regex.IsMatch(s, "^[a-z]{3}$")))
            throw new ArgumentException("Choose 1–10 languages using three-letter codes, for example swe,eng.");
        return languages;
    }

    public static string[] ParseGroups(string? value)
    {
        var groups = SplitList(value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length > 30 || groups.Any(s => !Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")))
            throw new ArgumentException("Enter up to 30 release groups using letters, numbers, dots, hyphens or underscores.");
        return groups;
    }

    private static string[] SplitList(string? value) => (value ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public void Validate()
    {
        PopcornApi.GetBaseUri(ManifestUrl);
        ParseLanguages(MovieLanguages);
        ParseLanguages(SeriesLanguages);
        ParseGroups(MovieReleaseGroups);
        ParseGroups(SeriesReleaseGroups);
        if (ResultsPerLanguage is < 1 or > 20)
            throw new ArgumentException("Results per language must be between 1 and 20.");
        if (AdditionalProviders is null || AdditionalProviders.Length > 8)
            throw new ArgumentException("Add up to eight additional providers.");
        foreach (var provider in AdditionalProviders)
        {
            if (provider is null) throw new ArgumentException("Provider entries cannot be null.");
            provider.Validate();
        }
    }

    public SubtitleProviderConfiguration[] GetProviders() =>
        (EnablePopcorn ? new[] { new SubtitleProviderConfiguration { Name = "Popcorn", ManifestUrl = ManifestUrl, PopcornOptions = true } } : [])
        .Concat(AdditionalProviders ?? []).Where(p => p.Enabled).ToArray();
}

public sealed class SubtitleProviderConfiguration
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = "";
    public string DownloadHosts { get; set; } = "";
    [System.Xml.Serialization.XmlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool PopcornOptions { get; set; }

    public string[] GetDownloadHosts() => (DownloadHosts ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60 || Name.Any(char.IsControl))
            throw new ArgumentException("Each provider needs a name of 1–60 characters.");
        if (Enabled || !string.IsNullOrWhiteSpace(ManifestUrl)) PopcornApi.GetBaseUri(ManifestUrl);
        var hosts = GetDownloadHosts();
        if (hosts.Length > 12 || hosts.Any(h => Uri.CheckHostName(h) != UriHostNameType.Dns || h.Contains('*')))
            throw new ArgumentException("Download hosts must be comma-separated DNS hostnames without paths, ports or wildcards.");
    }
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
    public override string Description => typeof(Plugin).Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description;

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var config = (PluginConfiguration)configuration;
        config.Validate();
        base.UpdateConfiguration(config);
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "PopcornSubtitles", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html" }
    ];
}
