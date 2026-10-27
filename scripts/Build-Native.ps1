param([Parameter(Mandatory=$true)][string]$OutputPath, [string]$Version)

$ErrorActionPreference = 'Stop'
function Get-StreamHash([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha.Dispose(); $Stream.Dispose() }
}
function Get-BuildHash([string]$Path) { Get-StreamHash ([IO.File]::OpenRead($Path)) }
$started = [Diagnostics.Stopwatch]::StartNew()
$projectRoot = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$referenceNames = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Management.dll')

# Report every missing input before touching the output, so a broken checkout never looks like a compiler error.
$required = @(
    @{ path = Join-Path $framework 'csc.exe'; hint = 'Activa .NET Framework 4.8 en Caracteristicas de Windows.' },
    @{ path = Join-Path $projectRoot 'native-wpf\BlackwallApp.cs'; hint = 'Restaura el codigo fuente WPF.' },
    @{ path = Join-Path $projectRoot 'native-wpf\OtaUpdate.cs'; hint = 'Restaura el codigo fuente WPF.' },
    @{ path = Join-Path $projectRoot 'src-tauri\icons\icon.ico'; hint = 'Regenera el icono con scripts\New-AppIcon.ps1.' },
    @{ path = Join-Path $projectRoot 'assets\icons\blackwall-mark.png'; hint = 'Regenera el icono con scripts\New-AppIcon.ps1.' },
    @{ path = Join-Path $projectRoot 'assets\update-public-key.xml'; hint = 'Restaura la clave publica OTA del repositorio.' }
) + ($referenceNames | ForEach-Object { @{ path = Join-Path $framework $_; hint = 'Activa .NET Framework 4.8 y WPF en Caracteristicas de Windows.' } }) +
    (@('tweaks', 'apps', 'version') | ForEach-Object { @{ path = Join-Path $projectRoot "catalogs\$_.json"; hint = 'Restaura los catalogos del repositorio.' } })
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_.path) } | ForEach-Object { '  - ' + $_.path + ' -> ' + $_.hint })
if ($missing.Count) { throw ("Faltan archivos necesarios. Se conserva el ejecutable anterior.`n" + ($missing -join "`n")) }
foreach ($catalog in @('tweaks', 'apps')) {
    try { $null = Get-Content -LiteralPath (Join-Path $projectRoot "catalogs\$catalog.json") -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw ("catalogs\$catalog.json no es JSON valido. Se conserva el ejecutable anterior. " + $_.Exception.Message) }
}
$trustKey = New-Object Security.Cryptography.RSACryptoServiceProvider
$trustKey.PersistKeyInCsp = $false
try {
    $trustKey.FromXmlString([IO.File]::ReadAllText((Join-Path $projectRoot 'assets\update-public-key.xml')))
    if (-not $trustKey.PublicOnly -or $trustKey.KeySize -lt 3072) { throw 'El recurso OTA debe contener solo una clave publica RSA de al menos 3072 bits.' }
} finally { $trustKey.Dispose() }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ne '.exe') { throw 'La salida debe ser un archivo .exe.' }
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'catalogs\version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Version) { $metadata.appVersion = $Version }
if ($metadata.appVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version de compilacion no valida.' }
$parsedVersion = [Version]$metadata.appVersion
if ($parsedVersion.Major -gt 65534 -or $parsedVersion.Minor -gt 65534 -or $parsedVersion.Build -gt 65534) { throw 'Version fuera de rango.' }
$peVersion = $metadata.appVersion + '.0'

$buildFolder = Join-Path $projectRoot ('artifacts\native-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildFolder -Force | Out-Null
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    $stagedExe = Join-Path $buildFolder ([IO.Path]::GetFileName($OutputPath))
    $versionResource = Join-Path $buildFolder 'version.json'
    [IO.File]::WriteAllText($versionResource, ($metadata | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $versionSource = Join-Path $buildFolder 'BuildVersion.cs'
    [IO.File]::WriteAllText($versionSource, ('[assembly: System.Reflection.AssemblyVersion("' + $peVersion + '")][assembly: System.Reflection.AssemblyFileVersion("' + $peVersion + '")]'))
    $references = $referenceNames | ForEach-Object { '/r:' + (Join-Path $framework $_) }
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
        if ((Get-StreamHash $stream) -ne (Get-BuildHash $resource.path)) {
            throw ('El recurso compilado no coincide con el archivo local: ' + $resource.name)
        }
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedExe).FileVersion
    if ($fileVersion -ne $peVersion -or $assembly.GetName().Version.ToString() -ne $peVersion) {
        throw ("La version PE ($fileVersion) no coincide con la solicitada ($peVersion). Se conserva el ejecutable anterior.")
    }

    $backup = $null
    if (Test-Path -LiteralPath $OutputPath) {
        $backupFolder = Join-Path $projectRoot 'artifacts\build-backups'
        New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
        $backup = Join-Path $backupFolder (([IO.Path]::GetFileName($OutputPath)) + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
        try { [IO.File]::Replace($stagedExe, $OutputPath, $backup) }
        catch { throw ('No se pudo reemplazar el ejecutable. Cierra la aplicacion y vuelve a compilar. ' + $_.Exception.Message) }
    } else { [IO.File]::Move($stagedExe, $OutputPath) }
} finally {
    # Only this build's own GUID folder is removed; other builds and artifacts are never touched.
    if (Test-Path -LiteralPath $buildFolder) { [IO.Directory]::Delete($buildFolder, $true) }
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$kind = if ($OutputPath -like ($artifactRoot + 'release*')) { 'paquete de version (sin publicar)' }
    elseif ($OutputPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { 'preview de revision' }
    else { 'portable' }
Write-Output "Compilado desde el codigo y recursos locales: $OutputPath"
Write-Output "Tipo: $kind | Version: $peVersion | Tamano: $((Get-Item -LiteralPath $OutputPath).Length) bytes | Duracion: $([Math]::Round($started.Elapsed.TotalSeconds, 1)) s"
Write-Output ('SHA256: ' + (Get-BuildHash $OutputPath))
if ($backup) { Write-Output "Respaldo anterior: $backup" }
