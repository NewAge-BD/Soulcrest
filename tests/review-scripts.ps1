# Run with Windows PowerShell 5.1. All external operations are mocked.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$launcher = Join-Path $repo 'scripts\tester\Update-And-Start.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher parse failure' }
if (@([IO.File]::ReadAllBytes($launcher) | Where-Object { $_ -gt 127 }).Count) { throw 'Launcher must be ASCII' }
foreach ($name in @('Test-Tester', 'Quote')) {
    $node = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$Logs = Join-Path $env:TEMP 'soulcrest-script-test'
$original = $env:SOULCREST_DATA
try {
    $env:SOULCREST_DATA = 'sentinel-user-profile'
    $script:seen = @()
    function Test-TesterIsolated($Path, $SmokeData) {
        if ($env:SOULCREST_DATA -ne $SmokeData -or $SmokeData -eq 'sentinel-user-profile') { throw 'Smoke profile not isolated' }
        $script:seen += $SmokeData
        return 'ok'
    }
    if ((Test-Tester 'unused.exe') -ne 'ok') { throw 'Smoke result lost' }
    if ((Test-Tester 'unused.exe') -ne 'ok') { throw 'Smoke result lost' }
    if ($seen[0] -eq $seen[1]) { throw 'Smoke directory reused' }
    if ($env:SOULCREST_DATA -ne 'sentinel-user-profile') { throw 'Environment not restored' }
    function Test-TesterIsolated($Path, $SmokeData) { throw 'simulated failure' }
    try { Test-Tester 'unused.exe' } catch { if ($_.Exception.Message -ne 'simulated failure') { throw } }
    if ($env:SOULCREST_DATA -ne 'sentinel-user-profile') { throw 'Environment not restored after failure' }
} finally { $env:SOULCREST_DATA = $original }

$assignment = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$arguments' }, $true)
$Source = 'G:\a custom repository\Pet Companion'
. ([scriptblock]::Create($assignment.Extent.Text))
$i = [Array]::IndexOf($arguments, '-Source')
if ($i -lt 0 -or $arguments[$i + 1] -ne (Quote $Source)) { throw 'Source was not forwarded' }

# Execute the recorder's real discovery prefix with relay-only fixtures; never reach dumpcap.
function Test-Path { return $true }
function Get-Process { [pscustomobject]@{ Id = 100 } }
function Get-NetUDPEndpoint { }
function Get-NetTCPConnection {
    param($OwningProcess, $LocalPort, $State, $ErrorAction)
    if ($LocalPort) { return [pscustomobject]@{ OwningProcess = 200 } }
    return [pscustomobject]@{ State = 'Established'; RemoteAddress = '127.0.0.1'; RemotePort = 5000; LocalPort = 1234 }
}
function Find-NetRoute { throw 'Relay-only traffic must not need an external route' }
$recorder = [IO.File]::ReadAllText((Join-Path $repo 'scripts\Record-GameTraffic.ps1'))
$prefix = $recorder.Substring(0, $recorder.IndexOf('New-Item -ItemType Directory'))
. ([scriptblock]::Create($prefix))
if ($device -ne '\Device\NPF_Loopback' -or $filter -ne 'tcp and (port 5000)') { throw 'Wrong relay capture target' }
Write-Output 'PASS: PS5.1 syntax/ASCII, isolated smoke success/failure, Source forwarding, relay-only discovery.'
