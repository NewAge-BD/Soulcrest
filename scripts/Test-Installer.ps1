#Requires -Version 5.1
[CmdletBinding()]
param([string] $Version)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath "$repoRoot\Directory.Build.props" -Raw)).Project.PropertyGroup.Version }
$setup = Join-Path $repoRoot "artifacts\installer\Soulcrest-$Version-Setup-win-x64.exe"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2946C774-E30B-4C98-A366-108062322891}_is1'
if (Test-Path $uninstallKey) { throw 'Soulcrest is already installed; isolated installation test skipped to preserve it.' }
$testRoot = Join-Path $repoRoot ('artifacts\installer-validation\install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$installDir = Join-Path $testRoot 'app'
$dataDir = Join-Path $testRoot 'userdata'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
Set-Content -LiteralPath "$dataDir\preserve.txt" -Value 'User data must survive uninstall.'
function Invoke-Setup([string] $Path, [string[]] $Arguments) {
    $p = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(180000)) { throw "Timeout: $Path (PID $($p.Id))" }
    if ($p.ExitCode -ne 0) { throw "Failed with exit code $($p.ExitCode): $Path" }
}
$savedData = $env:SOULCREST_DATA
$savedMap = $env:SOULCREST_MAPDATA
$savedSource = $env:SOULCREST_SOURCE
$smokeResult = 'not run'
try {
    Invoke-Setup $setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$installDir+'"'),('/LOG="'+$testRoot+'\install.log"'))
    if (-not (Test-Path "$installDir\mapdata\manifest.json")) { throw 'Installed mapdata missing.' }
    $env:SOULCREST_DATA = $dataDir
    $env:SOULCREST_MAPDATA = $null
    $env:SOULCREST_SOURCE = $null
    $mutex = [Threading.Mutex]::new($false, ('Local\Soulcrest-' + [Environment]::UserName))
    $available = $false
    try {
        try { $available = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $available = $true }
        if ($available) { $mutex.ReleaseMutex() }
    } finally { $mutex.Dispose() }
    if ($available) {
        $p = Start-Process -FilePath "$installDir\Soulcrest.exe" -ArgumentList '--smoke-test' -WorkingDirectory $installDir -WindowStyle Hidden -PassThru
        if (-not $p.WaitForExit(90000)) { $p.Kill(); throw 'App smoke test timed out.' }
        if ($p.ExitCode -ne 0) { throw "App smoke test failed: $($p.ExitCode). See $dataDir." }
        $smokeResult = Get-Content -LiteralPath "$dataDir\smoke-test.txt" -Raw
    } else {
        $smokeResult = 'SKIPPED: an existing Soulcrest instance holds the user mutex; it was left running.'
    }
    Write-Host $smokeResult
    # Repeat the same package to exercise the upgrade/repair path.
    Invoke-Setup $setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$installDir+'"'),('/LOG="'+$testRoot+'\upgrade.log"'))
} finally {
    $env:SOULCREST_DATA = $savedData
    $env:SOULCREST_MAPDATA = $savedMap
    $env:SOULCREST_SOURCE = $savedSource
    if (Test-Path "$installDir\unins000.exe") {
        Invoke-Setup "$installDir\unins000.exe" @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$testRoot+'\uninstall.log"'))
    }
}
if (Test-Path "$installDir\Soulcrest.exe") { throw 'Uninstall left the application executable.' }
if (Test-Path $uninstallKey) { throw 'Uninstall left its registration.' }
if (-not (Test-Path "$dataDir\preserve.txt")) { throw 'User data was removed.' }
"PASS: installation, same-version upgrade, uninstall, user-data preservation. Smoke: $smokeResult Logs: $testRoot" |
    Tee-Object -FilePath "$testRoot\RESULT.txt"
