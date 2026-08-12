param(
    [string]$DotNet = "dotnet",
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $projectRoot "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseVersion = [string]$versionInfo.version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "version.json contains an invalid semantic version"
}
$project = Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\Jellyfin.Plugin.MediaForge.csproj"
$output = Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\bin\$Configuration\net9.0"
$dist = Join-Path $projectRoot "dist"

function Get-ContainedPath([string]$Parent, [string]$Child) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a staging path outside the distribution directory: $childFull"
    }
    return $childFull
}

$pluginStage = Get-ContainedPath $dist (Join-Path $dist "stage-plugin")
$moduleStage = Get-ContainedPath $dist (Join-Path $dist "stage-module")

$buildArguments = @("build", $project, "--configuration", $Configuration)
if ($NoRestore) {
    $buildArguments += "--no-restore"
}
& $DotNet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path -LiteralPath $pluginStage) {
    Remove-Item -LiteralPath $pluginStage -Recurse -Force
}
if (Test-Path -LiteralPath $moduleStage) {
    Remove-Item -LiteralPath $moduleStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginStage | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $moduleStage "mediaforge_jellyfin_connector") | Out-Null

$pluginDll = Join-Path $dist "Jellyfin.Plugin.MediaForge.dll"
Copy-Item -LiteralPath (Join-Path $output "Jellyfin.Plugin.MediaForge.dll") -Destination $pluginDll -Force
Copy-Item -LiteralPath $pluginDll -Destination $pluginStage
Copy-Item -LiteralPath (Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\meta.json") -Destination $pluginStage
Copy-Item -Path (Join-Path $projectRoot "MediaForge.Module\mediaforge_jellyfin_connector\*.py") -Destination (Join-Path $moduleStage "mediaforge_jellyfin_connector")

$pluginZip = Join-Path $dist "MediaForgeRequests_$releaseVersion.zip"
$moduleZip = Join-Path $dist "mediaforge_jellyfin_connector_$releaseVersion.zip"
if (Test-Path -LiteralPath $pluginZip) { Remove-Item -LiteralPath $pluginZip -Force }
if (Test-Path -LiteralPath $moduleZip) { Remove-Item -LiteralPath $moduleZip -Force }

Compress-Archive -Path (Join-Path $pluginStage "*") -DestinationPath $pluginZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $moduleStage "mediaforge_jellyfin_connector") -DestinationPath $moduleZip -CompressionLevel Optimal
Remove-Item -LiteralPath $pluginStage -Recurse -Force
Remove-Item -LiteralPath $moduleStage -Recurse -Force

$checksums = @($pluginDll, $pluginZip, $moduleZip) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS.txt") -Value $checksums -Encoding UTF8

Write-Output "Created $pluginDll"
Write-Output "Created $pluginZip"
Write-Output "Created $moduleZip"
Write-Output "Created $(Join-Path $dist 'SHA256SUMS.txt')"
