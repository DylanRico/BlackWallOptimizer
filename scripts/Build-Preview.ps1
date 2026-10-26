param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputPath) { $OutputPath = Join-Path $artifactRoot 'Blackwall.preview.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $OutputPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($OutputPath) -ne '.exe') {
    throw 'La compilación de revisión solo escribe ejecutables dentro de artifacts.'
}
& (Join-Path $PSScriptRoot "Build-Native.ps1") -OutputPath $OutputPath
