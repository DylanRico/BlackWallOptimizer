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
& (Join-Path $PSScriptRoot 'Build-Preview.ps1') -OutputPath $previewExe

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
& powershell.exe -NoProfile -File (Join-Path $projectRoot 'tests\Test-NativeBuild.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas del compilador nativo.' }
Write-Output "Verificado: $previewExe"
