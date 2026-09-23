# Publish: create repo, push the single clean commit, upload release asset.
# Token from GH_TOKEN env. Target: SafestST/wwan-messenger, homepage safest.st
$token = $env:GH_TOKEN
if (-not $token) { Write-Host 'GH_TOKEN not set'; exit 1 }
$h = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$slug = 'SafestST/wwan-messenger'

# 1. create repo
$body = @{
    name = 'wwan-messenger'
    description = "Native Windows SMS client for the laptop's own WWAN modem - no phone, no cloud relay"
    homepage = 'https://safest.st'
    private = $false
    has_issues = $true
} | ConvertTo-Json
try {
    $repo = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug" -Headers $h
    Write-Host ("repo exists: " + $repo.html_url)
} catch {
    $repo = Invoke-RestMethod -Uri 'https://api.github.com/user/repos' -Method Post -Headers $h -ContentType 'application/json' -Body $body
    Write-Host ("repo created: " + $repo.html_url)
}

# 2. push main
Set-Location (Join-Path $PSScriptRoot '..')
$git = "$env:ProgramFiles\Git\cmd\git.exe"
& $git remote remove origin 2>$null
& $git remote add origin "https://${token}@github.com/$slug.git"
& $git branch -M main
& $git push -u origin main 2>&1 | Select-Object -Last 2
& $git remote set-url origin "https://github.com/$slug.git"

# 3. release v1.0.0 with the zip
$relBody = @{
    tag_name = 'v1.0.0'
    name = 'WWAN Messenger v1.0.0'
    body = "First public release.`n`nSelf-contained Windows x64 build - no .NET install needed. Unzip and run WwanMessenger.exe.`n`nRequires: Windows 10 (19041+) or Windows 11, a laptop with a WWAN modem, a SIM with SMS provisioned.`n`nReceives via the Windows Chat platform store (with modem-store fallback), sends over the Mobile Broadband API. Full documentation in the repo: SETUP-GUIDE, COMPATIBILITY, ARCHITECTURE, BRINGUP.`n`nSHA-256 checksums in SHA256SUMS.txt."
    draft = $false
    prerelease = $false
} | ConvertTo-Json
$relUri = "https://api.github.com/repos/$slug/releases"
$rel = Invoke-RestMethod -Uri $relUri -Method Post -Headers $h -ContentType 'application/json' -Body $relBody
Write-Host ("release: " + $rel.html_url)

$zipPath = Join-Path $PSScriptRoot '..\dist\WwanMessenger-v1.0.0.zip'
$uploadUri = $rel.upload_url.Replace('{?name,label}', '?name=WwanMessenger-v1.0.0.zip')
Invoke-RestMethod -Uri $uploadUri -Method Post -Headers $h -ContentType 'application/zip' -InFile $zipPath | Out-Null
Write-Host 'zip uploaded'
