param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionPath = Join-Path $projectRoot "version.json"
$versionInfo = Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$oldVersion = [string]$versionInfo.version
$oldFourPart = [string]$versionInfo.versionFourPart
$newFourPart = "$Version.0"

if ($oldVersion -notmatch '^\d+\.\d+\.\d+$' -or $oldFourPart -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "The current version.json is invalid"
}
if ($Version -eq $oldVersion) {
    throw "Version is already $Version"
}

function Update-ExactText([string]$RelativePath, [string]$OldText, [string]$NewText) {
    $path = Join-Path $projectRoot $RelativePath
    $content = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    if ($content.IndexOf($OldText, [StringComparison]::Ordinal) -lt 0) {
        throw "Expected version marker was not found in $RelativePath"
    }
    $content = $content.Replace($OldText, $NewText)
    [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
}

Update-ExactText "Jellyfin.Plugin.MediaForge\Jellyfin.Plugin.MediaForge.csproj" $oldFourPart $newFourPart
Update-ExactText "Jellyfin.Plugin.MediaForge\Jellyfin.Plugin.MediaForge.csproj" "<InformationalVersion>$oldVersion</InformationalVersion>" "<InformationalVersion>$Version</InformationalVersion>"
Update-ExactText "Jellyfin.Plugin.MediaForge\meta.json" ('"version": "' + $oldFourPart + '"') ('"version": "' + $newFourPart + '"')
Update-ExactText "Jellyfin.Plugin.MediaForge\meta.json" ("v$oldVersion\n") ("v$Version\n")
Update-ExactText "Jellyfin.Plugin.MediaForge\PluginServiceRegistrator.cs" "Jellyfin-MediaForge-Requests/$oldVersion" "Jellyfin-MediaForge-Requests/$Version"
Update-ExactText "MediaForge.Module\mediaforge_jellyfin_connector\__init__.py" ('MODULE_VERSION = "' + $oldVersion + '"') ('MODULE_VERSION = "' + $Version + '"')
$versionInfo.version = $Version
$versionInfo.versionFourPart = $newFourPart
$json = @"
{
  "version": "$Version",
  "versionFourPart": "$newFourPart",
  "targetAbi": "$($versionInfo.targetAbi)"
}
"@
[IO.File]::WriteAllText($versionPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

Write-Output "Updated all release version markers to $Version."
Write-Output "Review the changelog, commit, then create and push tag v$Version."
