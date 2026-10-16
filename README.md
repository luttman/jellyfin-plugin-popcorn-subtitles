# Popcorn Subtitles for Jellyfin

On-demand subtitles from multiple Stremio subtitle providers for local movies and episodes on **Jellyfin Server 12.2**, with separate language and release preferences for movies and series. Requires IMDb metadata. The plugin adds provider-labeled external text tracks to playback information, downloads a track when Jellyfin requests it, and uses Jellyfin's subtitle encoder for delivery and conversion.

## Install

Add a repository under **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/luttman/jellyfin-plugin-popcorn-subtitles/main/manifest.json
```

Install **Popcorn Subtitles** from the catalog and restart Jellyfin. Open its settings under **Dashboard → Plugins → Popcorn Subtitles**. Select a track named `Popcorn` from the player's subtitle menu.

For manual installation, download the ZIP from [Releases](https://github.com/luttman/jellyfin-plugin-popcorn-subtitles/releases), create `PopcornSubtitles_0.4.0.0` under the server's plugins directory, extract the ZIP contents directly there, and restart. The official Docker image normally uses `/config/plugins`.

## Subtitle providers and private URLs

**Popcorn Notes** and the public **OpenSubtitles v3 Stremio addon** are preconfigured and enabled. They are independent services: the public Stremio endpoint is not the account/API-key integration from Jellyfin's separate OpenSubtitles plugin. Each can be disabled independently. To enable **SubSense**, paste your own manifest URL into its initially empty row and select Enable. This repository does not contain a personal SubSense configuration.

Use **Add provider** for more Stremio subtitle manifests, up to eight additional entries. Set a display name, HTTPS manifest URL, enable switch and any additional download hostnames. The manifest must support the usual `/subtitles/movie/{imdb}/filename=...json` or series equivalent. Generic provider paths, including opaque private configuration tokens, are preserved exactly; Popcorn-specific language/note path options are applied only to Popcorn Notes. Results from generic providers are filtered to your configured languages locally.

Private URL fields are masked by default. **Show provider URLs** reveals them for editing. They are saved in Jellyfin's plugin XML configuration and are available through its administrator-only configuration API; they are not encrypted at rest by this plugin. Protect server configuration files and backups. Enter private URLs only in your server's settings, not in GitHub issues, CI variables, repository files or public screenshots. Test search returns names, status, language and labels, never manifest/download URLs. Plugin request errors and logs omit credential-bearing URLs. Server-wide tracing, reverse proxies and third-party providers have their own logging behavior.

The manifest origin may serve its own downloads. External HTTPS hosts must be listed explicitly, without wildcards, ports or paths. Defaults observed in live tests are `subs5.strem.io` for the public OpenSubtitles addon and `dl.opensubtitles.org,sub.wyzie.io` for SubSense. If an addon moves to another CDN, verify that host and add it in settings. Downloads and redirects to other hosts are rejected; redirected, ZIP/gzip or unsupported responses are not automatically unpacked.

Providers are searched in parallel within the same three-second budget. A failed provider does not discard another provider's results. Partial/all-failure searches are reused for two minutes before retrying; fully successful searches remain cached for six hours. Results are limited **per language, per provider**. Providers may overlap, so similar subtitles can appear more than once. Tracks show their provider name, and IDs are namespaced to avoid collisions between providers.

Supported subtitle formats are VTT, SRT and ASS/SSA announced by a filename or format label. If the provider does not declare a format, generic results default to SRT. The selected file is validated before caching and Jellyfin performs conversions for the client. No subtitle bytes are downloaded during search.

## Languages and release searches

Movies and series have independent ordered language lists and release-group lists. Examples:

| Setting | Movies | Series |
| --- | --- | --- |
| Languages | `swe,eng` | `eng,swe` |
| Release groups | `FraMeSToR,FGT,RARBG` | `NTb,FLUX,KOGi` |

Examples are not preselected groups. Both language lists default to `swe,eng`; group lists default to empty. Up to 10 languages and 30 groups are supported. Use ISO 639-2 codes: bibliographic aliases such as `ger` and `fre` are accepted, normalized to `deu`/`fra` for the provider and emitted as `ger`/`fre` for Jellyfin.

Group names are matched as complete tokens in provider labels, case-insensitively: `NTb` does not match `NTbX`. Matches are ranked before the **Results per language, per provider** limit, configurable from 1 to 20 (default 4). Other releases remain as fallback. Provider metadata may omit the release group; matching cannot establish which encode an unlabeled subtitle fits. The public OpenSubtitles addon often provides no release label.

**Send release-group hints to the provider** is off by default. When enabled, the actual filename is searched alongside filename hints for the first three preferred groups. For example, `Movie.1080p-ETHEL.mkv` produces an additional hint `Movie.1080p-NTb.mkv`. These are requests to the provider, not only local filters. They preserve the IMDb identity and do not rename the video. Requests run in parallel under the same three-second budget. Some providers use filenames only for ranking; this cannot force a missing release to appear or guarantee subtitle synchronization.

Use **Test search** to check the current unsaved URL, language lists, result limit and release hints without playing a video. Enter a movie IMDb ID such as `tt0133093`, or a series ID with season and episode such as `tt0903747:1:3`. Tests search metadata only; they do not download subtitle files. This endpoint requires an administrator account.

Saved changes apply to the next playback and bypass search results and failure entries for older settings. Restart an existing playback to refresh its choices.

## Performance and cache

- The complete search for each video has a **three-second budget**. Alternate versions are searched concurrently, rather than adding one timeout per version. Release hints add up to three parallel requests per version and may reach provider rate limits sooner.
- Concurrent requests for the same search share one task. Successful searches are cached for six hours. Failed searches are cached for two minutes, so an outage does not stall every playback-info request. Search tasks, playable results and failures have independent bounded caches.
- Downloads happen only when a track is requested. Each file has its own lock, so unrelated tracks do not block one another. Downloaded subtitles are stored under `popcorn-subtitles` in Jellyfin's configured cache directory, with format-appropriate extensions. There are no placeholder files and no subtitle database writes.
- Cached subtitles unused for seven days and temporary files older than one day are removed at startup and, at most hourly, after downloads. Recent temporary files are left alone. This is age-based cleanup, not a fixed disk quota.
- HTTP connections are recycled after five minutes to refresh DNS. Redirects and responses over 8 MiB are rejected. Popcorn tracks must remain under its track path; generic addons can use their manifest origin and explicitly configured HTTPS CDN hosts.

## Notes, service ownership and privacy

The default service is the **external `lsoares` deployment** at [popcorn-notes.lsoares.workers.dev](https://popcorn-notes.lsoares.workers.dev/). Its public configuration page publishes `lsoares@gmail.com` as a contact. It is not run by this repository or by Jellyfin. The operator's full identity and ownership have not been independently verified.

The service and its infrastructure can see your server's public IP address, requested IMDb IDs, language settings, filenames and any synthetic release hints. Your video bytes are not uploaded. **No verified public logging or retention policy was found; what the operator logs and how long it retains data are unknown.** Do not assume requests are anonymous or unlogged. The operator can change its behavior independently of this plugin.

**Allow AI and added informational notes** is an explicit opt-in, off by default, including upgrades from URLs containing `ai=on`. When off, requests force `ai=off` and skip the service's title-card, recap, trivia, goofs, characters and ratings categories. Enabling it allows these categories. This is not a control for AI-translated subtitles. The provider may still add its own messages or other content; this plugin does not guarantee clean or unmodified subtitle text.

Normal plugin logs include provider names, download language/format and warnings about failures. Request exceptions omit URLs. There is no separate analytics or telemetry service in the plugin. Every enabled provider receives search metadata; disabling Popcorn does not disable other providers.

## Verification and version coupling

[GitHub Actions](https://github.com/luttman/jellyfin-plugin-popcorn-subtitles/actions/workflows/verify.yml) builds the plugin, runs isolated tests, and starts a real `jellyfin/jellyfin:12.2` container with a deterministic HTTPS subtitle fixture. The test generates a video, scans it into a library, checks the playback track list and the unique indexes in the 1,000,000–1,001,000,000 range, verifies `eng`/`ger`/`fre` language codes, reads video bytes, reports a playback session, selects a subtitle, checks lazy disk caching and tests text-format conversion. Logs and playback JSON are retained as CI artifacts, without generated credentials or TLS private keys.

The test also posts representative web-style VTT and Android TV-style SRT device profiles, checks external delivery URLs, and fetches the resulting subtitles through Jellyfin. A generic provider returns SRT with duplicate external IDs, while a third provider returns an error: CI verifies that both working providers remain playable and that IDs remain unique. This verifies the server API and subtitle pipeline. It **does not verify rendering or interaction in a browser or on an Android TV device**. Those clients still need manual testing. Live third-party searches are optional and excluded from CI for reliability.

The plugin wraps `IMediaSourceManager` and `ISubtitleEncoder` and is compiled against `Jellyfin.Controller 12.2.0` and .NET 10. These interfaces can change across server releases; an added or changed member can prevent loading before runtime guards execute. If the expected services are absent at registration, playback integration is disabled with a startup warning. This does not make the plugin compatible with arbitrary future versions. **For every supported Jellyfin upgrade, update the package target, add its container version to the CI matrix, run the pipeline and manually test clients before publishing.**

## Build and test

Requires .NET SDK 10. Node.js is used for the settings tests; Python 3, Docker and OpenSSL are used for the integration test.

```powershell
dotnet run --project tests/PopcornSubtitles.SelfTest -c Release
node tests/settings-page.test.cjs
./build.ps1
```

Optional live third-party search and download:

```powershell
dotnet run --project tests/PopcornSubtitles.SelfTest -c Release -- --live
```

On a Linux Docker host:

```sh
python3 tests/integration/run.py
```

The build reads the version from the assembly, description/ABI from the project and changelog from `CHANGELOG.md`. It produces a root-level ZIP with the plugin, README, license and metadata, and updates `manifest.json` with its MD5 checksum while preserving older versions. Publish exactly that ZIP to the matching release tag. Rebuilding changes the ZIP checksum; always update the manifest and artifact together. Never replace an already published version with different bytes.

## Troubleshooting and removal

Check the item's IMDb metadata and the server log for `Popcorn`. Series also require season and episode numbers. `.strm` and HTTP video sources are not supported. Tracks are added to playback information and may not appear on the item's detail page beforehand.

Disable the plugin in settings to stop adding tracks. To remove it, stop Jellyfin, remove its plugin directory, and restart. Its `popcorn-subtitles` cache directory can be removed separately while the server is stopped.

## License

[GPL-3.0-only](LICENSE). The referenced `Jellyfin.Controller 12.2.0` NuGet package declares the same license. Jellyfin's runtime libraries are supplied by the server and are not included in plugin ZIPs. Container images, test dependencies and the external subtitle service retain their own terms.
