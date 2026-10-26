param([string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BlackwallPublisher\update-signing.dpapi'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$publicPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\update-public-key.xml'
if ((Test-Path -LiteralPath $PrivateKeyPath) -or (Test-Path -LiteralPath $publicPath)) {
    if (-not (Test-Path -LiteralPath $PrivateKeyPath) -or -not (Test-Path -LiteralPath $publicPath)) { throw 'Falta una de las claves existentes. No se reemplazara la identidad OTA.' }
    Write-Output 'Identidad OTA existente conservada.'
    exit 0
}
$folder = Split-Path -Parent ([IO.Path]::GetFullPath($PrivateKeyPath))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($sid)
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -LiteralPath $folder -AclObject $acl
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
try {
    $private = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    $protected = [Security.Cryptography.ProtectedData]::Protect($private, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($PrivateKeyPath, $protected)
    [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false), (New-Object Text.UTF8Encoding($false)))
    [Array]::Clear($private, 0, $private.Length)
} finally { $rsa.Dispose() }
Write-Output 'Clave publica creada; clave privada protegida con DPAPI fuera del proyecto.'
