# rename-zones.ps1
# Renames zone labels in dataset/labels.csv and dataset/img/.
# Usage:
#   .\rename-zones.ps1 -From limbs -To legs
#   .\rename-zones.ps1 -From limbs -To legs -Apply
# Without -Apply it only reports what would change.

param(
    [Parameter(Mandatory)][string]$From,
    [Parameter(Mandatory)][string]$To,
    [switch]$Apply
)

$root = Join-Path $PSScriptRoot 'dataset'
$csv  = Join-Path $root 'labels.csv'
$img  = Join-Path $root 'img'

if (-not (Test-Path $csv)) { Write-Error "no labels.csv at $csv"; exit 1 }

$rows  = Get-Content $csv
$hits  = $rows | Where-Object { ($_ -split ',')[1] -eq $From }
$files = Get-ChildItem $img -Filter "*_$From.png" -ErrorAction SilentlyContinue

Write-Host "label '$From' -> '$To'"
Write-Host "  csv rows : $($hits.Count)"
Write-Host "  png files: $($files.Count)"

if ($hits.Count -ne $files.Count) {
    Write-Warning "row and file counts differ - something is already out of sync"
}

if (-not $Apply) { Write-Host "`ndry run. re-run with -Apply to commit."; exit 0 }

# csv first, so it never points at a name that is gone
$rows | ForEach-Object {
    $p = $_ -split ','
    if ($p[1] -eq $From) { $p[1] = $To; $_ = $p -join ',' }
    $_
} | Set-Content $csv

foreach ($f in $files) {
    $new = $f.Name -replace "_$From\.png$", "_$To.png"
    Rename-Item $f.FullName -NewName $new
}

Write-Host "done."