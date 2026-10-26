$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $project ('artifacts\build checks ' + [Guid]::NewGuid().ToString('N'))
foreach ($folder in @('scripts', 'native-wpf', 'catalogs', 'assets\icons', 'src-tauri\icons')) {
    New-Item -ItemType Directory -Path (Join-Path $fixture $folder) -Force | Out-Null
}
foreach ($relative in @('Build-Portable-Exe.bat', 'scripts\Build-Native.ps1', 'scripts\Build-Preview.ps1', 'native-wpf\BlackwallApp.cs', 'native-wpf\OtaUpdate.cs', 'assets\update-public-key.xml', 'src-tauri\icons\icon.ico')) {
    Copy-Item -LiteralPath (Join-Path $project $relative) -Destination (Join-Path $fixture $relative)
}
Copy-Item -Path (Join-Path $project 'catalogs\*.json') -Destination (Join-Path $fixture 'catalogs')
Copy-Item -Path (Join-Path $project 'assets\icons\*.png') -Destination (Join-Path $fixture 'assets\icons')
Copy-Item -LiteralPath (Join-Path $project 'assets\icons\blackwall-mark.png') -Destination (Join-Path $fixture 'assets\icons\added-after-build-script.png')
$checks = 0
function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
$build = Join-Path $fixture 'Build-Portable-Exe.bat'
$exe = Join-Path $fixture 'Blackwall.exe'
& $build | Out-Null
Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $exe)) 'Portable BAT failed with a path containing spaces.'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
Assert ($assembly.EntryPoint.DeclaringType.FullName -eq 'Blackwall.Program') 'Wrong application entrypoint.'
Assert ($null -ne $assembly.GetType('Blackwall.MaterialCard')) 'The current visual design was not compiled.'
Assert ($assembly.GetManifestResourceNames() -contains 'added-after-build-script.png') 'New local icons must be embedded automatically.'
$beforeHash = (Get-FileHash -LiteralPath $exe).Hash
$publicFile = Join-Path $fixture 'assets\update-public-key.xml'
$publicOriginal = [IO.File]::ReadAllText($publicFile)
$testKey = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$testKey.PersistKeyInCsp = $false
try {
    [IO.File]::WriteAllText($publicFile, $testKey.ToXmlString($true))
    try { $ErrorActionPreference = 'Continue'; & $build 2>&1 | Out-Null }
    finally { $ErrorActionPreference = 'Stop' }
    Assert ($LASTEXITCODE -ne 0 -and (Get-FileHash -LiteralPath $exe).Hash -eq $beforeHash) 'A private key must never be embedded by the compiler.'
} finally { [IO.File]::WriteAllText($publicFile, $publicOriginal); $testKey.Dispose() }
$source = Join-Path $fixture 'native-wpf\BlackwallApp.cs'
Add-Content -LiteralPath $source -Value 'invalid C# compilation fixture'
try { $ErrorActionPreference = 'Continue'; & $build 2>&1 | Out-Null }
finally { $ErrorActionPreference = 'Stop' }
Assert ($LASTEXITCODE -ne 0 -and (Get-FileHash -LiteralPath $exe).Hash -eq $beforeHash) 'A compiler failure replaced the last usable executable.'
Copy-Item -LiteralPath (Join-Path $project 'native-wpf\BlackwallApp.cs') -Destination $source -Force
$lock = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    try { $ErrorActionPreference = 'Continue'; & $build 2>&1 | Out-Null }
    finally { $ErrorActionPreference = 'Stop' }
    Assert ($LASTEXITCODE -ne 0 -and (Get-FileHash -LiteralPath $exe).Hash -eq $beforeHash) 'A locked target must remain unchanged.'
} finally { $lock.Dispose() }
& $build | Out-Null
Assert ($LASTEXITCODE -eq 0) 'Build did not recover after unlocking the target.'
$backup = Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts\build-backups') -Filter '*.bak'
Assert (@($backup).Count -eq 1 -and (Get-FileHash -LiteralPath $backup.FullName).Hash -eq $beforeHash) 'Replacement must retain an exact backup.'
$preview = Join-Path $fixture 'scripts\Build-Preview.ps1'
try { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -File $preview -OutputPath (Join-Path $fixture 'outside-artifacts.exe') 2>&1 | Out-Null }
finally { $ErrorActionPreference = 'Stop' }
Assert ($LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $fixture 'outside-artifacts.exe'))) 'Preview escaped its output boundary.'
$publisher = Get-Content -LiteralPath (Join-Path $project 'Publish-Release.bat') -Raw
Assert ($publisher -match 'Prepare-Release.ps1' -and $publisher -notmatch 'ICON_RES|%CSC%|New-SelfSignedCertificate') 'Publisher must delegate to the signed package builder.'
$launcher = Get-Content -LiteralPath (Join-Path $project 'Start-Blackwall.bat') -Raw
Assert ($launcher.IndexOf('if exist "%~dp0Blackwall.exe"') -lt $launcher.IndexOf('if exist "src-tauri\target\release\blackwall.exe"')) 'Launcher must prefer the current WPF portable.'
Write-Output "PASS: $checks native build checks (isolated fixture only)."
