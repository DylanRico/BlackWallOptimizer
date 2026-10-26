param([Parameter(Mandatory=$true)][string]$BeforeExe, [Parameter(Mandatory=$true)][string]$AfterExe, [ValidateRange(1,20)][int]$Trials = 7)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $root 'artifacts\NativeScrollBenchmark.exe'
$references = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') '/nologo' '/target:exe' "/out:$output" @references (Join-Path $root 'tests\NativeScrollBenchmark.cs')
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el benchmark de entrada.' }
$measurements = @()
for ($trial = 1; $trial -le $Trials; $trial++) {
    $entries = @(@{label='before';path=$BeforeExe}, @{label='after';path=$AfterExe})
    if ($trial % 2 -eq 0) { [array]::Reverse($entries) }
    foreach ($entry in $entries) {
        $measurement = (& $output (Resolve-Path -LiteralPath $entry.path).Path) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Falló el benchmark de entrada.' }
        $measurement | Add-Member -NotePropertyName version -NotePropertyValue $entry.label
        $measurement | Add-Member -NotePropertyName trial -NotePropertyValue $trial
        $measurements += $measurement
        Write-Output ("Entrada medida: " + $entry.label + ", par " + $trial + "/" + $Trials)
    }
}
$measurements | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'artifacts\scroll-input-benchmark.json')
$measurements | Format-Table version,trial,events,frames,renderEventP95Ms,handlerP95Ms,offsetStepP95,cpuMs
Write-Output 'Entrada sintética en un host WPF aislado. No mide GPU, FPS visibles ni latencia física del touchpad.'
