#!/usr/bin/env bash
# WwanMessenger AT-port unlock — run from a Linux live session (e.g. Ubuntu USB).
#
#   sudo bash at-unlock.sh            # show current composition, then switch
#   sudo bash at-unlock.sh --restore 0
#
# Finds the Quectel EM120R-GL's AT channel, prints the current usbnet /
# data_interface configuration, and (after confirmation) switches the USB
# composition so Windows gets an AT serial port.
set -u

say()  { printf '\n== %s\n' "$*"; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run with sudo"

RESTORE=""
if [ "${1:-}" = "--restore" ] && [ -n "${2:-}" ]; then
    RESTORE="$2"
fi

say "candidate AT channels"
CANDS=""
for d in /dev/ttyUSB* /dev/mhi_*AT* /dev/serial/by-id/*quectel*; do
    [ -e "$d" ] || continue
    echo "  $d"
    CANDS="$CANDS $d"
done
[ -n "$CANDS" ] || die "no modem serial channels found (is rfkill blocking? try: rfkill unblock all; modprobe mhi_pci_controller option 2>/dev/null)"

ATPORT=""
for d in $CANDS; do
    if stty -F "$d" 115200 raw -echo 2>/dev/null; then
        printf 'AT\r' > "$d"
        sleep 1
        if head -c 64 "$d" 2>/dev/null | grep -q 'OK'; then
            ATPORT="$d"
            echo "  -> $d answers AT"
            break
        fi
    fi
done
[ -n "$ATPORT" ] || die "no channel answered 'AT' — try the other candidates manually with: screen <dev> 115200"

at() {  # at <command> -> prints response lines
    printf '%s\r' "$1" > "$ATPORT"
    sleep 2
    timeout 3 cat "$ATPORT" 2>/dev/null | tr -d '\r'
}

say "modem identity"
at "ATI" | grep -E 'Quectel|Model|Revision' || true

say "current composition (NOTHING CHANGED YET)"
echo "-- usbnet:";            at 'AT+QCFG="usbnet"' | grep -i qcfg || true
echo "-- data_interface:";    at 'AT+QCFG="data_interface"' | grep -i qcfg || true

say "values the firmware reports as supported"
at 'AT+QCFG=?' | grep -i usbnet || echo "(usbnet not listed — check the EM120R-GL AT manual before changing anything)"

if [ -n "$RESTORE" ]; then
    say "restoring usbnet=$RESTORE"
    at "AT+QCFG=\"usbnet\",$RESTORE"
    at 'AT+CFUN=1,1'
    echo "done — modem rebooting with the restored composition."
    exit 0
fi

say "switching composition"
# On Quectel LTE-A/5G modules usbnet=5 is the composition that routes
# NCM + AT + DIAG + NMEA + MODEM over USB (RMNET/QMUX-only is 0 — what the
# machine currently runs). Verify against the AT+QCFG=? listing above and the
# EM120R-GL AT Commands Manual if unsure.
read -r -p "Set AT+QCFG=\"usbnet\",5 and reboot the modem? [y/N] " ans
[ "$ans" = "y" ] || { echo "aborted — nothing changed"; exit 0; }

at 'AT+QCFG="usbnet",5'
at 'AT+CFUN=1,1'

say "done"
echo "Modem is rebooting with the AT-capable composition."
echo "Shut down, remove the USB stick, boot Windows, then in WwanMessenger: SCAN → pick the Quectel AT Port → CONNECT."
echo "To undo from Linux: sudo bash $0 --restore 0"
