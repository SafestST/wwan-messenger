# WwanMessenger — setup guide for users

The goal of this page: **anyone with a WWAN laptop runs one EXE and texts.**
No installer, no .NET to install, no phone tethering. Verified end-to-end on
a ThinkPad X1 Carbon Gen 9 (Quectel EM120R-GL modem, Orange PL SIM).

## 1. Requirements

| Need | Detail |
|---|---|
| Windows 10 (19041+) or 11, x64 | Windows 10 version 2004 / 20H2 or newer |
| WWAN modem built in | The laptop's cellular modem, visible to Windows as "Mobile broadband". ThinkPad/Fibocom/Quectel modules are the tested path |
| SIM card with SMS provisioned | A normal consumer SIM. **Data-only SIMs cannot send SMS** — see §5 |
| Carrier activation | A prepaid SIM that was never activated will register on the network and still refuse every send (lived experience — §5, row 2) |

If the machine has no WWAN modem at all, the app still runs and connects to
its built-in **SIMULATOR** (§3) — nothing breaks, you just can't text the
real world.

## 2. Get the app (zero install)

Build it once on any machine with the .NET 6 SDK, then copy the folder:

```powershell
cd wwan-messenger
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

This produces `dist\WwanMessenger\` containing **WwanMessenger.exe** (a single
compressed, self-contained file), `README.md` and this guide. Copy that
folder anywhere (Desktop is fine) and double-click **WwanMessenger.exe**. The
.NET runtime is inside the EXE — the target laptop needs nothing installed.

A `-FrameworkDependent` variant (tiny EXE, requires .NET 6 Desktop Runtime
on the target) also exists; the tradeoffs are documented in the header of
`tools\publish.ps1`. Default is the right one for giving to people.

Uninstall = delete the folder (plus `%AppData%\WwanMessenger` if you want your
message history gone too).

## 3. First run, five minutes

1. **Put the SIM in** (usually under or beside the battery compartment —
   check your laptop's manual for the slot).
2. **Let Windows see it.** Open Settings → Network & Internet → **Cellular**.
   Windows detects the modem and shows the carrier. If the page is missing
   entirely, Windows doesn't see a modem — fix that first (§5, row 1).
3. **Run WwanMessenger.exe.** The header bar shows a port dropdown:
   - **No modem present** → the app selects **SIMULATOR** (labelled
     `◼ SIMULATOR`) and auto-connects. A Dispatch Bot answers everything you
     send, including deliberately long multipart messages — this is the
     no-risk way to see the app work.
   - **Modem present** → the entry **`◆ MBIM MODEM`** appears, gets selected
     automatically, and the app auto-connects: LINK → SIM → NET lamps come
     up in the header.
4. **Send a text.** `+ NEW` → type a number in full international form
   (`+48…`, `+44…`) → write → Enter. The compose strip shows the live
   encoding budget (`GSM7 · 1 SEG · 23/160`), so you know how long you can
   run.
5. **Receive a text.** Reply from the other phone. It appears in the
   conversation within a second or two (how that works: §4). Messages that
   arrived while the app was closed are imported on the next start.

Anything misbehaves, open **CONSOLE »** in the header — the live transcript
of every modem exchange is exactly what to paste into an issue.

## 4. How send and receive actually work

**Sending** rides the same path Windows itself would use. WwanMessenger encodes
your text as a raw SMS protocol packet (GSM 03.40 PDU — character set,
multipart segmentation and all) and hands it to the Windows Mobile
Broadband COM API (`IMbnSms::SmsSendPdu`). WwanSvc carries it to the modem
over MBIM, the modem passes it to the carrier's message centre, and the
network's completion (typically `S_OK` in under a second on a healthy
setup) is reported back to the UI. The send channel is deliberately
conservative: exactly one request in flight, every completion matched to
that request, and **no blind retries** — if the modem accepts a send but
the network never answers, the message is marked unconfirmed, never
silently resent. (This contract exists because the first live session
produced exactly one duplicate send before it was written.)

**Receiving** uses a discovery about Windows itself: WwanSvc intercepts
every inbound MBIM SMS, writes it into the operating system's **Chat
platform store** (`Windows.ApplicationModel.Chat`), and deletes it from the
modem's own message store before any application could read it — modem-store
reads come back empty *by design*, not by failure. WwanMessenger therefore reads
the Chat store: an initial sweep imports existing history when the app
starts, and the store's change notification drives live delivery
afterwards. The discovery is documented at the top of
`src\WwanMessenger.Core\Mbn\ChatStoreReceiver.cs`. Your full local history lives
in `%AppData%\WwanMessenger\store.json` and survives restarts.

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No `◆ MBIM MODEM` entry in the dropdown | Windows doesn't see the modem: WwanSvc stopped, driver missing, or WWAN disabled | Settings → Network & Internet → Cellular present? Check `netsh mbn show interfaces` (§6). Elevated PowerShell: `Start-Service WwanSvc`. Device Manager → Network adapters → look for "Mobile Broadband" / your modem model; reinstall the vendor WWAN driver from your laptop maker |
| Connects, but every send is accepted then fails after exactly ~60 s (error mentions code 33 / `0xC0040021`) | Prepaid SIM not activated/registered — network-side refusal; registration and data attach still look healthy | Activate the SIM (top it up / finish carrier registration). This exact signature was our blocker; a top-up cleared it instantly. The decisive test: put the SIM in a phone — if the phone can't text either, it's the carrier |
| SIM lamp red / console says "SIM absent?" | SIM not detected | Power off, reseat the SIM, check orientation; try the SIM in a phone to confirm it's alive |
| Sends work, nothing ever received | Testing wrong, or unusual driver stack | First: send a text **from another phone to your SIM's number** — replying to your own outbound is the reliable test. If still nothing and the other phone reports delivery, capture the CONSOLE transcript and open an issue: on the verified hardware Windows routes inbound SMS into the Chat store; a vendor driver that parks them in the modem store instead is the known variant (the backend ships a modem-store sweep fallback for exactly that case) |
| `netsh mbn show smsconfig` says `Ready to send SMS : No` | Carrier/modem SMS provisioning missing | Carrier-side: ask them to enable SMS (and IMS/VoLTE on LTE-only modems). Data-only plans commonly ship without SMS |
| `+CMS ERROR: 500/38` on every send (AT backend) | SMS not provisioned on the plan | Same as above — carrier |
| Modem store visibly filling, app shows nothing | Expected on this stack | Windows empties the modem store into the Chat store (§4). Not a fault |

## 6. Zero-code sanity checks

Copy-pasteable, run from any PowerShell:

```powershell
netsh mbn show interfaces
```
Want: your modem listed with `State : ...` and a `Name` (e.g. `Cellular`).

```powershell
netsh mbn show smsconfig interface="Cellular"
```
(Use the exact `Name` from the previous command.) Want:
`Ready to send SMS : Yes`.

```powershell
Get-Service WwanSvc
```
Want: `Status : Running`. (This Windows build has no `netsh mbn sms
read/send` subcommands — the app talks to the same APIs directly.)

## 7. Appendix: the AT-port path

Modern modems (this Quectel included, in its factory USB composition) expose
no AT serial port to Windows — the MBIM path above is the intended one. If
your modem *does* expose AT ports, or you want to unlock them (more direct
control, an alternate receive channel), WwanMessenger speaks raw AT there too:
press **SCAN**, pick the modem port, **CONNECT**. The full procedure —
Quectel's Windows USB driver package, or a one-time Linux session to switch
the USB composition — is in **[docs\UNLOCK-AT-PORT.md](UNLOCK-AT-PORT.md)**.
