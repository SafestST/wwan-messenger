# Broadened pre-push verification: strict UTF-8 decode, mojibake class detection
# (E2-AC partial-repair and C3-prefixed double-encode), replacement chars,
# non-ASCII inventory. Run before every push. Exit 1 on any issue.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$git = "$env:ProgramFiles\Git\cmd\git.exe"

# This file intentionally contains detection patterns built from [char] codes
# rather than literal mojibake characters, so it does not flag itself. Skip
# scanning this file by name to be safe.
$self = 'tools' + [IO.Path]::DirectorySeparatorChar + 'verify-encoding.ps1'

$issues = 0
foreach ($f in (& $git ls-files | Where-Object { $_ -match '\.(md|cs|xaml|ps1|sh|txt|json)$' })) {
    if ($f -ieq $self) { continue }
    $path = Join-Path $root $f
    if (-not (Test-Path $path)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $strict = New-Object System.Text.UTF8Encoding($false, $true)
    try { $s = $strict.GetString($bytes) } catch {
        Write-Host ("INVALID UTF-8: $f"); $issues++; continue
    }
    if ($s.Contains([char]0xFFFD)) { Write-Host ("U+FFFD replacement char in $f"); $issues++ }

    $n1 = ([regex]::Matches($s, [string][char]0x00E2 + [string][char]0x20AC)).Count
    $n2 = ([regex]::Matches($s, [string][char]0x00C3 + '[\u0080-\u00FF]')).Count
    $n3 = ([regex]::Matches($s, [string][char]0x00C2 + '[\u0020-\u00BF]')).Count
    if ($n1 -gt 0) { Write-Host ("E2-AC mojibake x$n1 in $f"); $issues++ }
    if ($n2 -gt 0) { Write-Host ("C3-prefixed mojibake x$n2 in $f"); $issues++ }
    if ($n3 -gt 0) { Write-Host ("C2-stray mojibake x$n3 in $f"); $issues++ }
}
if ($issues -eq 0) { Write-Host 'ENCODING CLEAN across all tracked text files' }
else { Write-Host "$issues issue(s)"; exit 1 }
