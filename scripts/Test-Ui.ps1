param([switch]$ScanHardware)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputFolder = Join-Path $projectRoot 'artifacts'
$frameworkFolder = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkFolder 'csc.exe'
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null

$references = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Management.dll') |
    ForEach-Object { '/r:' + (Join-Path $frameworkFolder $_) }
$previewExe = Join-Path $outputFolder 'Blackwall.preview.exe'
$compileArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$previewExe", ('/win32icon:' + (Join-Path $projectRoot 'src-tauri\icons\icon.ico'))) + $references
foreach ($catalog in @('tweaks', 'apps', 'version')) {
    $compileArgs += '/resource:' + (Join-Path $projectRoot "catalogs\$catalog.json") + ",$catalog.json"
}
foreach ($icon in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets\icons') -Filter '*.png') {
    $compileArgs += '/resource:' + $icon.FullName + ',' + $icon.Name
}
$compileArgs += Join-Path $projectRoot 'native-wpf\BlackwallApp.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de Blackwall.' }

$checkExe = Join-Path $outputFolder 'NativeUiSelfCheck.exe'
$checkArgs = @('/nologo', '/target:exe', "/out:$checkExe") + $references + (Join-Path $projectRoot 'tests\NativeUiSelfCheck.cs')
& $compiler @checkArgs
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de las pruebas WPF.' }
$runArgs = @($previewExe, (Join-Path $outputFolder 'native'))
if ($ScanHardware) { $runArgs += '--scan' }
& $checkExe @runArgs
if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas WPF.' }
& node --test (Join-Path $projectRoot 'tests\web-ui.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas web.' }
Write-Output "Verificado: $previewExe"
