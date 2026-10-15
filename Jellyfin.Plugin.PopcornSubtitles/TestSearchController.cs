using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed record TestSearchRequest(PluginConfiguration Configuration, string Type, string Id, string Filename);

[ApiController]
[Route("PopcornSubtitles")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class TestSearchController(PopcornApi api) : ControllerBase
{
    [HttpPost("TestSearch")]
    public async Task<ActionResult> TestSearch([FromBody] TestSearchRequest request, CancellationToken token)
    {
        try
        {
            request.Configuration.Validate();
            var parts = request.Id.Split(':');
            if (request.Type is not ("movie" or "series") || !PopcornApi.IsImdbId(parts[0])
                || (request.Type == "movie" && parts.Length != 1)
                || (request.Type == "series" && (parts.Length != 3 || !int.TryParse(parts[1], out var season) || season < 0 || !int.TryParse(parts[2], out var episode) || episode < 1))
                || string.IsNullOrWhiteSpace(request.Filename) || request.Filename.Length > 512)
                return BadRequest("Enter a movie IMDb ID or a series IMDb ID with season and episode, and a filename.");
            var config = request.Configuration;
            var languages = PluginConfiguration.ParseLanguages(request.Type == "series" ? config.SeriesLanguages : config.MovieLanguages);
            var groups = PluginConfiguration.ParseGroups(request.Type == "series" ? config.SeriesReleaseGroups : config.MovieReleaseGroups);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var tracks = await api.SearchAsync(PopcornApi.GetBaseUri(config.ManifestUrl, languages, config.EnableAiNotes), request.Type,
                request.Id, Path.GetFileName(request.Filename), timeout.Token, languages, groups, config.ResultsPerLanguage, config.SearchReleaseGroups);
            return Ok(new { count = tracks.Length, tracks = tracks.Select(t => new { language = t.Lang, label = t.Label }) });
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            return StatusCode(502, "The subtitle service did not respond successfully within the search budget. Check the URL and try again.");
        }
    }
}
