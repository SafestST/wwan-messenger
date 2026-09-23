# Re-init: single clean commit (no history), rebuild release zip, verify.
Set-Location (Join-Path $PSScriptRoot '..')

$git = "$env:ProgramFiles\Git\cmd\git.exe"
if (-not (Test-Path $git)) { $git = "$env:ProgramFiles\Git\bin\git.exe" }
if (-not (Test-Path $git)) { Write-Host 'git not found'; exit 1 }

# Nuke history entirely - the pre-scrub commit must never leave this machine
Remove-Item -Recurse -Force '.git'
& $git init | Select-Object -First 1
& $git config user.name 'SafestST'
& $git config user.email 'SafestST@users.noreply.github.com'
& $git config core.autocrlf false
& $git add -A
& $git commit -q -m @'
WwanMessenger: native Windows SMS client for the laptop WWAN modem

Three backends (MBIM COM send + Chat-store receive with modem-sweep and
AT-serial fallbacks, in-process simulator), GSM 03.40/03.38 codec with
canonical-vector regression suite (63 checks), supervision with automatic
transport rebuild, WPF client with three themes, pinning, rename, smooth
scrolling and a live modem console. Live-verified end-to-end on ThinkPad
X1 Carbon Gen 9 / Quectel EM120R-GL / Orange PL. Privacy-scanned before
publish (tools/privacy-sweep.ps1).
'@
$commits = (& $git rev-list --count HEAD)
Write-Host "commit count: $commits (must be 1)"
& $git log --oneline

Write-Host '=== release zip ==='
Remove-Item 'dist\WwanMessenger-v1.0.0.zip' -Force -ErrorAction SilentlyContinue
Compress-Archive -Path 'dist\WwanMessenger\*' -DestinationPath 'dist\WwanMessenger-v1.0.0.zip' -Force
$h1 = (Get-FileHash 'dist\WwanMessenger-v1.0.0.zip' -Algorithm SHA256).Hash
$h2 = (Get-FileHash 'dist\WwanMessenger\WwanMessenger.exe' -Algorithm SHA256).Hash
$zipMB = [math]::Round((Get-Item 'dist\WwanMessenger-v1.0.0.zip').Length / 1MB, 2)
Write-Host "zip sha256: $h1  ($zipMB MB)"
Write-Host "exe sha256: $h2"
"$h1  WwanMessenger-v1.0.0.zip`n$h2  WwanMessenger/WwanMessenger.exe" | Set-Content 'dist\SHA256SUMS.txt'
Write-Host 'checksums written'
