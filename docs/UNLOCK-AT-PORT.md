# Unlocking the AT port (stable receive)

## Where we are (2026-09-22, evening)

- **Send**: fully working through MBIM (one request / one completion / no
  silent retries — verified live).
- **Receive**: blocked. Inbound texts land in the modem (store index climbs,
  store-change events fire) but the Microsoft generic MBIM driver cannot read
  them back — `mbncli probe` tried every `SmsRead` permutation (4 statuses ×
  index/no-index × PDU/TEXT) before AND after the Lenovo driver install:
  zero messages ever returned.
- The modem's USB is a **UDE virtual composite (NCM + QMUX only)** — the AT
  interface is not routed to Windows by the current firmware configuration
  (`AT+QCFG="usbnet"` / `AT+QCFG="data_interface"`), and Quectel's `qcser.inf`
  port driver sits staged in the driver store with nothing to bind.

The WwanMessenger serial-AT backend (receive via `+CMTI`/`CMGR`) is built and
smoke-tested since day one — the moment a "Quectel AT Port (COMx)" exists,
press SCAN in the GUI, pick it, CONNECT, and both directions work.

## Option 1 — Quectel's own Windows USB driver package (try first)

Lenovo's package installed the aux drivers but created no ports. Quectel's
generic package is known to enumerate AT ports in exactly this situation:

1. Download **"Quectel_LTE&5G_Windows_USB_Driver_V2.4"** (or newer) from
   [quectel.com](https://www.quectel.com/download-page/usb-driver/) —
   requires a free Quectel account.
2. Run the installer (it binds the staged `qcser.inf` and, on modules with
   QMUX-only compositions, creates the bridged **"Quectel AT Port (COMx)"**).
3. Reboot. Check: `Device Manager → Ports (COM & LPT)` or run
   `tools\hw-probe.ps1 -ScanAt`.
4. If an AT port appears → GUI → SCAN → select it → CONNECT. Done.

## Option 2 — one-time Linux session (certain)

If Option 1 yields no port, switch the USB composition once from Linux
(Linux binds the modem's real interfaces natively, no chicken-and-egg):

1. Boot an Ubuntu live USB (any recent release; "Try Ubuntu").
2. Open a terminal and run `tools/at-unlock.sh` (copy it to the USB stick
   first) — it finds the modem's AT channel, prints the CURRENT composition
   (`AT+QCFG="usbnet"`, `AT+QCFG="data_interface"`), lists the values the
   firmware reports as supported (`AT+QCFG=?`), and switches to a
   composition that routes **AT + DIAG + NMEA + the network port over USB**.
   It prints exactly what it does; nothing is changed until the current
   values are shown.
3. Power off, boot back into Windows. The staged `qcser.inf` binds the new
   serial interfaces → AT port exists → GUI → SCAN → CONNECT.

Reversal: re-run the script with `--restore <value>` if the Windows side ever
needs the old composition.

## After the unlock

```powershell
# quick check
cd wwan-messenger
powershell -File tools\hw-probe.ps1 -ScanAt
# GUI
dotnet run --project src/WwanMessenger.App   # SCAN → pick the Quectel AT Port → CONNECT
```

Receive then flows through the AT backend: `+CMTI` notification → `AT+CMGR`
→ PDU decoded by WwanMessenger.Core → conversation in the GUI. The store's
stranded messages also become readable that way (slots the generic driver
could not see).
