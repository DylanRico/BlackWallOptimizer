$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/Prepare-Release.ps1'), [ref]$tokens, [ref]$errors)
$gate = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-ReleaseGates' }, $true)
if (-not $gate) { throw 'Release gate function is missing.' }
$invocation = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Test-ReleaseGates' }, $true)
if (-not $invocation -or $invocation.Extent.StartOffset -gt $ast.Extent.Text.IndexOf('[Security.Cryptography.ProtectedData]::Unprotect')) { throw 'Release gate must run before private-key decryption.' }
. ([scriptblock]::Create($gate.Extent.Text))
$fixture = Join-Path $env:TEMP ('blackwall-release-gates-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $fixture 'scripts/Test-Ui.ps1'), 'if ($global:failGate -eq "ui") { throw "ui failed" }')
    [IO.File]::WriteAllText((Join-Path $fixture 'scripts/Test-Ota.ps1'), 'if ($global:failGate -eq "ota") { throw "ota failed" }')
    function git { $global:LASTEXITCODE = 0; if ($global:failGate -eq 'dirty') { ' M native-wpf/file.cs' } }
    function node { if ($global:failGate -eq 'safety') { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 } }
    foreach ($case in @('ui', 'ota', 'safety', 'dirty')) {
        $global:failGate = $case; $rejected = $false
        try { Test-ReleaseGates $fixture $false } catch { $rejected = $true }
        if (-not $rejected) { throw "Gate accepted $case failure." }
    }
    $global:failGate = 'dirty'; Test-ReleaseGates $fixture $true
    $global:failGate = ''; Test-ReleaseGates $fixture $false
    Write-Output 'PASS: release gates reject failing checks and tracked changes.'
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
