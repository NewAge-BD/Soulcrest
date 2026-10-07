<#
.SYNOPSIS
  Records the network traffic of the running Aion 2 client (AION2.exe) into a pcapng file, for the
  offline search for loot/soul events (user request 2026-10-03, opt-in exception to docs/SAFETY.md).

.DESCRIPTION
  Passive only: dumpcap (Wireshark) via Npcap, capture filter on the remote addresses the game process
  has open connections to. Nothing is sent, nothing is decrypted. The file goes to
  %LOCALAPPDATA%\Soulcrest\captures\aion2-<time>.pcapng, next to a JSON file with the connections and
  the start time, so the loot events of Soulcrest (events.jsonl) can be lined up with the packets.

.EXAMPLE
  ./scripts/Record-GameTraffic.ps1 -Seconds 120
#>
param(
    [int]$Seconds = 120,
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'Soulcrest\captures')
)

$ErrorActionPreference = 'Stop'
$dumpcap = 'C:\Program Files\Wireshark\dumpcap.exe'
if (-not (Test-Path -LiteralPath $dumpcap)) { throw 'dumpcap.exe (Wireshark) not found.' }

# AION2.exe runs as two processes; the one with the game window holds the connections.
$games = @(Get-Process -Name AION2 -ErrorAction SilentlyContinue)
if ($games.Count -eq 0) { throw 'AION2.exe is not running.' }

# Remote endpoints of the game (TCP, without loopback). UDP has no remote address here; it is
# covered by capturing all traffic to and from those hosts.
$tcp = $games | ForEach-Object { Get-NetTCPConnection -OwningProcess $_.Id -ErrorAction SilentlyContinue } |
    Where-Object { $_.State -eq 'Established' -and $_.RemoteAddress -notmatch '^(127\.|::1)' }
$hosts = @($tcp | Select-Object -ExpandProperty RemoteAddress -Unique)
# Only the game's own traffic: its TCP hosts, and its UDP sockets by their local ports (real-time game
# traffic may run over UDP; a first capture on the TCP hosts alone saw no packets in 5 s).
$udpPorts = @($games | ForEach-Object { Get-NetUDPEndpoint -OwningProcess $_.Id -ErrorAction SilentlyContinue } |
    Where-Object { $_.LocalAddress -notmatch '^(127\.|::1)' } | Select-Object -ExpandProperty LocalPort -Unique)
$parts = @($hosts | ForEach-Object { "host $_" })
if ($udpPorts.Count -gt 0) { $parts += "(udp and (" + (($udpPorts | ForEach-Object { "port $_" }) -join ' or ') + "))" }
$filter = $parts -join ' or '

# Game traffic through a local relay (e.g. ExitLag: AION2 -> 127.0.0.1:<port> -> tunnel): on the network
# card only the relay's tunnel is visible, the game stream itself only on loopback. Then record exactly
# the game's connections to that relay port on the Npcap loopback adapter.
$gameIds = @($games | ForEach-Object { $_.Id })
$relayPorts = @($games | ForEach-Object { Get-NetTCPConnection -OwningProcess $_.Id -State Established -ErrorAction SilentlyContinue } |
    Where-Object { $_.RemoteAddress -match '^(127\.|::1)' } |
    Where-Object {
        $peer = Get-NetTCPConnection -LocalPort $_.RemotePort -State Established -ErrorAction SilentlyContinue | Select-Object -First 1
        $peer -and ($gameIds -notcontains $peer.OwningProcess)
    } | Select-Object -ExpandProperty RemotePort -Unique)
if ($relayPorts.Count -gt 0) {
    $device = '\Device\NPF_Loopback'
    $filter = 'tcp and (' + (($relayPorts | ForEach-Object { "port $_" }) -join ' or ') + ')'
    $adapter = [pscustomobject]@{ Name = "Loopback (relay port $($relayPorts -join ', '))" }
}
else {
    if ($hosts.Count -eq 0) { throw 'The game has no direct or relay connections.' }
    $route = Find-NetRoute -RemoteIPAddress $hosts[0] | Select-Object -First 1
    $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex
    $device = "\Device\NPF_$($adapter.InterfaceGuid)"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$file = Join-Path $OutDir "aion2-$stamp.pcapng"

$meta = [ordered]@{
    start       = (Get-Date).ToString('o')
    seconds     = $Seconds
    interface   = $adapter.Name
    filter      = $filter
    connections = @($tcp | ForEach-Object { [ordered]@{ remote = "$($_.RemoteAddress):$($_.RemotePort)"; local = $_.LocalPort } })
}
$meta | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -Path ([IO.Path]::ChangeExtension($file, '.json'))

Write-Host "Recording $Seconds s on '$($adapter.Name)' -> $file"
Write-Host "Filter: $filter"
& $dumpcap -i $device -f $filter -a "duration:$Seconds" -w $file -q
if ($LASTEXITCODE -ne 0) { throw "dumpcap ended with exit code $LASTEXITCODE." }
$size = (Get-Item -LiteralPath $file).Length
Write-Host ("Done: {0:N0} bytes." -f $size)
