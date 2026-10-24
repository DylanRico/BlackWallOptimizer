$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$catalogPath = Join-Path $PSScriptRoot "..\catalogs\tweaks.json"
if (-not (Test-Path $catalogPath)) {
    Write-Output "{}"
    exit 0
}

$json = Get-Content -Path $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$states = @{}

foreach ($t in $json.tweaks) {
    $isApplied = $false
    if ($t.checkCmd) {
        try {
            $res = Invoke-Expression $t.checkCmd
            if ($res -eq $true) { $isApplied = $true }
        } catch {
            $isApplied = $false
        }
    }
    $states[$t.id] = $isApplied
}

$states | ConvertTo-Json -Compress
