param([string]$Repository = 'luttman/jellyfin-plugin-popcorn-subtitles')

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'Jellyfin.Plugin.PopcornSubtitles/Jellyfin.Plugin.PopcornSubtitles.csproj'
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

$packageName = 'PopcornSubtitles_0.1.0.0'
$packageDirectory = Join-Path $PSScriptRoot "dist/$packageName"
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Jellyfin.Plugin.PopcornSubtitles/bin/Release/net10.0/Jellyfin.Plugin.PopcornSubtitles.dll') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageDirectory
@{
    category = 'Subtitles'
    guid = 'a4b728c6-9e52-47ab-9184-bc34d137efb2'
    name = 'Popcorn Subtitles'
    description = 'Swedish and English subtitles on demand from Popcorn Notes.'
    overview = 'On-demand subtitle tracks for local movies and episodes.'
    owner = $Repository.Split('/')[0]
    targetAbi = '12.2.0.0'
    version = '0.1.0.0'
    timestamp = [DateTime]::UtcNow.ToString('o')
    status = 'Active'
    autoUpdate = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'meta.json') -Encoding utf8NoBOM
$zipPath = Join-Path $PSScriptRoot "dist/$packageName.zip"
# Jellyfin extracts the archive into its own versioned directory; files must be at the ZIP root.
Compress-Archive -LiteralPath @(
    (Join-Path $packageDirectory 'Jellyfin.Plugin.PopcornSubtitles.dll'),
    (Join-Path $packageDirectory 'meta.json'),
    (Join-Path $packageDirectory 'README.md')
) -DestinationPath $zipPath -Force
$package = @{
    guid = 'a4b728c6-9e52-47ab-9184-bc34d137efb2'
    name = 'Popcorn Subtitles'
    overview = 'Swedish and English subtitles on demand.'
    description = 'On-demand subtitles from Popcorn Notes for local movies and episodes. Experimental; requires IMDb metadata. Web and Android TV playback have not yet been verified on a running server.'
    owner = $Repository.Split('/')[0]
    category = 'Subtitles'
    versions = @(@{
        version = '0.1.0.0'
        targetAbi = '12.2.0.0'
        changelog = 'Initial test version: Swedish and English subtitle search, download on selection and server-side cache.'
        sourceUrl = "https://github.com/$Repository/releases/download/v0.1.0.0/$packageName.zip"
        checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('o')
    })
}
ConvertTo-Json -InputObject @($package) -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Encoding utf8NoBOM
Write-Output "Created $zipPath"
Write-Output 'Created manifest.json'
