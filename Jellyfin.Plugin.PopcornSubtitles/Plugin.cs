using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = "https://popcorn-notes.lsoares.workers.dev/langs=swe,eng&skip=&ai=on/manifest.json";
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
    public override string Description => "Swedish and English subtitles on demand from Popcorn Notes.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "PopcornSubtitles", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html" }
    ];
}
