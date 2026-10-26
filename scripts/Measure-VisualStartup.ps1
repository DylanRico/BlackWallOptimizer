param([Parameter(Mandatory=$true)][string]$BeforeExe, [Parameter(Mandatory=$true)][string]$AfterExe, [ValidateRange(1,20)][int]$Trials = 7)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $root 'artifacts\NativeVisualBenchmark.exe'
$references = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') '/nologo' '/target:exe' "/out:$output" @references (Join-Path $root 'tests\NativeVisualBenchmark.cs')
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el benchmark.' }
$measurements = @()
for ($trial = 1; $trial -le $Trials; $trial++) {
    $entries = @(@{label='before'; path=$BeforeExe}, @{label='after'; path=$AfterExe})
    if ($trial % 2 -eq 0) { [array]::Reverse($entries) }
    foreach ($entry in $entries) {
        $measurement = (& $output (Resolve-Path -LiteralPath $entry.path).Path) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Falló el benchmark.' }
        $measurement | Add-Member -NotePropertyName version -NotePropertyValue $entry.label
        $measurement | Add-Member -NotePropertyName trial -NotePropertyValue $trial
        $measurement | Add-Member -NotePropertyName exeBytes -NotePropertyValue (Get-Item -LiteralPath $entry.path).Length
        $measurements += $measurement
        Write-Output ("Medido: " + $entry.label + ", par " + $trial + "/" + $Trials)
    }
}
$measurements | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'artifacts\visual-startup-benchmark.json')
$measurements | Format-Table version,trial,elapsedMs,navigationMs,searchMs,searchP95Ms,searchPageReplacements,scrollMs,idleCpuMs,privateBytes
Write-Output 'Solo construcción y render fuera de pantalla: no mide arranque completo, GPU ni FPS de una ventana visible.'
