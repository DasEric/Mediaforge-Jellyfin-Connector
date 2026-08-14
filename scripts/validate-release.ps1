param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $projectRoot "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedTag = "v$($versionInfo.version)"
if ($Tag -ne $expectedTag) {
    throw "Release tag '$Tag' does not match $expectedTag from version.json"
}

[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\Jellyfin.Plugin.MediaForge.csproj") -Raw -Encoding UTF8
$properties = $project.Project.PropertyGroup | Select-Object -First 1
if ([string]$properties.InformationalVersion -ne [string]$versionInfo.version -or
    [string]$properties.Version -ne [string]$versionInfo.versionFourPart -or
    [string]$properties.AssemblyVersion -ne [string]$versionInfo.versionFourPart -or
    [string]$properties.FileVersion -ne [string]$versionInfo.versionFourPart) {
    throw "The .NET project version does not match version.json"
}

$metadata = Get-Content -LiteralPath (Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\meta.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$metadata.version -ne [string]$versionInfo.versionFourPart -or
    [string]$metadata.targetAbi -ne [string]$versionInfo.targetAbi -or
    $metadata.autoUpdate -ne $true) {
    throw "meta.json does not match version.json"
}

$module = Get-Content -LiteralPath (Join-Path $projectRoot "MediaForge.Module\mediaforge_jellyfin_connector\__init__.py") -Raw -Encoding UTF8
if ($module -notmatch ('MODULE_VERSION = "' + [regex]::Escape([string]$versionInfo.version) + '"')) {
    throw "The MediaForge module version does not match version.json"
}

$service = Get-Content -LiteralPath (Join-Path $projectRoot "Jellyfin.Plugin.MediaForge\PluginServiceRegistrator.cs") -Raw -Encoding UTF8
if ($service -notmatch ('Jellyfin-MediaForge-Requests/' + [regex]::Escape([string]$versionInfo.version))) {
    throw "Runtime version strings do not match version.json"
}

Write-Output "Release metadata is consistent for $expectedTag"
