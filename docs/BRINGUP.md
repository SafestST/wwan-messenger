# WwanMessenger — live bring-up log & data-safety contract

Everything below was verified against the real machine and the real network.
This is the engineering log: what happened, what broke, and the contracts
that came out of it.

## Status summary (2026-09-22)

- **Send: verified live, end-to-end.** PDU built in-process → Mobile
  Broadband COM API → WwanSvc/MBIM → Quectel EM120R-GL → Orange PL SMSC →
  handset. Completion `S_OK` in ~0.4 s. One controlled send = one modem
  request, one completion, zero silent retries.
- **Receive: verified live.** Windows routes every inbound MBIM SMS into its
  Chat platform store; WwanMessenger reads that store (history import + live
  polling) and imports weeks of SIM-resident history on first run.
- **Supervision: verified live.** A forced 80-second WWAN-service outage was
  detected (`LINK LOST`) and the transport rebuilt itself automatically
  (`RECOVERED (attempt 5)`), no user action.

## Hardware/reference build

Exact machine, modem, firmware and SIM account notes (identifiers redacted)
are in `docs/TESTED-BUILD.md`. Short version: ThinkPad X1 Carbon Gen 9
(20XW005GUK), Quectel EM120R-GL LTE Cat 12, Windows 11 x64, Orange PL
prepaid SIM.

## The activation saga (carrier-side SMS gating)

With a freshly inserted prepaid SIM, every send was accepted by the modem
and then refused by the network: `0xC0040021` after an exact 60-second
hold — while registration and data-attach worked fine. That signature is
"prepaid account not yet activated": topping up the SIM cleared it
instantly. Anyone seeing code-33 refusals should check SIM
activation/balance before debugging anything else. Data-only or unactivated
plans frequently cannot SMS at all.

## The discovery: Windows intercepts your SMS

With the SIM inserted, inbound texts visibly arrived — the modem store's
last-used index climbed and `OnSmsStatusChange` fired — yet every store read
returned empty. The decisive observation: the moment a text landed, an
**`OnSmsDeleteComplete`** event fired that no code of ours had requested.
Windows' WWAN service intercepts inbound MBIM SMS, writes it into the
Windows Chat platform store, and deletes it from the modem store before an
application can read it. WwanMessenger therefore treats the Chat store as its
primary receive channel (see `src/WwanMessenger.Core/Mbn/ChatStoreReceiver.cs`)
and keeps a modem-store sweep as the automatic fallback for stacks where
that interception does not happen.

Firmware quirks recorded along the way: store reads complete in ~30–80 s
(and empty stores report `0x80548222` instead of an empty list), read
completion IDs are unreliable, and the driver never pushes signal events —
the meter is poll-driven on this stack.

## The `?` defect post-mortem (why exact vectors exist)

A recipient saw everything except the trailing `?`, which rendered as an
accented E. Root cause: our GSM-7 table had `'?'` placed at the **reserved
index 0x1F**, shadowing the real `?` at 0x3F — every question mark went on
air as a reserved code point. The bug survived round-trip tests because our
encoder and decoder shared the same wrong table; it was only catchable
against real handsets. Reproduced live (4/4 self-sends corrupted at exactly
the `?`), fixed, confirmed over the air, and locked with deterministic
exact-PDU regression vectors, including part-boundary cases.

## Data-safety contract

The first live GUI session produced a duplicate send. Root cause: send
completions were matched by a request ID stored *after* the COM call
returned — a completion arriving inside that race window was dropped, the
90-second wait timed out, and the retry resent a message that had already
been delivered. The backend now guarantees:

1. **Single-flight send matching.** One send in flight at a time; any
   completion during that window belongs to it. Request IDs are
   logging-only.
2. **No retry on unknown outcomes.** If the modem accepts a request but no
   completion arrives within 90 s, the result is UNCONFIRMED and nothing is
   resent — the message may already be in flight. Retries happen only after
   an explicit failure completion, maximum one.
3. **Sends never queue behind store reads.** Sweeps poll in 250 ms slices
   and yield the moment a send is queued.
4. **Idempotent imports.** Re-import protection is by exact store-message
   ID at the receiver (persisted), so restarts never duplicate history and
   identical rapid-fire texts are never dropped.
5. **Never destructive.** The backend never deletes from the modem store;
   the local store file is written atomically-debounced with corrupt-file
   recovery.

## Error-code quick table

| Signal | Meaning | Action |
|---|---|---|
| send refused, WWAN code 33 (`0xC0040021`) after ~60 s | carrier-side SMS gate (typically unactivated prepaid) | activate/top up the SIM; no code change |
| `+CME ERROR: sim not inserted` / 10/13 | SIM not detected | reseat; check orientation |
| `+CMS ERROR: 310` | SIM absent | reseat |
| `+CMS ERROR: 304` | unknown PDU parameter | treat as a codec bug — report it |
| reads return `0x80548222` | empty message store (firmware-specific) | normal, not a failure |
| `LINK LOST` in console | transport died (WwanSvc bounce, device re-enumeration) | automatic — the supervisor rebuilds the backend |

## AT-port escape hatch

On modem compositions that expose no AT COM port (this EM120R-GL ships
MBIM/QMUX-only), the AT path can be surfaced with vendor drivers or a
one-time USB composition switch from a Linux live session:
`docs/UNLOCK-AT-PORT.md` and `tools/at-unlock.sh`.
