param([Parameter(Mandatory=$true)][string]$BeforeExe, [Parameter(Mandatory=$true)][string]$AfterExe, [ValidateRange(1,20)][int]$Trials = 7)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $root 'artifacts\NativeBackdropBenchmark.exe'
$references = @('WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') '/nologo' '/target:exe' "/out:$output" @references (Join-Path $root 'tests\NativeBackdropBenchmark.cs')
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el benchmark del fondo del aviso.' }
$measurements = @()
for ($trial = 1; $trial -le $Trials; $trial++) {
    $entries = @(@{label='before';path=$BeforeExe}, @{label='after';path=$AfterExe})
    if ($trial % 2 -eq 0) { [array]::Reverse($entries) }
    foreach ($entry in $entries) {
        $m = (& $output (Resolve-Path -LiteralPath $entry.path).Path) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Falló el benchmark del fondo del aviso.' }
        $measurements += [pscustomobject]@{ version = $entry.label; trial = $trial; backdrop = $m.backdrop; tier = $m.renderTier
            plainCpuMs = $m.plain.cpuMs; dimmedCpuMs = $m.dimmed.cpuMs; plainP95Ms = $m.plain.renderP95Ms; dimmedP95Ms = $m.dimmed.renderP95Ms
            plainFrames = $m.plain.frames; dimmedFrames = $m.dimmed.frames }
        Write-Output ("Fondo medido: " + $entry.label + ", par " + $trial + "/" + $Trials)
    }
}
$measurements | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'artifacts\backdrop-benchmark.json')
$measurements | Format-Table version,trial,backdrop,tier,plainCpuMs,dimmedCpuMs,plainP95Ms,dimmedP95Ms,plainFrames,dimmedFrames
Write-Output 'Host WPF fuera de pantalla, 1,5 s por fase. La CPU incluye el hilo de render de WPF; no mide tiempo de GPU ni FPS visibles.'
