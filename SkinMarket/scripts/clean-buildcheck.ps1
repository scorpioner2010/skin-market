param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = "Stop"

$resolvedRoot = (Resolve-Path ($Root.TrimEnd('\', '/'))).Path
$directories = Get-ChildItem -Path $resolvedRoot -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*buildcheck*' } |
    Sort-Object { $_.FullName.Length } -Descending

if ($directories.Count -eq 0) {
    Write-Host "No buildcheck directories found under $resolvedRoot."
    exit 0
}

foreach ($directory in $directories) {
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    Write-Host "Removed $($directory.FullName)"
}

Write-Host "Removed $($directories.Count) buildcheck director$(if ($directories.Count -eq 1) { 'y' } else { 'ies' })."
