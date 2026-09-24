# WWAN MESSENGER — SMS for the SIM already inside your laptop

A native Windows SMS client that sends and receives texts through the
laptop's **own WWAN modem** — no phone involved, no cloud relay, full
history kept locally. Built and verified live end-to-end on a ThinkPad
X1 Carbon Gen 9 (Quectel EM120R-GL, Orange PL); portable by architecture
to any Windows-driven cellular modem. See
**[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)** for the honest
compatibility matrix.

> Verified over the air: send completion `S_OK` in ~0.4 s, receive
> propagation ≤ 10 s, full weeks of SIM-resident history imported, a
> forced 80-second WWAN-service outage recovered automatically in five
> retries. All protocol claims locked by a 70-check regression suite.

## Get it

- **Releases** → download `WwanMessenger-v1.0.0.zip`, unzip anywhere, run
  `WwanMessenger.exe`. Self-contained (~73 MB): no .NET install, no
  prerequisites beyond Windows 10 (19041+) x64, a WWAN modem and a SIM with
  SMS provisioned. First run: SIM in → Windows detects it → WwanMessenger
  auto-connects (`◆ MBIM MODEM`).
- **From source**: .NET 6 SDK →

```powershell
git clone <this repo>
cd wwan-messenger
dotnet run --project src/WwanMessenger.App      # GUI — SIMULATOR if no modem
dotnet run --project src/WwanMessenger.Smoke    # 70-check protocol suite
powershell -ExecutionPolicy Bypass -File tools\publish.ps1   # your own EXE
```

Requirements, walkthrough, troubleshooting:
**[docs/SETUP-GUIDE.md](docs/SETUP-GUIDE.md)**.

## How it works

```
WwanMessenger.App            WPF client — three themes (Paper/Graphite/Nord),
   ├                    smooth-scrolling chats, pinning, contact rename,
   ├                    custom chrome, live modem console drawer
   ─│ ISmsBackend │││││ one interface, three interchangeable backends:
        └│ MbnSmsService         MBIM via the Windows Mobile Broadband COM
        ├    ─│ ChatStoreReceiver  API (send) + Chat platform store (receive)
        └│ SmsService             AT serial: CMGS send, +CMTI/CMGR receive
        ─│ SimulatedModemChannel  in-process modem with a replying bot
WwanMessenger.Core            GSM 03.40 PDU / 03.38 codecs (canonical vectors),
                         segmenter, multipart reassembler, JSON store,
                         supervision & auto-reconnect, discovery
```

Receiving is adaptive: the Windows Chat store when the platform routes SMS
there (common), modem-store sweeps when it doesn't, AT-serial for
port-exposed modems. The console drawer shows which mode is live
(`receive mode: …`) along with every poll, send, and inbound event.

## Engineering notes worth reading

- **Data-safety contract** (`docs/BRINGUP.md`): one send = one modem
  request; retries only after explicit network refusal — never after an
  ambiguous timeout (a message that *might* have been delivered is never
  resent). Idempotent imports; nothing destructive.
- **Live-verified bring-up log** (`docs/BRINGUP.md`): the activation
  saga, the Windows-intercepts-your-SMS discovery, error-code decodings.
- **The `?` defect post-mortem**: a GSM table bug that survived symmetric
  round-trip tests and was only catchable against real handsets — caught,
  fixed over the air, locked with deterministic PDU vectors.
- `docs/UNLOCK-AT-PORT.md` — surfacing an AT port on MBIM-only modems.

## Controls

Enter sends · Shift+Enter newline · live segment counter (`GSM7 · 2 SEG ·
118/306`) · pin channels (iPhone-style) · rename contacts · theme cycling
from the title bar · CONSOLE » for the live AT/MBIM transcript · LINK lamp
counts seconds since the last healthy poll.

## Documentation

| Doc | For |
|---|---|
| [docs/SETUP-GUIDE.md](docs/SETUP-GUIDE.md) | users — install, first run, troubleshooting |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | the honest compatibility matrix |
| [docs/TESTED-BUILD.md](docs/TESTED-BUILD.md) | exact hardware this was verified on |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | layers, protocol strategy, threading |
| [docs/BRINGUP.md](docs/BRINGUP.md) | live bring-up log + data-safety contract |
| [docs/UNLOCK-AT-PORT.md](docs/UNLOCK-AT-PORT.md) | AT-port escape hatch |

## Roadmap

Delivery reports (TP-SRR + `+CDS`), Windows notifications + tray, SQLite
store, contact import, per-conversation E2E encryption ("dark channel"
mode), MMS retrieval client.

## License & maintainer

MIT — see [LICENSE](LICENSE). © 2026 [Safest Security Limited](https://safest.st).
Built and maintained by [Safest Security Limited](https://safest.st) — [safest.st](https://safest.st).
