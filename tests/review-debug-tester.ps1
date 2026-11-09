# Windows PowerShell 5.1: fake processes and listeners; child scripts only write fixture output.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$starter = Join-Path $repo 'scripts\tester\Start-DebugTester.ps1'
$parseTokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($starter, [ref] $parseTokens, [ref] $parseErrors)
if ($parseErrors.Count) { throw 'Debug starter parse failure' }
foreach ($file in @($starter, (Join-Path $repo 'scripts\tester\Debug Tester.cmd'))) {
    if (@([IO.File]::ReadAllBytes($file) | Where-Object { $_ -gt 127 }).Count) { throw 'Debug launcher must be ASCII' }
}
foreach ($function in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$nativeUpdateFunction = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-DebugTesterUpdate' }, $true).Extent.Text
function Assert-True($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock] $Action, [string] $Message) {
    $caught = $false
    try { & $Action } catch { $caught = $true; if ($_.Exception.Message -notlike "*$Message*") { throw } }
    Assert-True $caught "Expected failure: $Message"
}
function Reset-Fixture {
    $script:processes = @(); $script:listeners = @(); $script:updateCalls = 0; $script:sleeps = 0
    $script:browser = [pscustomobject]@{ Name = 'msedgewebview2.exe'; ParentProcessId = 1000 }
    $script:targets = @([pscustomobject]@{ type = 'page'; title = 'Soulcrest'; url = 'https://0.0.0.1/' })
    $script:exitCode = 0; $script:updateThrows = $false; $script:createReady = $true; $script:inspectThrows = $false
    $env:SOULCREST_DATA = $null; $env:SOULCREST_WEBVIEW_DEBUG_PORT = '9555'
}
function Set-Ready {
    $script:processes = @([pscustomobject]@{ Id = 1000; Path = (Join-Path $env:LOCALAPPDATA 'SoulcrestTester\app\Soulcrest Tester.exe') })
    $script:listeners = @([pscustomobject]@{ LocalAddress = '127.0.0.1'; OwningProcess = 2000 })
}
function Get-DebugTesterProcesses { return $script:processes }
function Get-DebugPortListeners { if ($script:inspectThrows) { throw 'fixture inspection unavailable' }; return $script:listeners }
function Get-DebugBrowserProcess([int] $ProcessId) { Assert-True ($ProcessId -eq 2000) 'Wrong browser lookup'; return $script:browser }
function Get-DebugTargetList { return $script:targets }
function Start-Sleep { param($Milliseconds) $script:sleeps++ }
function Invoke-DebugTesterUpdate([string] $Launcher, [string] $Source, [bool] $Force) {
    $script:updateCalls++
    Assert-True ($env:SOULCREST_WEBVIEW_DEBUG_PORT -eq '9222') 'Port not set before child start'
    Assert-True ($Launcher -eq 'fixture-updater.ps1' -and $Source -eq $script:source -and $Force) 'Arguments lost'
    if ($script:updateThrows) { throw 'fixture update exception' }
    if ($script:createReady) { Set-Ready }
    return $script:exitCode
}
$script:source = 'G:\a custom repository & fixture\Pet Companion'
$savedData = $env:SOULCREST_DATA; $savedPort = $env:SOULCREST_WEBVIEW_DEBUG_PORT
try {
    Reset-Fixture
    Start-DebugTester 'fixture-updater.ps1' $script:source $true
    Assert-True ($script:updateCalls -eq 1 -and $env:SOULCREST_WEBVIEW_DEBUG_PORT -eq '9555') 'Start or environment restore failed'
    Reset-Fixture; $env:SOULCREST_WEBVIEW_DEBUG_PORT = $null
    Start-DebugTester 'fixture-updater.ps1' $script:source $true
    Assert-True (-not $env:SOULCREST_WEBVIEW_DEBUG_PORT) 'Absent port not restored'
    Reset-Fixture; $script:exitCode = 7
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'Code 7'
    Assert-True ($env:SOULCREST_WEBVIEW_DEBUG_PORT -eq '9555') 'Failure port restore failed'
    Reset-Fixture; $script:updateThrows = $true
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'fixture update exception'
    Assert-True ($env:SOULCREST_WEBVIEW_DEBUG_PORT -eq '9555') 'Exception port restore failed'
    Reset-Fixture; $script:processes = @([pscustomobject]@{ Id = 1000; Path = (Join-Path $env:LOCALAPPDATA 'SoulcrestTester\app\Soulcrest Tester.exe') })
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'Soulcrest laeuft bereits'
    Assert-True ($script:updateCalls -eq 0) 'Second instance started'
    Reset-Fixture; Set-Ready
    Start-DebugTester 'fixture-updater.ps1' $script:source $true
    Assert-True ($script:updateCalls -eq 0 -and $env:SOULCREST_WEBVIEW_DEBUG_PORT -eq '9555') 'Existing debugger relaunched or environment changed'
    Reset-Fixture; Set-Ready; $script:processes[0].Path = 'G:\other fixture\Soulcrest.exe'
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'Soulcrest laeuft bereits'
    Assert-True ($script:updateCalls -eq 0) 'Different installation accepted'
    Reset-Fixture; $script:listeners = @([pscustomobject]@{ LocalAddress = '127.0.0.1'; OwningProcess = 2000 })
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } '9222 ist bereits belegt'
    Assert-True ($script:updateCalls -eq 0) 'Port conflict started updater'
    Reset-Fixture; $script:inspectThrows = $true
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'inspection unavailable'
    Assert-True ($script:updateCalls -eq 0) 'Unknown listener inspection accepted'
    Reset-Fixture; Set-Ready; $script:browser.ParentProcessId = 999
    Assert-True (-not (Test-DebugTesterReady 1000)) 'Foreign browser accepted'
    Reset-Fixture; Set-Ready; $script:browser.Name = 'other.exe'
    Assert-True (-not (Test-DebugTesterReady 1000)) 'Non-WebView listener accepted'
    Reset-Fixture; Set-Ready; $script:listeners[0].LocalAddress = '0.0.0.0'
    Assert-True (-not (Test-DebugTesterReady 1000)) 'Non-loopback listener accepted'
    Reset-Fixture; Set-Ready; $script:targets[0].url = 'https://other.invalid/'
    Assert-True (-not (Test-DebugTesterReady 1000)) 'Foreign page accepted'
    Reset-Fixture; $script:createReady = $false
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true -ReadyTimeoutMilliseconds 1 } 'keinen eigenen Debug-Endpunkt'
    Assert-True ($script:updateCalls -eq 1 -and $script:sleeps -lt 10000) 'Readiness not bounded or updater restarted'
    Reset-Fixture; $env:SOULCREST_DATA = 'fixture-profile-override'
    Assert-Throws { Start-DebugTester 'fixture-updater.ps1' $script:source $true } 'SOULCREST_DATA ist gesetzt'
    Assert-True ($script:updateCalls -eq 0) 'Unexpected data override started'
} finally { $env:SOULCREST_DATA = $savedData; $env:SOULCREST_WEBVIEW_DEBUG_PORT = $savedPort }

