$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$targetNames = @('powershell.exe', 'pwsh.exe', 'cmd.exe', 'wscript.exe', 'cscript.exe', 'mshta.exe')
$myPid = $PID

$procs = Get-CimInstance -ClassName Win32_Process | Where-Object {
    $targetNames -contains $_.Name.ToLower() -and $_.ProcessId -ne $myPid
}

$result = @()
foreach ($p in $procs) {
    $cmdLine = if ($p.CommandLine) { [string]$p.CommandLine } else { [string]$p.ExecutablePath }
    $decodedHint = $null
    if ($cmdLine -match '(?i)-(?:e|enc|encodedcommand)\s+([A-Za-z0-9+/=]{16,})') {
        try {
            $bytes = [System.Convert]::FromBase64String($matches[1])
            $decodedHint = [System.Text.Encoding]::Unicode.GetString($bytes)
        } catch {}
    }

    $ramMb = [math]::Round($p.WorkingSetSize / 1MB, 1)
    $createdStr = if ($p.CreationDate) { $p.CreationDate.ToString("HH:mm:ss") } else { "Activo" }

    $result += @{
        pid         = [int]$p.ProcessId
        parentPid   = [int]$p.ParentProcessId
        name        = [string]$p.Name
        path        = if ($p.ExecutablePath) { [string]$p.ExecutablePath } else { "Sistema" }
        commandLine = $cmdLine
        decodedCmd  = $decodedHint
        memoryMb    = $ramMb
        startedAt   = $createdStr
    }
}

ConvertTo-Json -InputObject @($result) -Depth 4 -Compress
