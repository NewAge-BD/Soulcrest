#Requires -Version 5.1
[CmdletBinding()]
param([string] $Compiler, [string] $Version)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath "$repoRoot\Directory.Build.props" -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must be numeric (X.Y.Z).' }
if (-not $Compiler) { $Compiler = Join-Path $repoRoot 'artifacts\tools\inno-7.1.0\ISCC.exe' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup compiler missing. Pass -Compiler <ISCC.exe>.' }
$mapData = Join-Path $repoRoot 'imports\generated\mapdata'
if (-not (Test-Path -LiteralPath "$mapData\manifest.json")) { throw 'Generated mapdata missing; build the authorized local imports first.' }
$output = Join-Path $repoRoot 'artifacts\installer'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$payload = Join-Path $repoRoot "artifacts\installer-build\$stamp\payload"
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null
$revision = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
$sourceStatus = @(& git -C $repoRoot status --porcelain)
& dotnet publish (Join-Path $repoRoot 'src\Soulcrest.App') -c Release -r win-x64 --self-contained true -o $payload --nologo `
    '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' `
    '-p:PublishTrimmed=false' '-p:DebugType=none' "-p:Version=$Version" "-p:SourceRevisionId=$revision"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
& robocopy $mapData "$payload\mapdata" /E /COPY:DAT /DCOPY:T /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw 'Copying the map data failed.' }
Copy-Item -LiteralPath "$PSScriptRoot\installer\README-Setup.txt" -Destination $payload
foreach ($file in 'Soulcrest.exe','wwwroot\index.html','mapdata\manifest.json','mapdata\pets.json','THIRD_PARTY_NOTICES.md') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Payload incomplete: $file" }
}
[ordered]@{
    version=$Version; sourceRevision=$revision; sourceStatus=$sourceStatus; builtAt=(Get-Date).ToString('o');
    dotnet=(& dotnet --version); mapManifestSha256=(Get-FileHash "$payload\mapdata\manifest.json").Hash
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$payload\BUILD-INFO.json" -Encoding utf8
& $Compiler "/DPayloadDir=$payload" "/DOutputDir=$output" "/DAppVersion=$Version" "$PSScriptRoot\installer\Soulcrest.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "Soulcrest-$Version-Setup-win-x64.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
Copy-Item -LiteralPath "$payload\BUILD-INFO.json" -Destination "$output\BUILD-INFO.json" -Force
Write-Host "Installer: $setup"
Write-Host "Payload:   $payload"
