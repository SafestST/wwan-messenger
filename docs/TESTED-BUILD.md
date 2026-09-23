# Tested build — exact hardware specification

The reference machine this project was live-verified on, end-to-end over the
air (send, receive, supervision, outage recovery).

## Machine

| Component | Specification |
|---|---|
| Model | Lenovo ThinkPad X1 Carbon Gen 9 — **20XW005GUK** (UK SKU) |
| WWAN option fitted | Quectel **EM120R-GL** (factory option; the same chassis optionally ships a Fibocom L860-GL — both are MBIM modems and architecturally interchangeable for WwanMessenger) |
| WWAN firmware | `EM120RGLAPR02A07M4G` |
| WWAN interface to Windows | MBIM over a UDE-emulated USB composite (NCM + QMUX); data modem bound to the Microsoft in-box class driver ("Generic Mobile Broadband Adapter"), Quectel aux drivers (QMUX interconnect, firmware device, UDE, MHI host) staged |
| WWAN class | LTE Cat 12 — LTE-only (no 2G/3G fallback; SMS rides IMS/VoLTE) |
| OS at verification | Windows 11 22H2 (22631), x64 |
| .NET | Self-contained .NET 6 build (net6.0-windows10.0.19041.0) |

## SIM / service

| Item | Value |
|---|---|
| Carrier | Orange Polska (MCC 260, MNC 03) prepaid |
| SMSC reported by modem | +48501200777 (Orange PL public service centre) |
| IMSI / ICCID / MSISDN | **redacted** — subscriber identifiers tie the repo to a specific person and account; they are not needed to replicate any of this. Your own SIM supplies its own. |
| Account notes | Prepaid required activation (top-up) before the network would deliver SMS — unactivated SIMs register for data fine while every send is refused (WWAN code 33 after a 60 s hold). Carriers differ; see `docs/BRINGUP.md`. |

## Behavior observed on this exact stack (reproducible)

1. Windows routes every inbound MBIM SMS into its **Chat platform store**
   and deletes it from the modem store before applications can read it —
   WwanMessenger reads the Chat store (its console prints
   `receive mode: Windows chat store`).
2. The WWAN driver pushes **no signal events** (`OnSignalStateChange` never
   fires) — the signal meter is poll-driven by hardware limitation.
3. Modem-store reads complete in 30–80 s and report empty as an error
   (`0x80548222`) — irrelevant while the Chat store is the receive channel.
4. Send via `IMbnSms::SmsSendPdu` completes in ~0.4 s post-activation.
