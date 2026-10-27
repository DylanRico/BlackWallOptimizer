param([string]$PackagePath = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\releases\' + (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'catalogs\version.json') -Raw -Encoding UTF8 | ConvertFrom-Json).appVersion)))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $root 'artifacts\NativeOtaCheck.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe "/out:$exe" '/r:System.Web.Extensions.dll' ('/resource:' + (Join-Path $root 'assets\update-public-key.xml') + ',update-public-key.xml') (Join-Path $root 'native-wpf\OtaUpdate.cs') (Join-Path $root 'tests\NativeOtaCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'OTA checks failed to compile.' }
$fixture = Join-Path $root ('artifacts\ota-checks\' + [Guid]::NewGuid().ToString('N'))
& $exe ([IO.Path]::GetFullPath($PackagePath)) $root $fixture
if ($LASTEXITCODE -ne 0) { throw 'OTA checks failed.' }
