param([string]$Repository = 'luttman/jellyfin-plugin-popcorn-subtitles')

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'Jellyfin.Plugin.PopcornSubtitles/Jellyfin.Plugin.PopcornSubtitles.csproj'
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$assemblyPath = Join-Path $PSScriptRoot 'Jellyfin.Plugin.PopcornSubtitles/bin/Release/net10.0/Jellyfin.Plugin.PopcornSubtitles.dll'
$version = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
$description = $project.Project.PropertyGroup.Description
$targetAbi = $project.Project.ItemGroup.PackageReference.Version + '.0'
$changelog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Raw
$packageName = "PopcornSubtitles_$version"
$packageDirectory = Join-Path $PSScriptRoot "dist/$packageName"
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath $assemblyPath -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $packageDirectory
@{
    category = 'Subtitles'
    guid = 'a4b728c6-9e52-47ab-9184-bc34d137efb2'
    name = 'Popcorn Subtitles'
    description = $description
    overview = 'On-demand subtitle tracks for local movies and episodes.'
    owner = $Repository.Split('/')[0]
    targetAbi = $targetAbi
    version = $version
    timestamp = [DateTime]::UtcNow.ToString('o')
    status = 'Active'
    autoUpdate = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'meta.json') -Encoding utf8NoBOM
$zipPath = Join-Path $PSScriptRoot "dist/$packageName.zip"
# Jellyfin extracts the archive into its own versioned directory; files must be at the ZIP root.
Compress-Archive -LiteralPath @(
    (Join-Path $packageDirectory 'Jellyfin.Plugin.PopcornSubtitles.dll'),
    (Join-Path $packageDirectory 'meta.json'),
    (Join-Path $packageDirectory 'README.md'),
    (Join-Path $packageDirectory 'LICENSE')
) -DestinationPath $zipPath -Force
$package = @{
    guid = 'a4b728c6-9e52-47ab-9184-bc34d137efb2'
    name = 'Popcorn Subtitles'
    overview = 'On-demand subtitles with language and release group preferences.'
    description = $description
    owner = $Repository.Split('/')[0]
    category = 'Subtitles'
    versions = @(@{
        version = $version
        targetAbi = $targetAbi
        changelog = $changelog.Trim()
        sourceUrl = "https://github.com/$Repository/releases/download/v$version/$packageName.zip"
        checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('o')
    })
}
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $package.versions += @($previous | Where-Object guid -EQ $package.guid | ForEach-Object { $_.versions } | Where-Object version -NE $version)
}
ConvertTo-Json -InputObject @($package) -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Encoding utf8NoBOM
Write-Output "Created $zipPath"
Write-Output 'Created manifest.json'
