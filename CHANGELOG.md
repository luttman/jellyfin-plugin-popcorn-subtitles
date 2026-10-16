# 0.4.0.0

- Public Popcorn Notes and OpenSubtitles providers are included by default.
- Add, remove, enable or disable up to eight additional Stremio subtitle providers.
- SubSense has an empty private-URL row; personal URLs are never included in source or releases.
- Preserve opaque provider paths, mask private URLs in settings, omit request URLs from errors, and require explicit CDN download hostnames.
- Label tracks by provider, namespace their IDs, apply result limits per provider and language, and isolate provider failures.
- Support SRT, VTT and declared ASS/SSA subtitles with the correct codec and cache extension.
- Test search reports per-provider status; real-server CI covers mixed VTT/SRT providers and one unavailable provider.

# 0.3.0.0

- Optional upstream release-group filename hints, capped at three extra parallel requests.
- Real Jellyfin 12.2 container CI: playback track list, ISO 639-2/B language codes, video delivery and on-demand subtitle conversion.
- Three-second shared search budget, parallel alternate-version searches, and a two-minute failure cache.
- Separate search/result caches, DNS connection refresh, per-path download locks, and stale temporary-file cleanup.
- Explicit opt-in for AI and informational notes, off by default even for older manifest URLs.
- English settings and documentation, configurable results per language, and an administrator-only test-search button.
- GPL-3.0-only license, safer missing-service registration, and release metadata derived from the assembly/project and this changelog.
