"""Boot a real Jellyfin container, play generated media and select a lazy subtitle."""
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = Path(__file__).resolve().parent / 'output'
OUTPUT.mkdir(exist_ok=True)
IMAGE = os.environ.get('JELLYFIN_IMAGE', 'jellyfin/jellyfin:12.2')
BASE = 'http://127.0.0.1:18096'
AUTH = 'MediaBrowser Client="Popcorn CI", Device="Integration", DeviceId="popcorn-ci", Version="1.0"'
token = ''


def command(*args, check=True):
    return subprocess.run(args, check=check, text=True, capture_output=True)


def api(path, body=None, method=None, raw=False, headers=None):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(BASE + path, data=data, method=method,
        headers={'Authorization': AUTH + (f', Token="{token}"' if token else ''),
                 'Content-Type': 'application/json', **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            content = response.read()
            return content if raw else (json.loads(content) if content else None)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'{path}: HTTP {error.code}: {error.read().decode(errors="replace")}') from error


try:
    config = OUTPUT / 'config'
    media = OUTPUT / 'media'
    plugin = config / 'plugins' / 'PopcornSubtitles'
    plugin.mkdir(parents=True, exist_ok=True)
    media.mkdir(exist_ok=True)
    shutil.copy(ROOT / 'Jellyfin.Plugin.PopcornSubtitles/bin/Release/net10.0/Jellyfin.Plugin.PopcornSubtitles.dll', plugin)
    configuration = config / 'plugins/configurations'
    configuration.mkdir(parents=True, exist_ok=True)
    (configuration / 'Jellyfin.Plugin.PopcornSubtitles.xml').write_text('''<PluginConfiguration>
<Enabled>true</Enabled><ManifestUrl>https://popcorn-fixture:8443/langs=eng,deu,fra/manifest.json</ManifestUrl>
<MovieLanguages>eng,deu,fra</MovieLanguages><SeriesLanguages>eng</SeriesLanguages>
<MovieReleaseGroups>TEST</MovieReleaseGroups><SeriesReleaseGroups />
<EnableAiNotes>false</EnableAiNotes><ResultsPerLanguage>4</ResultsPerLanguage>
</PluginConfiguration>''')
    command('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(OUTPUT / 'key.pem'),
        '-out', str(OUTPUT / 'cert.pem'), '-days', '2', '-subj', '/CN=popcorn-fixture', '-addext', 'subjectAltName=DNS:popcorn-fixture')
    (OUTPUT / 'fixture.py').write_text((Path(__file__).parent / 'fixture.py').read_text())
    (OUTPUT / 'Dockerfile').write_text(f'FROM {IMAGE}\nUSER root\nCOPY cert.pem /usr/local/share/ca-certificates/popcorn-fixture.crt\nRUN update-ca-certificates\n')
    command('docker', 'network', 'create', 'popcorn-ci')
    command('docker', 'build', '-t', 'popcorn-jellyfin-ci', str(OUTPUT))
    command('docker', 'run', '-d', '--name', 'popcorn-fixture', '--network', 'popcorn-ci', '-v', f'{OUTPUT}:/fixture:ro', 'python:3.13-slim', 'python', '/fixture/fixture.py')
    command('docker', 'run', '--rm', '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', '-v', f'{media}:/media',
        'popcorn-jellyfin-ci', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10', '-f', 'lavfi', '-i', 'sine=frequency=440',
        '-t', '4', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '/media/Fixture.2026-TEST.mp4')
    (media / 'Fixture.2026-TEST.nfo').write_text('<movie><title>CI Fixture</title><uniqueid type="imdb" default="true">tt0133093</uniqueid></movie>')
    command('docker', 'run', '-d', '--name', 'popcorn-jellyfin', '--network', 'popcorn-ci', '-p', '18096:8096',
        '-e', 'JELLYFIN_CACHE_DIR=/config/cache', '-v', f'{config}:/config', '-v', f'{media}:/media:ro', 'popcorn-jellyfin-ci')
    for attempt in range(90):
        try:
            api('/System/Info/Public')
            break
        except Exception:
            if attempt == 89: raise
            time.sleep(2)
    api('/Startup/Configuration', {'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'})
    api('/Startup/User')
    api('/Startup/User', {'Name': 'ci', 'Password': 'CI-only-test-password-123!'})
    api('/Startup/RemoteAccess', {'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False})
    api('/Startup/Complete', {}, 'POST')
    login = api('/Users/AuthenticateByName', {'Username': 'ci', 'Pw': 'CI-only-test-password-123!'})
    token = login['AccessToken']
    user = login['User']['Id']
    api('/Library/VirtualFolders?name=CI&collectionType=movies&refreshLibrary=false', {'LibraryOptions': {
        'PathInfos': [{'Path': '/media'}], 'EnableRealtimeMonitor': False,
        'TypeOptions': [{'Type': 'Movie', 'MetadataFetchers': [], 'ImageFetchers': []}]}})
    api('/Library/Refresh', {}, 'POST')
    for attempt in range(90):
        items = api(f'/Items?UserId={user}&Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds')['Items']
        if items: break
        time.sleep(2)
    assert items, 'The fixture was not scanned into the library'
    item = items[0]['Id']
    if items[0].get('ProviderIds', {}).get('Imdb') != 'tt0133093':
        details = api(f'/Users/{user}/Items/{item}')
        details.setdefault('ProviderIds', {})['Imdb'] = 'tt0133093'
        api(f'/Items/{item}', details)
    playback = api(f'/Items/{item}/PlaybackInfo?UserId={user}')
    (OUTPUT / 'playback.json').write_text(json.dumps(playback, indent=2))
    source = playback['MediaSources'][0]
    tracks = [s for s in source['MediaStreams'] if s.get('Title', '').startswith('Popcorn')]
    assert len(tracks) == 3, tracks
    assert {s['Language'] for s in tracks} == {'eng', 'ger', 'fre'}, tracks
    assert len({s['Index'] for s in source['MediaStreams']}) == len(source['MediaStreams']), 'Duplicate indexes'
    assert all(1_000_000 <= s['Index'] <= 1_001_000_000 for s in tracks)
    assert not list((config / 'cache').rglob('*.vtt')), 'Search downloaded a subtitle eagerly'
    result = api(f'/Videos/{item}/stream?Static=true&MediaSourceId={source["Id"]}', raw=True, headers={'Range': 'bytes=0-4095'})
    assert len(result) > 0, 'Video playback returned no bytes'
    api('/Sessions/Playing', {'ItemId': item, 'MediaSourceId': source['Id'], 'PlaySessionId': playback.get('PlaySessionId'), 'PositionTicks': 0, 'CanSeek': True, 'PlayMethod': 'DirectPlay'})
    selected = next(s for s in tracks if s['Language'] == 'ger')
    subtitle_path = f'/Videos/{item}/{source["Id"]}/Subtitles/{selected["Index"]}/0/Stream.vtt'
    subtitle = api(subtitle_path, raw=True)
    assert b'CI subtitle from the selected track' in subtitle, subtitle
    files = list(config.rglob('popcorn-subtitles/*.vtt'))
    assert len(files) == 1, files
    assert api(subtitle_path, raw=True) == subtitle, 'Cached delivery changed the subtitle'
    # Exercise conversion, as required by clients requesting another text format.
    assert b'CI subtitle' in api(subtitle_path.replace('.vtt', '.srt'), raw=True)
    api('/Sessions/Playing/Stopped', {'ItemId': item, 'MediaSourceId': source['Id'], 'PositionTicks': 10000000})
    print('PASS: real Jellyfin loaded the plugin, returned unique high-index tracks with eng/ger/fre, played video, and downloaded only the selected subtitle.')
finally:
    for container in ['popcorn-jellyfin', 'popcorn-fixture']:
        result = command('docker', 'logs', container, check=False)
        (OUTPUT / (container + '.log')).write_text(result.stdout + result.stderr)
        command('docker', 'rm', '-f', container, check=False)
    command('docker', 'network', 'rm', 'popcorn-ci', check=False)
    # Never upload generated server credentials or the fixture TLS key as CI artifacts.
    shutil.rmtree(OUTPUT / 'config', ignore_errors=True)
    (OUTPUT / 'key.pem').unlink(missing_ok=True)
