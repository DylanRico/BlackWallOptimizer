param(
    [string]$Version = $env:BLACKWALL_RELEASE_VERSION,
    [string]$BaseUrl = 'https://github.com/DylanRico/BlackWallOptimizer/releases/download',
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BlackwallPublisher\update-signing.dpapi'),
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
function Test-ReleaseGates([string]$ProjectRoot, [bool]$AllowChanges) {
    & (Join-Path $ProjectRoot 'scripts/Test-Ui.ps1')
    if (-not $?) { throw 'Fallaron las pruebas UI.' }
    & (Join-Path $ProjectRoot 'scripts/Test-Ota.ps1')
    if (-not $?) { throw 'Fallaron las pruebas OTA.' }
    & node (Join-Path $ProjectRoot 'scripts/check-publish-safety.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Fallo la comprobacion de seguridad de publicacion.' }
    $changes = @(git -C $ProjectRoot status --porcelain --untracked-files=no -- native-wpf/ catalogs/ scripts/ tests/ assets/)
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo comprobar el estado de Git.' }
    if ($changes.Count -gt 0 -and -not $AllowChanges) { throw 'Hay cambios en archivos versionados; no se firmara el paquete.' }
}

Add-Type -AssemblyName System.Security
$root = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version no valida.' }
$uri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'BaseUrl debe ser HTTPS sin credenciales, consulta ni fragmento.' }
$release = Join-Path $root "artifacts\releases\$Version"
if (Test-Path -LiteralPath $release) { throw 'Esa version ya esta preparada. Conserva las versiones publicadas; usa otra version.' }
# The signed notes come from version.json, so they must describe the version being signed.
$metadata = Get-Content -LiteralPath (Join-Path $root 'catalogs\version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($metadata.appVersion -ne $Version) { throw "catalogs\version.json declara $($metadata.appVersion). Actualiza appVersion y changelog a $Version antes de preparar el paquete." }
if (-not @($metadata.changelog).Count) { throw 'catalogs\version.json no contiene notas (changelog) para firmar.' }
if (-not $AllowDirty -and -not (Test-Path -LiteralPath $PrivateKeyPath)) { throw 'Primero ejecuta scripts\Initialize-UpdateKey.ps1 en el equipo publicador.' }

# Build and validate before the private key is decrypted: detectable build failures never touch it.
$staging = Join-Path $root ('artifacts\release-staging\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
try {
    $exe = Join-Path $staging 'Blackwall.exe'
    & (Join-Path $PSScriptRoot 'Build-Native.ps1') -OutputPath $exe -Version $Version
    Test-ReleaseGates $root ([bool]$AllowDirty)
    if ($AllowDirty) { Write-Output 'Ensayo local completado; no se ha firmado ningun paquete.'; return }
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
    $protocol = $assembly.GetType('Blackwall.OtaProtocol', $true)
    $binaryHash = $protocol.GetMethod('HashFile').Invoke($null, @($exe.PSObject.BaseObject))

    $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PrivateKeyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($private))
    [Array]::Clear($private, 0, $private.Length)
    if ($rsa.KeySize -lt 3072 -or $rsa.ToXmlString($false) -cne [IO.File]::ReadAllText((Join-Path $root 'assets\update-public-key.xml'))) { throw 'La clave privada no corresponde a la identidad OTA del proyecto.' }
    $payload = [ordered]@{
        schemaVersion = 1; appVersion = $Version
        binary = [ordered]@{ url = $BaseUrl.TrimEnd('/') + '/v' + $Version + '/Blackwall.exe'; sha256 = $binaryHash; size = (Get-Item -LiteralPath $exe).Length }
        changelog = @($metadata.changelog)
    } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $signature = $rsa.SignData($bytes, 'SHA256')
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($bytes); signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $staging 'update.json'), $envelope, (New-Object Text.UTF8Encoding($false)))
    $verified = $protocol.GetMethod('Verify').Invoke($null, @([IO.File]::ReadAllBytes((Join-Path $staging 'update.json')), $rsa.ToXmlString($false)))
    $protocol.GetMethod('VerifyBinary').Invoke($null, @($exe.PSObject.BaseObject, $verified.PSObject.BaseObject))
    New-Item -ItemType Directory -Path (Split-Path -Parent $release) -Force | Out-Null
    [IO.Directory]::Move($staging, $release)
    Write-Output "Paquete preparado y verificado: $release"
    Write-Output 'No se ha publicado ni modificado el portable raiz. Publica el binario inmutable antes del manifiesto firmado.'
} finally {
    $rsa.Dispose()
    # A failed preparation leaves no half-signed package behind.
    if (Test-Path -LiteralPath $staging) { [IO.Directory]::Delete($staging, $true) }
}
