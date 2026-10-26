param([Parameter(Mandatory=$true)][string]$OutputPath, [string]$Version)

$ErrorActionPreference = 'Stop'
function Get-BuildHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$trustKey = New-Object Security.Cryptography.RSACryptoServiceProvider
$trustKey.PersistKeyInCsp = $false
try {
    $trustKey.FromXmlString([IO.File]::ReadAllText((Join-Path $projectRoot 'assets\update-public-key.xml')))
    if (-not $trustKey.PublicOnly -or $trustKey.KeySize -lt 3072) { throw 'El recurso OTA debe contener solo una clave publica RSA de al menos 3072 bits.' }
} finally { $trustKey.Dispose() }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'assets\icons\blackwall-mark.png'))) {
    throw 'Falta assets\icons\blackwall-mark.png. Se conserva el ejecutable anterior.'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ne '.exe') { throw 'La salida debe ser un archivo .exe.' }
$buildFolder = Join-Path $projectRoot ('artifacts\native-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildFolder -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$stagedExe = Join-Path $buildFolder ([IO.Path]::GetFileName($OutputPath))
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'catalogs\version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Version) { $metadata.appVersion = $Version }
if ($metadata.appVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version de compilacion no valida.' }
$parsedVersion = [Version]$metadata.appVersion
if ($parsedVersion.Major -gt 65534 -or $parsedVersion.Minor -gt 65534 -or $parsedVersion.Build -gt 65534) { throw 'Version fuera de rango.' }
$versionResource = Join-Path $buildFolder 'version.json'
[IO.File]::WriteAllText($versionResource, ($metadata | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
$versionSource = Join-Path $buildFolder 'BuildVersion.cs'
[IO.File]::WriteAllText($versionSource, ('[assembly: System.Reflection.AssemblyVersion("' + $metadata.appVersion + '.0")][assembly: System.Reflection.AssemblyFileVersion("' + $metadata.appVersion + '.0")]'))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Management.dll') |
    ForEach-Object { '/r:' + (Join-Path $framework $_) }
$compileArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$stagedExe", ('/win32icon:' + (Join-Path $projectRoot 'src-tauri\icons\icon.ico'))) + $references
$resources = @()
foreach ($catalog in @('tweaks', 'apps', 'version')) {
    $catalogPath = Join-Path $projectRoot "catalogs\$catalog.json"
    if ($catalog -eq 'version') { $catalogPath = $versionResource }
    $resources += @{ path = $catalogPath; name = "$catalog.json" }
}
$resources += @{ path = Join-Path $projectRoot 'assets\update-public-key.xml'; name = 'update-public-key.xml' }
foreach ($icon in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets\icons') -Filter '*.png' | Sort-Object Name) {
    $resources += @{ path = $icon.FullName; name = $icon.Name }
}
foreach ($resource in $resources) { $compileArgs += '/resource:' + $resource.path + ',' + $resource.name }
$compileArgs += Join-Path $projectRoot 'native-wpf\BlackwallApp.cs'
$compileArgs += Join-Path $projectRoot 'native-wpf\OtaUpdate.cs'
$compileArgs += $versionSource
& (Join-Path $framework 'csc.exe') @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion. Se conserva el ejecutable anterior.' }

# Read bytes to validate the embedded assets without locking the output or starting the application.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($stagedExe))
foreach ($resource in $resources) {
    $stream = $assembly.GetManifestResourceStream($resource.name)
    if (-not $stream) { throw ('Falta el recurso embebido: ' + $resource.name) }
    try { $embeddedHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose() }
    if ($embeddedHash -ne (Get-BuildHash $resource.path)) {
        throw ('El recurso compilado no coincide con el archivo local: ' + $resource.name)
    }
}
if (Test-Path -LiteralPath $OutputPath) {
    $backupFolder = Join-Path $projectRoot 'artifacts\build-backups'
    New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
    $backup = Join-Path $backupFolder (([IO.Path]::GetFileName($OutputPath)) + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    try { [IO.File]::Replace($stagedExe, $OutputPath, $backup) }
    catch { throw ('No se pudo reemplazar el ejecutable. Cierra la aplicacion y vuelve a compilar. ' + $_.Exception.Message) }
    Write-Output "Respaldo anterior: $backup"
} else { [IO.File]::Move($stagedExe, $OutputPath) }
[IO.File]::Delete($versionSource)
[IO.File]::Delete($versionResource)
[IO.Directory]::Delete($buildFolder)
Write-Output "Compilado desde el codigo y recursos locales: $OutputPath"
Write-Output ('SHA256: ' + (Get-BuildHash $OutputPath))
