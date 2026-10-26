param(
    [Parameter(Mandatory=$true)][string]$Path,
    [switch]$Restore,
    [Security.SecureString]$Password,
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BlackwallPublisher\update-signing.dpapi')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$Path = [IO.Path]::GetFullPath($Path)
if ([IO.Path]::GetExtension($Path) -ne '.pfx') { throw 'El respaldo debe usar extension .pfx.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)) + [IO.Path]::DirectorySeparatorChar
if (-not $Restore -and $Path.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase) -and -not $Path.StartsWith((Join-Path $projectRoot 'artifacts\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Guarda la clave privada fuera de los archivos del proyecto.' }
if (-not $Password) { $Password = Read-Host 'Contrasena larga para el respaldo PFX (minimo 16 caracteres)' -AsSecureString }
if ($Password.Length -lt 16) { throw 'Usa una contrasena de al menos 16 caracteres.' }
$publicPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\update-public-key.xml'
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$certificate = $null
try {
    if ($Restore) {
        if (Test-Path -LiteralPath $PrivateKeyPath) { throw 'La clave privada actual existe. No se sobrescribira.' }
        $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
        $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2([IO.Path]::GetFullPath($Path), $Password, $flags)
        $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if (-not $key) { throw 'El respaldo no contiene una clave privada RSA.' }
        try {
            $rsa.ImportParameters($key.ExportParameters($false))
            if ($rsa.ToXmlString($false) -cne [IO.File]::ReadAllText($publicPath)) { throw 'El respaldo no corresponde a la clave publica del proyecto.' }
            if ($key -is [Security.Cryptography.RSACng]) {
                if (-not $key.Key.IsEphemeral) { throw 'Se esperaba una clave temporal en memoria.' }
                # Only this imported in-memory copy is made exportable for re-protection with DPAPI.
                $policy = [int]([Security.Cryptography.CngExportPolicies]::AllowExport -bor [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport)
                $key.Key.SetProperty((New-Object Security.Cryptography.CngProperty('Export Policy', [BitConverter]::GetBytes($policy), [Security.Cryptography.CngPropertyOptions]::None)))
            }
            $rsa.ImportParameters($key.ExportParameters($true))
        } finally { $key.Dispose() }
        if ($rsa.ToXmlString($false) -cne [IO.File]::ReadAllText($publicPath)) { throw 'El respaldo no corresponde a la clave publica del proyecto.' }
        $folder = Split-Path -Parent ([IO.Path]::GetFullPath($PrivateKeyPath))
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false); $acl.SetOwner($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -LiteralPath $folder -AclObject $acl
        $bytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        try { [IO.File]::WriteAllBytes($PrivateKeyPath, [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)) }
        finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        Write-Output 'Clave restaurada y protegida para el usuario de Windows actual.'
    } else {
        if (Test-Path -LiteralPath $Path) { throw 'El archivo de respaldo ya existe; no se sobrescribira.' }
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PrivateKeyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($bytes)) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        if ($rsa.ToXmlString($false) -cne [IO.File]::ReadAllText($publicPath)) { throw 'La clave privada no corresponde al proyecto.' }
        $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=Blackwall OTA Backup', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddYears(10))
        $temporary = Join-Path (Split-Path -Parent $PrivateKeyPath) ([Guid]::NewGuid().ToString('N') + '.pfx')
        $random = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($random) } finally { $rng.Dispose() }
        $temporaryPassword = ConvertTo-SecureString ([Convert]::ToBase64String($random)) -AsPlainText -Force
        [Array]::Clear($random, 0, $random.Length)
        try {
            [IO.File]::WriteAllBytes($temporary, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $temporaryPassword))
            $pfx = Get-PfxData -FilePath $temporary -Password $temporaryPassword
            Export-PfxCertificate -PfxData $pfx -Password $Password -FilePath $Path -CryptoAlgorithmOption AES256_SHA256 | Out-Null
        } finally { $temporaryPassword.Dispose(); if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
        Write-Output 'Respaldo PFX cifrado creado. Guardalo fuera del proyecto y conserva la contrasena por separado.'
    }
} finally { if ($certificate) { $certificate.Dispose() }; $rsa.Dispose() }
