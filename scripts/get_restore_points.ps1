$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$points = @()
try {
    $rawPoints = @(Get-ComputerRestorePoint | Sort-Object SequenceNumber -Descending | Select-Object -First 15)
    foreach ($rp in $rawPoints) {
        $dtStr = $rp.CreationTime
        try {
            $parsed = [System.Management.ManagementDateTimeConverter]::ToDateTime($rp.CreationTime)
            $dtStr = $parsed.ToString("yyyy-MM-dd HH:mm:ss")
        } catch {}

        $points += @{
            sequenceNumber = [int]$rp.SequenceNumber
            description    = [string]$rp.Description
            creationTime   = [string]$dtStr
            eventType      = [string]$rp.RestorePointType
        }
    }
} catch {}

$srEnabled = $false
try {
    $srVal = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore" -Name "RPSessionInterval" -ErrorAction SilentlyContinue).RPSessionInterval
    if ($null -ne $srVal -and $srVal -gt 0) { $srEnabled = $true }
    if ($points.Count -gt 0) { $srEnabled = $true }
} catch {}

$out = @{
    protectionEnabled = $srEnabled
    points            = $points
}

$out | ConvertTo-Json -Depth 4 -Compress
