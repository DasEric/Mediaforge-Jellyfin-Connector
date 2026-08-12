param(
    [Parameter(Mandatory = $true)]
    [string]$RepositorySlug,
    [string]$ReleaseTag
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $projectRoot "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$versionInfo.version
$versionFourPart = [string]$versionInfo.versionFourPart
$targetAbi = [string]$versionInfo.targetAbi
if ($version -notmatch '^\d+\.\d+\.\d+$' -or
    $versionFourPart -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $targetAbi -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "version.json contains an invalid version"
}
if ($RepositorySlug -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "RepositorySlug must use the owner/repository format"
}

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = "v$version"
}
if ($ReleaseTag -ne "v$version") {
    throw "Release tag '$ReleaseTag' does not match version v$version"
}

$archiveName = "MediaForgeRequests_$version.zip"
$archivePath = Join-Path $projectRoot "dist\$archiveName"
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Release archive not found: $archivePath. Run scripts/build.ps1 first."
}

$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm MD5).Hash.ToUpperInvariant()
$sourceUrl = "https://github.com/$RepositorySlug/releases/download/$ReleaseTag/$archiveName"
$manifest = @(
    [ordered]@{
        guid = "2ea7f67d-8e4d-4c84-bd5a-a5bcd713bb23"
        name = "MediaForge Requests"
        description = "Durchsucht MediaForge nach Filmen und Serien. Benutzer koennen Downloads anfragen, ausstehende Anfragen zurueckziehen und den Download-Fortschritt sehen."
        overview = "MediaForge-Suche und Download-Anfragen fuer alle Jellyfin-Benutzer"
        owner = "MediaForge Jellyfin Connector contributors"
        category = "General"
        versions = @(
            [ordered]@{
                version = $versionFourPart
                changelog = "Repository-Updates, Download-Fortschritt, Rueckzug ausstehender Anfragen und zusaetzliche Sicherheitshaertungen."
                targetAbi = $targetAbi
                sourceUrl = $sourceUrl
                checksum = $checksum
                timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            }
        )
    }
)

$repositoryDirectory = Join-Path $projectRoot "repository"
New-Item -ItemType Directory -Force -Path $repositoryDirectory | Out-Null
$manifestPath = Join-Path $repositoryDirectory "manifest.json"
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Created $manifestPath"
$owner, $repositoryName = $RepositorySlug.Split('/')
$pagesPath = if ($repositoryName -ieq "$owner.github.io") { "" } else { "/$repositoryName" }
Write-Output "Jellyfin repository URL after GitHub Pages deployment: https://$owner.github.io$pagesPath/manifest.json"
