using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class PopcornSubtitleEncoder(ISubtitleEncoder inner, IMediaSourceManager sources, SubtitleCatalog catalog) : ISubtitleEncoder, IDisposable
{
    public async Task<Stream> GetSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, long startTimeTicks, long endTimeTicks, bool preserveOriginalTimestamps, CancellationToken cancellationToken)
    {
        var source = await sources.GetMediaSource(item, mediaSourceId, null!, false, cancellationToken);
        var stream = source?.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == subtitleStreamIndex);
        if (stream is not null) await catalog.MaterializeAsync(stream, cancellationToken);
        return await inner.GetSubtitles(item, mediaSourceId, subtitleStreamIndex, outputFormat, startTimeTicks, endTimeTicks, preserveOriginalTimestamps, cancellationToken);
    }

    public async Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        await catalog.MaterializeAsync(subtitleStream, cancellationToken);
        return await inner.GetSubtitleFilePath(subtitleStream, mediaSource, cancellationToken);
    }

    public async Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        await catalog.MaterializeAsync(subtitleStream, cancellationToken);
        return await inner.GetSubtitleFileCharacterSet(subtitleStream, language, mediaSource, cancellationToken);
    }

    public Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken) => inner.ExtractAllExtractableSubtitles(mediaSource, cancellationToken);
    public void Dispose() => (inner as IDisposable)?.Dispose();
}
