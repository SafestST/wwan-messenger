# Compatibility — what works, honestly

WwanMessenger talks to *Windows' cellular stack*, not to any one vendor's silicon.
That is the root of its portability: anything Windows can drive, WwanMessenger can
text through.

## Requirements (all mandatory)

1. **Windows 10 (build 19041+) / Windows 11, x64.** The app is self-contained — no
   .NET install needed (the release EXE). The Mobile Broadband COM API it
   uses has shipped in Windows since Windows 7.
2. **A WWAN modem the machine's OS actually drives.** In practice: any modern
   M.2 cellular module (Qualcomm-, Intel XMM-, MediaTek-based) or USB
   cellular dongle that Windows recognizes. Rule of thumb: if
   `netsh mbn show interfaces` lists an interface, WwanMessenger can use it.
3. **A SIM with SMS provisioned.** Data-only plans cannot send texts (the
   network refuses — we lived this: unactivated prepaid SIMs fail every send
   with a 60-second network refusal until activated). Regional/carrier
   independent: the app reads the SMSC from the modem, so any carrier works
   without configuration.

## Per-capability matrix

| Capability | Requirement | Notes |
|---|---|---|
| **Send (MBIM)** | Windows-driven modem with PDU SMS support | Verified live: Quectel EM120R-GL (ThinkPad X1 Carbon Gen 9, 20XW) on Orange PL. The API is vendor-neutral; Sierra/Fibocom/Intel-XMM/MediaTek MBIM modems use the identical path. |
| **Receive, mode A — Windows Chat store** | Windows routes inbound SMS into its Chat platform store | Verified live on the above stack. This is the common behavior on Windows 10/11 desktop with MBIM modems. |
| **Receive, mode B — modem-store sweep** | Modem keeps inbound SMS readable in its own store | **Automatic fallback**: if the Chat store is unavailable, WwanMessenger switches to sweeping the modem store (logged as `receive mode: modem-store sweep`). Slow on some firmware (reads can take 30–80 s) but functional. |
| **Receive, mode C — AT serial** | Modem exposes an AT COM port (vendor driver, dongles, older modules) | Full `+CMTI`/`CMGR` engine, instant delivery. Select the COM port in the app. USB dongles almost always expose one. |
| **Simulator** | Nothing (no modem, no SIM) | Full app experience for development and demos. |
| **MMS (photos/stickers)** | — | **Not supported by desktop Windows at any level** — the OS has no MMS stack, so a sticker sent from an iPhone never reaches the machine as data. Attachment-bearing messages and carrier MMS→link SMSes are handled (media saved to `%AppData%\WwanMessenger\attachments`). |

## Known quirks by stack (live-tested notes)

- **Quectel EM120R-GL + Windows 11 (ThinkPad 20XW):** receive lands in the
  Chat store (mode A); the modem's own store is emptied by Windows before
  apps can read it; the driver never pushes signal events (the meter is
  poll-driven). All documented in `docs/BRINGUP.md`.
- **LTE-only modems** (EM120R-GL included) need the carrier to support SMS
  over IMS/VoLTE — standard for consumer carriers, occasionally missing on
  data-focused MVNOs.

## Tested hardware

| Machine | Modem | SIM | Result |
|---|---|---|---|
| Lenovo ThinkPad X1 Carbon Gen 9 (20XW005GUK) | Quectel EM120R-GL (fw EM120RGLAPR02A07M4G) | Orange PL prepaid | Send + receive verified live, end-to-end, over the air |

**Everything else is expected-to-work per the architecture above but not
yet live-verified — if you run WwanMessenger on different hardware, please open
a GitHub issue with your machine/modem/carrier and what worked; tested-
hardware rows get added here.**

## Quick self-check on any machine

```powershell
netsh mbn show interfaces     # an interface here → MBIM backend available
```

Then run `WwanMessenger.exe` — it auto-selects `◆ MBIM MODEM` when present and
tells you its receive mode in the console drawer.
