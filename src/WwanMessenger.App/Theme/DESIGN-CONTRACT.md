# WwanMessenger UI overhaul — design contract (v1)

The three workstreams (theme, windows, packaging) parallelize against THIS
contract. Resource keys are frozen: the theme file defines them, the windows
consume them, the integrator verifies runtime resolution.

## Product
WWAN MESSENGER — WWAN messaging console. A native Windows SMS client for laptops
with built-in WWAN modems. Current stack: WPF on .NET 6
(net6.0-windows10.0.19041.0). ZERO external UI packages — the win is craft,
not dependencies.

## Design direction ("sexy, modern, not bloated")
- Dark graphite foundation with a **warm amber accent** (brand signature),
  phosphor green/red/cyan semantics preserved (they map to LINK/SIM/NET
  lamps, message status words).
- Modern shapes: cards with CornerRadius 12, buttons 8; soft hairlines;
  generous whitespace; 8-pt spacing rhythm.
- Typography: Segoe UI Variable Display for UI, Cascadia Mono for technical
  readouts (timestamps, counters, AT console, lamp labels).
- Subtle motion: 120–150 ms hover color transitions; no heavyweight
  animations.
- Depth without glass: layered panel tones + a 1px top-highlight on raised
  surfaces; DropShadowEffect used sparingly (compose bar, send button).
- The instrument-console DNA stays: lamps, signal meter, mono tags — but
  executed with polish, not bare rectangles.

## Frozen resource keys (Theme/Resources.xaml MUST define all of these)
Brushes: Bg, RailBg, PanelBg, PanelHi, CardIn, CardOut, Line, Accent,
AccentSoft, GreenOk, RedBad, CyanInfo, Fg, FgMid, FgLow, UnreadBadge.
Fonts:   UiFont, MonoFont.
Styles (TargetType / x:Key):
  - TextBlock  MonoTag      — small mono caption (labels, readouts)
  - TextBlock  BrandTitle   — the WWAN MESSENGER wordmark
  - Button     BtnGhost     — quiet bordered button (CONSOLE, SCAN, + NEW, CANCEL)
  - Button     BtnAccent    — primary filled accent button (SEND, CONNECT/ON AIR, OPEN)
  - ComboBox   CmbDark      — dark combo box
  - TextBox    TxtCompose   — compose input
  - ListBoxItem ChannelItem — channel rail item container (selection + hover visuals)
Extras the windows may assume exist: a ScrollBar implicit style (thin, dark).

## Frozen bindings & names (MainWindow.xaml MUST keep, verbatim)
- x:Name: Transcript, ConsoleView, ComposeInput, (NewChannelDialog: NumberInput, NameInput)
- Handlers: OnLoaded, OnClosing, OnNewChannel (Click), OnComposeKeyDown (KeyDown),
  dialog OnOpen/OnNumberKeyDown
- Binding paths (MainViewModel is unchanged): Conversations, SelectedConversation,
  Ports, SelectedPort, LinkLabel, SimLabel, NetLabel, LinkBrush, SimBrush, NetBrush,
  OperatorReadout, RssiReadout, SignalBars, ConnectButtonLabel, StatusLine,
  ComposeText, SegmentCounter, Connected, ConsoleOpen, HasConversation,
  item-level: Title, Number, Initials, LastPreview, LastTime, UnreadCount, HasUnread,
  Messages, Text, TimeDisplay, StatusWord, StatusBrush, IsIncoming (uses BoolToVis /
  InverseBoolToVis converters), ConsoleLines item: Text, Brush
- Commands: ConnectCommand, DisconnectCommand (if used), SendCommand,
  ToggleConsoleCommand, RefreshPortsCommand, NewChannel via OnNewChannel click
- Buttons must keep Content/Name: "CONSOLE »", "SEND »", "+ NEW", "SCAN"
- Signal meter: may be restyled; must keep binding to SignalBars (int 0–5)

## Out of bounds for all workstreams
- WwanMessenger.Core, WwanMessenger.Smoke, WwanMessenger.Cli (backend/test code — untouched)
- ViewModels in WwanMessenger.App (MainViewModel etc.) — bindings must resolve against
  the CURRENT viewmodels; do not rename properties
- Installing packages, git operations, launching the GUI
