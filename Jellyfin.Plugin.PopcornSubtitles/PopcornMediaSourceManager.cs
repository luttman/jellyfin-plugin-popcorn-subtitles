using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.PopcornSubtitles;

// Forward Jellyfin's normal media handling; only append on-demand subtitle tracks.
public sealed class PopcornMediaSourceManager(IMediaSourceManager inner, SubtitleCatalog catalog, ILibraryManager library) : IMediaSourceManager, IDisposable
{
    private MediaSourceInfo Augment(MediaSourceInfo source, BaseItem item)
    {
        var id = Guid.TryParse(source.Id, out var sourceId) ? sourceId : item.Id;
        source.MediaStreams = catalog.AddStreams(id, source.MediaStreams).ToArray();
        return source;
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var sources = await inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);
        await catalog.EnsureAsync(item, cancellationToken);
        foreach (var source in sources)
        {
            if (Guid.TryParse(source.Id, out var id) && id != item.Id && library.GetItemById(id) is { } alternate)
                await catalog.EnsureAsync(alternate, cancellationToken);
        }
        return sources.Select(s => Augment(s, item)).ToArray();
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null!) =>
        inner.GetStaticMediaSources(item, enablePathSubstitution, user).Select(s => Augment(s, item)).ToArray();

    public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var source = await inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
        if (source is null || !string.IsNullOrEmpty(liveStreamId)) return source!;
        await catalog.EnsureAsync(item, cancellationToken);
        return Augment(source, item);
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => inner.GetMediaStreams(itemId);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => inner.GetMediaAttachments(itemId);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => inner.OpenLiveStream(request, cancellationToken);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => inner.OpenLiveStreamInternal(request, cancellationToken);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => inner.GetLiveStream(id, cancellationToken);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => inner.GetLiveStreamInfoByUniqueId(uniqueId);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => inner.GetRecordingStreamMediaSources(info, cancellationToken);
    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => inner.GetLiveStreamMediaInfo(id, cancellationToken);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    public void Dispose() => (inner as IDisposable)?.Dispose();
}