# Exercise actual native PS5.1 argument/environment forwarding and the exact CMD shortcut arguments.
$fixtureRoot = Join-Path $repo ('output\debug-tester-validation\fixture ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$savedOutput = $env:SOULCREST_DEBUG_TEST_OUTPUT
try {
    $fixtureUpdater = Join-Path $fixtureRoot 'Start-DebugTester.ps1'
    $env:SOULCREST_DEBUG_TEST_OUTPUT = Join-Path $fixtureRoot 'native-seen.json'
    $env:SOULCREST_WEBVIEW_DEBUG_PORT = '9222'
    [IO.File]::WriteAllText($fixtureUpdater, @'
param([string] $Source, [switch] $Force)
[pscustomobject]@{ source = $Source; force = [bool] $Force; port = $env:SOULCREST_WEBVIEW_DEBUG_PORT } |
    ConvertTo-Json | Set-Content -LiteralPath $env:SOULCREST_DEBUG_TEST_OUTPUT
Write-Output 'fixture progress'
exit 0
'@, [Text.Encoding]::ASCII)
    . ([scriptblock]::Create($nativeUpdateFunction))
    $result = Invoke-DebugTesterUpdate $fixtureUpdater $script:source $true
    Assert-True ($result -is [int] -and $result -eq 0) 'Child progress polluted exit code'
    $seen = Get-Content -LiteralPath $env:SOULCREST_DEBUG_TEST_OUTPUT -Raw | ConvertFrom-Json
    Assert-True ($seen.source -eq $script:source -and $seen.force -and $seen.port -eq '9222') 'Native PS5.1 forwarding failed'

    # A GUI descendant can outlive its updater. Do not wait for inherited stdout handles.
    $descendantUpdater = Join-Path $fixtureRoot 'UpdaterWithDescendant.ps1'
    [IO.File]::WriteAllText($descendantUpdater, @'
param([string] $Source, [switch] $Force)
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$info.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 4"'
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
[void] [Diagnostics.Process]::Start($info)
exit 0
'@, [Text.Encoding]::ASCII)
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-DebugTesterUpdate $descendantUpdater $script:source $false
    Assert-True ($result -eq 0 -and $elapsed.ElapsedMilliseconds -lt 3000) 'Updater waited for descendant output handles'
    Copy-Item -LiteralPath (Join-Path $repo 'scripts\tester\Debug Tester.cmd') -Destination $fixtureRoot
    $installerTokens = $null; $installerErrors = $null
    $installerAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts\Install-Tester.ps1'), [ref] $installerTokens, [ref] $installerErrors)
    Assert-True ($installerErrors.Count -eq 0) 'Installer parse failure'
    $shortcutArguments = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$debugShortcut.Arguments' }, $true)
    $debugShortcut = [pscustomobject]@{ Arguments = '' }; $testerRoot = $fixtureRoot; $root = $script:source
    . ([scriptblock]::Create($shortcutArguments.Extent.Text))
    $env:SOULCREST_DEBUG_TEST_OUTPUT = Join-Path $fixtureRoot 'shortcut-seen.json'
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $env:ComSpec; $info.Arguments = $debugShortcut.Arguments
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $child = [Diagnostics.Process]::Start($info)
    if (-not $child.WaitForExit(10000)) { throw 'Fixture CMD did not finish' }
    $text = $child.StandardOutput.ReadToEnd() + $child.StandardError.ReadToEnd()
    Assert-True ($child.ExitCode -eq 0) ('Fixture CMD failed: ' + $text)
    $seen = Get-Content -LiteralPath $env:SOULCREST_DEBUG_TEST_OUTPUT -Raw | ConvertFrom-Json
    Assert-True ($seen.source -eq $script:source -and $seen.port -eq '9222') 'Double-click shortcut quoting failed'
} finally { $env:SOULCREST_DEBUG_TEST_OUTPUT = $savedOutput; $env:SOULCREST_WEBVIEW_DEBUG_PORT = $savedPort }
Write-Output 'PASS: Debug launcher ownership, fixed port inheritance/restore, bounded readiness, native PS5.1 and CMD/shortcut quoting.'
