$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('artifacts\key-checks\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $fixture 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts\Backup-UpdateKey.ps1') -Destination (Join-Path $fixture 'scripts\Backup-UpdateKey.ps1')
$script = Join-Path $fixture 'scripts\Backup-UpdateKey.ps1'
$privatePath = Join-Path $fixture 'source.dpapi'
$restoredPath = Join-Path $fixture 'restored\key.dpapi'
$pfx = (Join-Path (Split-Path -Parent $fixture) ((Split-Path -Leaf $fixture) + '.pfx'))
$password = ConvertTo-SecureString 'fixture-only-password-2026' -AsPlainText -Force
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
$before = @(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint)
try {
    [IO.File]::WriteAllText((Join-Path $fixture 'assets\update-public-key.xml'), $rsa.ToXmlString($false))
    $bytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    [IO.File]::WriteAllBytes($privatePath, [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    [Array]::Clear($bytes, 0, $bytes.Length)
    & $script -Path $pfx -Password $password -PrivateKeyPath $privatePath | Out-Null
    if (-not (Test-Path -LiteralPath $pfx)) { throw 'No encrypted backup was written.' }
    & $script -Restore -Path $pfx -Password $password -PrivateKeyPath $restoredPath | Out-Null
    $restored = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($restoredPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $copy = New-Object Security.Cryptography.RSACryptoServiceProvider
    $copy.PersistKeyInCsp = $false
    try { $copy.FromXmlString([Text.Encoding]::UTF8.GetString($restored)); if ($copy.ToXmlString($false) -cne $rsa.ToXmlString($false)) { throw 'Restored identity does not match.' } }
    finally { $copy.Dispose(); [Array]::Clear($restored, 0, $restored.Length) }
    $rejected = $false
    try { & $script -Restore -Path $pfx -Password $password -PrivateKeyPath $restoredPath | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Existing identity was overwritten.' }
    $rejected = $false
    try { & $script -Restore -Path $pfx -Password (ConvertTo-SecureString 'wrong-fixture-password' -AsPlainText -Force) -PrivateKeyPath (Join-Path $fixture 'invalid\key.dpapi') | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $fixture 'invalid\key.dpapi'))) { throw 'Wrong-password recovery wrote a key.' }
    $after = @(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint)
    if (@(Compare-Object $before $after).Count -ne 0) { throw 'Certificate store changed.' }
    Write-Output 'PASS: 5 publisher-key backup/restore checks; test identity only, no certificate store changes.'
} finally { $rsa.Dispose(); $password.Dispose() }
