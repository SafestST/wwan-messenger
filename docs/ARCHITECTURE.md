# WwanMessenger — architecture

## What this is

A native Windows SMS client that sends and receives texts through the
laptop's **own WWAN modem**. Verified live end-to-end on a ThinkPad X1
Carbon Gen 9 (20XW) with a Quectel EM120R-GL on Orange PL; by architecture
it works with any modem Windows drives. No BIOS work, no Phone Link, no
cloud relay — the modem's own SMS capability, wrapped in a purpose-built
WPF console.

```
WWAN Messenger (WPF, .NET 6)
  + ISmsBackend -- one interface, three interchangeable backends:
      +-- MbnSmsService      MBIM via the Windows Mobile Broadband COM
      |     +-- ChatStoreReceiver  API (send) + Chat platform store (receive)
      +-- SmsService         AT serial: CMGS send, +CMTI/CMGR receive
      +-- SimulatedModemChannel  in-process modem with a replying bot
WwanMessenger.Core          GSM 03.40 PDU / 03.38 codecs (canonical vectors),
                       segmenter, multipart reassembler, JSON store,
                       supervision & auto-reconnect, discovery
```

## Protocol strategy

**PDU mode is the primary wire format.** `AT+CMGF=0` on the AT path;
`IMbnSms::SmsSendPdu` on the MBIM path:

- charset-independent — we decode GSM-7 / UCS-2 ourselves (extension table,
  the `0x1B` ESC ambiguity, reserved-code-point handling),
- works on every conformant modem,
- exact control of multipart segmentation (8-bit reference UDH, fill bits),
- locked by exact-PDU regression vectors (a live over-the-air `?`
  corruption bug was caught and fixed this way — see BRINGUP).

Text mode (`CMGF=1`, `CSCS="UCS2"` escalation) exists only as a per-message
fallback on the AT path.

**Sending (MBIM path):** PDU built in-process, prefixed with the modem's own
SMSC, submitted via the Mobile Broadband COM API. One send = one modem
request; retries only after an explicit network refusal — never after an
ambiguous timeout. See the data-safety contract in `docs/BRINGUP.md`.

**Receiving is adaptive**, chosen at connect time and reported in the
console drawer (`receive mode: …`):

1. **Windows Chat store** (common on Win10/11 MBIM stacks — Windows routes
   inbound SMS there and empties the modem store): history import on
   connect, event + 10 s idempotent poll for live arrivals, attachments
   saved to `%AppData%\WwanMessenger\attachments`.
2. **Modem-store sweep** (fallback when the Chat store is unavailable):
   `AT+CMGL`-equivalent reads with multipart reassembly.
3. **AT serial** (`SmsService` + a `SerialModemChannel`): `+CMTI` →
   `AT+CMGR` push receive for modems that expose an AT COM port.

**Status:** signal (CSQ/RSSI), registration, operator, radio technology and
SIM state are polled every 10 s; the LINK lamp shows seconds since the last
healthy poll. Supervision declares the transport dead after repeated poll
or receive failures and rebuilds the entire backend with backoff — verified
to self-heal a forced WWAN-service outage.

## Threading model

- The MBIM backend runs all COM work on a dedicated MTA thread with a
  serialised work queue; sends never queue behind slow store reads (sweeps
  yield in 250 ms slices when a send is pending).
- The AT transceiver serialises commands with a semaphore; URCs
  (`+CMTI`, `+CMT`, `RING`, `^…`) route to a separate event mid-command.
- Core events fire on worker threads; the App marshals to the UI
  dispatcher. MVVM-lite: `RelayCommand` + INPC, no framework.

## Simulator parity

`SimulatedModemChannel` implements the same AT subset as real hardware:
timing jitter, signal random-walk, `+CMS ERROR: 310` with no SIM, and a
replying "network bot" whose inbound messages are real PDUs built and
decoded by the production codec — every third reply is multipart. The full
client is exercisable with no modem and no SIM.

## Storage

`%AppData%\WwanMessenger\store.json` — conversations, statuses, modem references
and pins, flush-debounced every 2 s with corrupt-file recovery. Dedup is
the backend's job (exact store-message IDs); the store records everything
it is given, including identical rapid-fire texts. SQLite is the documented
upgrade path once volume justifies it.
