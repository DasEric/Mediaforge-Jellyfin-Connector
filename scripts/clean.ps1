$ErrorActionPreference = "Stop"

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$rootPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

$generatedPaths = @(
    ".ruff_cache",
    "dist",
    "repository",
    "Jellyfin.Plugin.MediaForge\bin",
    "Jellyfin.Plugin.MediaForge\obj",
    "MediaForge.Module\mediaforge_jellyfin_connector\__pycache__",
    "Tests\__pycache__",
    "Tests\Connector.SecurityTests\bin",
    "Tests\Connector.SecurityTests\obj"
)

foreach ($relativePath in $generatedPaths) {
    $target = [IO.Path]::GetFullPath((Join-Path $projectRoot $relativePath))
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the project: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Output "Removed $relativePath"
    }
}

Write-Output "Generated build output and caches have been removed."
