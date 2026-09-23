<#
.SYNOPSIS
    WwanMessenger hardware bring-up probe for the ThinkPad's WWAN modem.
.DESCRIPTION
    Run this after inserting the SIM. It inventories the WWAN hardware, finds
    the modem's AT-capable COM port, and (with -ScanAt) performs a live AT
    handshake: ATI, CPIN?, CSQ, COPS, CEREG — the exact command family WwanMessenger
    uses. Everything is echoed to the console and saved to hw-probe-report.txt.

    Typical use:
        powershell -ExecutionPolicy Bypass -File .\hw-probe.ps1 -ScanAt
        powershell -ExecutionPolicy Bypass -File .\hw-probe.ps1 -ScanAt -AllPorts   # brute-force every COM port
#>
param(
    [switch]$ScanAt,      # attempt an AT handshake on likely modem ports
    [switch]$AllPorts,    # with -ScanAt, try every COM port, not just modem-looking ones
    [string]$Port         # probe only this port (e.g. -Port COM7)
)

$ErrorActionPreference = 'Continue'
$report = New-Object System.Collections.Generic.List[string]
function Write-Section([string]$t) { $line = "`n=== $t ==="; $line; $report.Add($line) }
function Write-Out2([string]$t) { $t; $report.Add($t) }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Out2 "WwanMessenger hardware probe — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  (admin: $admin)"

# --- 1. PnP inventory ---------------------------------------------------------
Write-Section 'WWAN / modem / COM PnP devices'
Get-CimInstance Win32_PnPEntity |
    Where-Object { $_.Name -match 'Fibocom|Quectel|Sierra|WWAN|Mobile Broadband|Modem|\(COM\d' } |
    ForEach-Object {
        Write-Out2 ("  {0}" -f $_.Name)
        Write-Out2 ("      DeviceID : {0}" -f $_.DeviceID)
        Write-Out2 ("      Status   : {0}  (code {1})" -f $_.Status, $_.ConfigManagerErrorCode)
    }

# --- 2. Registry serial map ---------------------------------------------------
Write-Section 'SERIALCOMM registry map'
$sc = Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM' -ErrorAction SilentlyContinue
if ($sc) {
    $sc.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } |
        ForEach-Object { Write-Out2 ("  {0,-24} -> {1}" -f $_.Name, $_.Value) }
} else {
    Write-Out2 '  (no serial devices registered)'
}

# --- 3. Windows Mobile Broadband stack ---------------------------------------
Write-Section 'WwanSvc service'
Get-Service WwanSvc -ErrorAction SilentlyContinue | ForEach-Object { Write-Out2 ("  {0} : {1}" -f $_.Name, $_.Status) }

Write-Section 'netsh mbn interfaces'
$mbn = netsh mbn show interfaces 2>&1 | Out-String
Write-Out2 ($mbn.Trim() -split '\r?\n' | ForEach-Object { "  $_" })

if ($mbn -match 'There is no Mobile Broadband interface') {
    Write-Out2 '  NOTE: no MBIM interface. That is fine for AT-port SMS, but means Windows itself'
    Write-Out2 '        is not currently driving the modem. Check the Lenovo Fibocom WWAN driver.'
}

# --- 4. AT handshake ----------------------------------------------------------
if ($ScanAt -or $Port) {
    Write-Section 'AT handshake scan'
    $candidates = @()
    if ($Port) {
        $candidates = @($Port)
    } else {
        $all = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object -Unique
        if ($AllPorts) {
            $candidates = $all
        } else {
            $friendly = Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match '\(COM\d+\)' }
            $candidates = @($friendly | Where-Object {
                $_.Name -match 'Fibocom|WWAN|Mobile Broadband|AT Port|Modem'
            } | ForEach-Object { if ($_.Name -match '\((COM\d+)\)') { $Matches[1] } }) | Sort-Object -Unique
            if (-not $candidates) { $candidates = $all }
        }
    }

    Write-Out2 ("  candidate ports: " + ($candidates -join ', '))

    foreach ($name in $candidates) {
        Write-Out2 "`n  ---- $name ----"
        $p = New-Object System.IO.Ports.SerialPort $name, 115200, 'None', 8, 'One'
        $p.ReadTimeout = 1200; $p.WriteTimeout = 1200; $p.DtrEnable = $true; $p.RtsEnable = $true
        try {
            $p.Open()
            $p.DiscardInBuffer()
        } catch {
            Write-Out2 ("    open failed: " + $_.Exception.Message)
            continue
        }
        try {
            foreach ($cmd in 'AT', 'ATE0', 'ATI', 'AT+CPIN?', 'AT+CSQ', 'AT+COPS?', 'AT+CEREG?', 'AT+CMGF=?') {
                $p.DiscardInBuffer()
                $p.Write($cmd + "`r")
                Start-Sleep -Milliseconds 500
                $resp = ''
                try { $resp = $p.ReadExisting() } catch {}
                $resp = ($resp -replace "`r", '' -replace "`n+", ' | ').Trim()
                Write-Out2 ("    {0,-12} -> {1}" -f $cmd, ($(if ($resp) { $resp } else { '(no response)' })))
                if ($cmd -eq 'AT' -and -not $resp) { break }   # dead port, skip the rest
            }
        } finally {
            try { $p.Close() } catch {}
            $p.Dispose()
        }
    }
}

# --- 5. save ------------------------------------------------------------------
$outPath = Join-Path $PSScriptRoot 'hw-probe-report.txt'
$report | Out-File -FilePath $outPath -Encoding utf8
Write-Section "report saved to $outPath"
Write-Out2  "Next: if a port answered ATI with a Fibocom/WWAN identity and CPIN? said READY,"
Write-Out2  "start WwanMessenger, pick that COM port in the header bar, and press CONNECT."
