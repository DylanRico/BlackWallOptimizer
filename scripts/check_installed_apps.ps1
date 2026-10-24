$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$catalogPath = Join-Path $PSScriptRoot "..\catalogs\apps.json"
if (-not (Test-Path $catalogPath)) {
    Write-Output "{}"
    exit 0
}

$appsCatalog = (Get-Content -Path $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json).apps

$regPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
)

$installedEntries = Get-ItemProperty $regPaths -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName } |
    Select-Object DisplayName, DisplayVersion

$statusMap = @{}

foreach ($app in $appsCatalog) {
    $searchPattern = if ($app.detectPattern) { $app.detectPattern } else { $app.name }
    $match = $installedEntries | Where-Object { $_.DisplayName -match $searchPattern } | Select-Object -First 1

    if ($match) {
        $statusMap[$app.id] = @{
            installed = $true
            version   = if ($match.DisplayVersion) { [string]$match.DisplayVersion } else { "Instalado" }
        }
    } else {
        $statusMap[$app.id] = @{
            installed = $false
            version   = $null
        }
    }
}

$statusMap | ConvertTo-Json -Depth 3 -Compress
