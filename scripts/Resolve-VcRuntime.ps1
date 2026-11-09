#Requires -Version 5.1
<#[
Find the three Microsoft redistributable x64 CRT files required by the bundled
native solver. Read only; paths are consumed by the publish target and bundled
with the executable. No installation or download occurs here.
]#>
[CmdletBinding()]
param([string] $Directory)
$ErrorActionPreference = 'Stop'
# MSBuild can inherit PowerShell 7's module path. Use the matching built-in module
# explicitly when the build invokes Windows PowerShell 5.1.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

if (-not $Directory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual C++ redistributable files missing. Set SoulcrestVcRuntimeDirectory to the official x64 Microsoft.VC143.CRT directory.'
    }
    $installation = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installation.Count -ne 1) { throw 'Cannot locate Visual C++ build tools.' }
    $redist = Join-Path $installation[0] 'VC\Redist\MSVC'
    $candidates = @(Get-ChildItem -LiteralPath $redist -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending)
    foreach ($candidate in $candidates) {
        $crt = Join-Path $candidate.FullName 'x64\Microsoft.VC143.CRT'
        if (Test-Path -LiteralPath $crt -PathType Container) { $Directory = $crt; break }
    }
}

if (-not $Directory -or $Directory.StartsWith('\\')) { throw 'A local official x64 CRT directory is required.' }
$resolved = (Resolve-Path -LiteralPath $Directory).ProviderPath
foreach ($name in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
    $file = Get-Item -LiteralPath (Join-Path $resolved $name)
    if ($file.Length -eq 0 -or $file.PSIsContainer) { throw "Invalid CRT file: $name" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "CRT file must have a valid Microsoft signature: $name"
    }
    Write-Output $file.FullName
}
