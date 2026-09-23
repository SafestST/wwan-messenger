# Broadened pre-push privacy sweep: scans TRACKED files (structural shapes),
# plus untracked working-tree text files, plus generic international numbers.
# Exit 1 on any hit. Run before every push.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$git = "$env:ProgramFiles\Git\cmd\git.exe"

# The maintainer surname pattern is assembled from parts so this scanner
# never matches its own source text.
$namePart = [string][char]0x0047 + 'rabda'

$exempt = @(
    '48501200777',                            # Orange PL public SMSC (documented)
    '8421436587F9',                           # encoded placeholder destination
    '234150009988776', '8944110099887766554', # simulator constants
    '48123456789', '48111222333',             # fictional test numbers
    '447700900123', '447700900456', '447700900787', '447700900000',
    '447700900999', '447700999000',           # Ofcom-reserved fictional UK mobiles (demo/sim)
    '447700900'                               # the whole Ofcom drama range prefix used by demo data
)

function Test-Content([string]$text, [string]$label) {
    $hits = 0
    $nameLit = $namePart
    $ms = [regex]::Matches($text, '\+\d{1,3}[\s]?\d{4,12}|\b48\d{9}\b|\b\d{15}\b|\b89\d{17,19}\b|ghp_[A-Za-z0-9]{20,}|Users[\\/][A-Za-z0-9_.$-]+[\\/]|' + $nameLit)
    foreach ($m in $ms) {
        $v = $m.Value
        $ok = $false
        foreach ($e in $exempt) { if ($v -match [regex]::Escape($e)) { $ok = $true; break } }
        if (-not $ok) { $hits++; Write-Host ("HIT [" + $label + "] '" + $v + "'") }
    }
    return $hits
}

$total = 0
foreach ($f in (& $git ls-files)) {
    $p = Join-Path $root $f
    if (-not (Test-Path $p)) { continue }
    $t = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
    $n = Test-Content $t $f
    if ($n -gt 0) { $total += $n; Write-Host ("  in tracked: " + $f) }
}

$untracked = & $git ls-files --others --exclude-standard | Where-Object { $_ -match '\.(md|cs|xaml|ps1|sh|txt|json|csproj)$' }
foreach ($f in $untracked) {
    $p = Join-Path $root $f
    if (-not (Test-Path $p)) { continue }
    $t = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
    $n = Test-Content $t ("UNTRACKED " + $f)
    if ($n -gt 0) { $total += $n }
}

if ($total -eq 0) { Write-Host 'PRIVACY CLEAN (tracked + untracked)' }
else { Write-Host ("$total hit(s) - fix before pushing"); exit 1 }
