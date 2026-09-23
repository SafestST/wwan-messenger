<#
.SYNOPSIS
    One-command release builder for WwanMessenger.

.DESCRIPTION
    Publishes the WPF client into dist\WwanMessenger\ as a folder that IS the app:
    one compressed single-file WwanMessenger.exe plus the README and setup guide
    copied next to it. No installer, no registry, no .NET install on the
    target machine (default mode).

    Default (self-contained, ~70-90 MB EXE):
        Every .NET 6 runtime library is embedded and compressed into the
        single file. Native libraries self-extract at launch. This is the
        variant docs\SETUP-GUIDE.md documents: copy the folder to any
        Windows 10/11 x64 laptop with a WWAN modem and run WwanMessenger.exe.

    -FrameworkDependent (~21 MB EXE):
        Publishes without the .NET runtime; requires ".NET Desktop Runtime
        6.0.x (x64)" preinstalled on the target machine (dotnet.microsoft.com,
        free). Still not tiny: the Windows SDK WinRT projection assemblies
        (Microsoft.Windows.SDK.NET, needed for the chat-store receive
        channel) ship with the app in every mode. Use it only when you
        control the target — developer-to-developer handoff, your own second
        machine. For everyone else the download-and-run story breaks at
        "please install a runtime", which is exactly what the default
        variant avoids. Tradeoff in one line: default = 73 MB nobody has to
        think about; this = 21 MB plus an install step that can go wrong.

    Debug symbols are embedded (DebugType=embedded) so crash stack traces
    stay meaningful without shipping a sidecar .pdb.

.PARAMETER FrameworkDependent
    Publish the small runtime-dependent variant instead of the
    self-contained default.

.PARAMETER Output
    Override the output directory (default: dist\WwanMessenger under the repo).

.PARAMETER DryRun
    Print the exact commands that would run, change nothing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1

    Build the shippable self-contained release into dist\WwanMessenger\.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -FrameworkDependent

    Build the small runtime-dependent variant (target machine needs the
    .NET 6 Desktop Runtime).
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$DryRun,
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project  = Join-Path $RepoRoot 'src\WwanMessenger.App\WwanMessenger.App.csproj'
$OutDir   = if ($Output) { [System.IO.Path]::GetFullPath($Output) }
            else { Join-Path $RepoRoot 'dist\WwanMessenger' }

$Docs = @(
    (Join-Path $RepoRoot 'README.md'),
    (Join-Path $RepoRoot 'LICENSE'),
    (Join-Path $RepoRoot 'docs\SETUP-GUIDE.md')
)

foreach ($p in @($Project) + $Docs) {
    if (-not (Test-Path $p)) { throw "required file missing: $p" }
}

$SelfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

$pubArgs = @(
    'publish', $Project,
    '-c', 'Release',
    '-r', 'win-x64',
    "--self-contained", $SelfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded'
)
# bundle compression is only supported for self-contained apps (NETSDK1176);
# the framework-dependent variant is small enough that it doesn't matter
if (-not $FrameworkDependent) { $pubArgs += '-p:EnableCompressionInSingleFile=true' }
$pubArgs += @('-o', $OutDir)

Write-Host ('mode      : ' + $(if ($FrameworkDependent) { 'framework-dependent (needs .NET 6 Desktop Runtime on target)' } else { 'self-contained (no .NET install needed)' }))
Write-Host ('output    : ' + $OutDir)
Write-Host ('command   : dotnet ' + ($pubArgs -join ' '))
Write-Host ('then copy : ' + (($Docs | ForEach-Object { Split-Path -Leaf $_ }) -join ', '))

if ($DryRun) { Write-Host ''; Write-Host 'dry run — nothing built.'; return }

# wipe stale output so the two modes never mix in one folder
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }

& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

foreach ($doc in $Docs) { Copy-Item $doc $OutDir -Force }

# The project emits WwanMessenger.App.exe (assembly name = project file name).
# The shipped name is WwanMessenger.exe — rename rather than setting AssemblyName
# in the csproj, so debug/build behaviour elsewhere is untouched. Renaming a
# single-file apphost before first run is safe.
$builtExe = Join-Path $OutDir 'WwanMessenger.App.exe'
if (Test-Path $builtExe) { Rename-Item $builtExe 'WwanMessenger.exe' }

if (-not (Test-Path (Join-Path $OutDir 'WwanMessenger.exe'))) {
    throw 'publish completed but WwanMessenger.exe is not in the output folder'
}

Write-Host ''
Write-Host 'output tree:'
$files = Get-ChildItem $OutDir -Recurse -File | Sort-Object FullName
$total = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($OutDir.Length).TrimStart('\')
    $mb = [math]::Round($f.Length / 1MB, 2)
    Write-Host ('  {0,-44} {1,10:N2} MB' -f $rel, $mb)
    $total += $f.Length
}
$exe = Get-Item (Join-Path $OutDir 'WwanMessenger.exe')
Write-Host ''
Write-Host ('WwanMessenger.exe : {0:N2} MB' -f ($exe.Length / 1MB))
Write-Host ('folder total : {0:N2} MB ({1} files)' -f ($total / 1MB), $files.Count)
