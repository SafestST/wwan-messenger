using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using WwanMessenger.App.Services;
using WwanMessenger.Core.At;
using WwanMessenger.Core.Channels;
using WwanMessenger.Core.Mbn;
using WwanMessenger.Core.Modem;
using WwanMessenger.Core.Sms;
using WwanMessenger.Core.Storage;

namespace WwanMessenger.App.ViewModels;

public sealed class ConsoleLine
{
    public ConsoleLine(string text, string colorKey)
    {
        Text = text;
        Brush = App.Current.Resources.Contains(colorKey)
            ? (System.Windows.Media.Brush)App.Current.Resources[colorKey]
            : (System.Windows.Media.Brush)(App.Current.Resources.Contains("FgMid")
                ? App.Current.Resources["FgMid"]
                : System.Windows.Media.Brushes.Gray);
    }
    public string Text { get; }
    public System.Windows.Media.Brush Brush { get; }
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settingsStore = new();
    private readonly MessageStore _store = new();
    private readonly Settings _settings;

    private ISmsBackend? _service;
    private SimulatedModemChannel? _sim;
    private bool _connecting;
    private bool _recovering;
    private static bool? _mbimAvailable;

    public ObservableCollection<ConversationViewModel> Conversations { get; } = new();
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = new();
    public ObservableCollection<PortOption> Ports { get; } = new();

    private ConversationViewModel? _selectedConversation;
    private string _composeText = "";
    private string _segmentCounter = "";
    private bool _consoleOpen;
    private bool _connected;
    private string _statusLine = "OFFLINE";

    // status readouts
    private string _linkLabel = "LINK · OFF";
    private string _simLabel = "SIM · ?";
    private string _netLabel = "NET · ?";
    private System.Windows.Media.Brush _linkBrush = Dim;
    private System.Windows.Media.Brush _simBrush = Dim;
    private System.Windows.Media.Brush _netBrush = Dim;
    private string _operatorReadout = "NO OPERATOR";
    private string _radioReadout = "";
    private string _rssiReadout = "—";
    private int _signalBars;
    private string _connectButtonLabel = "CONNECT";
    private PortOption? _selectedPort;

    private static System.Windows.Media.Brush Res(string key)
        => Application.Current.Resources.Contains(key)
            ? (System.Windows.Media.Brush)Application.Current.Resources[key]
            : (System.Windows.Media.Brush)Application.Current.Resources["FgMid"];

    private static System.Windows.Media.Brush Dim => Res("FgMid");
    private static System.Windows.Media.Brush Green => Res("GreenOk");
    private static System.Windows.Media.Brush Red => Res("RedBad");
    private static System.Windows.Media.Brush Amber => Res("Accent");
    private static System.Windows.Media.Brush Cyan => Res("CyanInfo");

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = _settingsStore.Load();

        SeedData.EnsureSeeded(_store);
        foreach (var conv in _store.Conversations)
            Conversations.Add(new ConversationViewModel(conv));
        if (Conversations.Count > 0) SelectedConversation = Conversations[0];

        RefreshPorts();
        SortConversations();

        ConnectCommand = new RelayCommand(_ => _ = ConnectAsync(), _ => !_connected && !_connecting);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => _connected);
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => _connected && SelectedConversation is not null && ComposeText.Trim().Length > 0);
        ToggleConsoleCommand = new RelayCommand(_ => ConsoleOpen = !ConsoleOpen);
        RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
        CycleThemeCommand = new RelayCommand(_ => CycleTheme());

        ApplyTheme(_settings.Theme);
        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += (_, _) => RefreshLinkElapsed();
        _ticker.Start();

        UpdateSegmentCounter();
    }

    // ---- commands ----------------------------------------------------------

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand SendCommand { get; }
    public RelayCommand ToggleConsoleCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand CycleThemeCommand { get; }

    // ---- theme -------------------------------------------------------------

    private static readonly string[] ThemeOrder = { "paper", "graphite", "nord" };

    public string ThemeName => _settings.Theme.ToUpperInvariant();

    /// <summary>Swaps the brush dictionary at runtime; DynamicResource
    /// references restyle live without a restart.</summary>
    private void ApplyTheme(string theme)
    {
        if (!ThemeOrder.Contains(theme)) theme = "paper";
        _settings.Theme = theme;
        _settingsStore.Save(_settings);
        try
        {
            var uri = new Uri($"Theme/{theme}.xaml", UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };
            var merged = Application.Current.Resources.MergedDictionaries;
            // index 0 is the theme dictionary (App.xaml merge order: theme, shared)
            if (merged.Count > 0 && merged[0].Source?.OriginalString.Contains("Theme/") == true)
                merged[0] = dict;
            else
                merged.Insert(0, dict);
            Raise(nameof(ThemeName));
            UiLog($"THEME: {theme.ToUpperInvariant()}");
        }
        catch (Exception ex)
        {
            UiLog($"THEME: failed to load '{theme}' — {ex.Message}");
        }
    }

    public void CycleTheme()
    {
        int idx = Array.IndexOf(ThemeOrder, _settings.Theme);
        ApplyTheme(ThemeOrder[(idx + 1) % ThemeOrder.Length]);
    }

    // ---- contact rename ----------------------------------------------------

    public void RenameContact(ConversationViewModel conv, string newName)
    {
        var name = newName.Trim();
        _store.SetDisplayName(conv.Model, string.IsNullOrEmpty(name) ? null : name);
        conv.SetDisplayName(string.IsNullOrEmpty(name) ? null : name);
        UiLog($"RENAME: {conv.Number} \u2192 {name}");
    }

    // ---- live link ticker --------------------------------------------------

    private readonly DispatcherTimer _ticker;

    /// <summary>LABEL: "LINK · UP 12s" — seconds since the last successful
    /// poll, ticking every second until the next poll refreshes it.</summary>
    private void RefreshLinkElapsed()
    {
        if (_service is null) return;
        var s = _service.Status;
        int secs = s.LastPollLocal is { } t ? (int)Math.Clamp(DateTime.Now.Subtract(t).TotalSeconds, 0, 9999) : -1;
        LinkLabel = s.Link switch
        {
            LinkState.Online when secs >= 0 => $"LINK · UP {secs}s",
            LinkState.Connecting when secs >= 0 => $"LINK · SYNC {secs}s",
            _ => "LINK · OFF",
        };
    }

    // ---- bindables ---------------------------------------------------------

    public ConversationViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (Set(ref _selectedConversation, value) && value is not null)
            {
                value.MarkRead();
                _store.MarkRead(value.Model);
                Raise(nameof(HasConversation));
            }
        }
    }

    public bool HasConversation => SelectedConversation is not null;

    public string ComposeText
    {
        get => _composeText;
        set { if (Set(ref _composeText, value)) UpdateSegmentCounter(); }
    }

    public string SegmentCounter
    {
        get => _segmentCounter;
        private set => Set(ref _segmentCounter, value);
    }

    public bool ConsoleOpen
    {
        get => _consoleOpen;
        set => Set(ref _consoleOpen, value);
    }

    public bool Connected
    {
        get => _connected;
        private set
        {
            if (Set(ref _connected, value)) Raise(nameof(Disconnected));
        }
    }

    public bool Disconnected => !Connected;

    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
    public string LinkLabel { get => _linkLabel; private set => Set(ref _linkLabel, value); }
    public string SimLabel { get => _simLabel; private set => Set(ref _simLabel, value); }
    public string NetLabel { get => _netLabel; private set => Set(ref _netLabel, value); }
    public System.Windows.Media.Brush LinkBrush { get => _linkBrush; private set => Set(ref _linkBrush, value); }
    public System.Windows.Media.Brush SimBrush { get => _simBrush; private set => Set(ref _simBrush, value); }
    public System.Windows.Media.Brush NetBrush { get => _netBrush; private set => Set(ref _netBrush, value); }
    public string OperatorReadout { get => _operatorReadout; private set => Set(ref _operatorReadout, value); }
    public string RadioReadout { get => _radioReadout; private set => Set(ref _radioReadout, value); }
    public string RssiReadout { get => _rssiReadout; private set => Set(ref _rssiReadout, value); }
    public int SignalBars { get => _signalBars; private set => Set(ref _signalBars, value); }
    public string ConnectButtonLabel { get => _connectButtonLabel; private set => Set(ref _connectButtonLabel, value); }

    public PortOption? SelectedPort
    {
        get => _selectedPort;
        set => Set(ref _selectedPort, value);
    }

    // ---- connectivity ------------------------------------------------------

    public void RefreshPorts()
    {
        var selected = SelectedPort?.Name;
        Ports.Clear();
        Ports.Add(new PortOption("SIMULATOR", null));
        if (MbnAvailable())
            Ports.Add(new PortOption("MBIM MODEM", MbnSmsService.IsAvailable()
                ? "Windows WWAN service (live modem)" : null));
        try
        {
            foreach (var p in ModemDiscovery.Discover())
                Ports.Add(new PortOption(p.PortName, p.FriendlyName));
        }
        catch { /* discovery is best-effort */ }
        string preferred = _settings.Mode switch
        {
            "mbim" when MbnAvailable() => "MBIM MODEM",
            "serial" when _settings.PortName is not null => _settings.PortName,
            _ => MbnAvailable() ? "MBIM MODEM" : "SIMULATOR",
        };
        var restore = Ports.FirstOrDefault(p => p.Name == selected)
                      ?? Ports.FirstOrDefault(p => p.Name == preferred)
                      ?? Ports.FirstOrDefault(p => p.Name == "MBIM MODEM")
                      ?? Ports[0];
        SelectedPort = restore;

        UiLog($"SCAN: {Ports.Count} transport(s) available — {string.Join(", ", Ports.Select(p => p.Name))}");
        UiLog("SCAN: the active connection is unaffected by scanning");
    }

    /// <summary>App-level log line surfaced in the console drawer.</summary>
    private void UiLog(string text) => _ = _dispatcher.BeginInvoke(() =>
    {
        ConsoleLines.Add(new ConsoleLine("SYS· " + text, "FgMid"));
        while (ConsoleLines.Count > 400)
            ConsoleLines.RemoveAt(0);
    });

    private static bool MbnAvailable()
    {
        _mbimAvailable ??= Task.Run(() =>
        {
            try { return MbnSmsService.IsAvailable(); }
            catch { return false; }
        }).GetAwaiter().GetResult();
        return _mbimAvailable.Value;
    }

    private ISmsBackend CreateBackend()
    {
        if (SelectedPort is not null && SelectedPort.Name == "MBIM MODEM")
        {
            _settings.Mode = "mbim";
            _settings.PortName = null;
            return new MbnSmsService();
        }
        if (SelectedPort is not null && SelectedPort.Name != "SIMULATOR")
        {
            _settings.Mode = "serial";
            _settings.PortName = SelectedPort.Name;
            return new SmsService(new SerialModemChannel(SelectedPort.Name));
        }
        _settings.Mode = "sim";
        _settings.PortName = null;
        _sim = new SimulatedModemChannel
        {
            Scenario = (SimScenario)Math.Clamp(_settings.SimScenario, 0, 3),
        };
        return new SmsService(_sim);
    }

    public async Task ConnectAsync()
    {
        if (_connecting || Connected) return;
        _connecting = true;
        ConnectButtonLabel = "LINKING…";
        StatusLine = "OPENING CHANNEL";

        _service = CreateBackend();
        _settingsStore.Save(_settings);

        UiLog($"CONNECT: transport = {SelectedPort?.Name ?? "?"}");
        _service.Logged += OnLogged;
        _service.MessageReceived += OnMessageReceived;
        _service.StatusChanged += () => _dispatcher.BeginInvoke(RefreshStatus);

        var ok = await Task.Run(_service.ConnectAsync);

        _connecting = false;
        Connected = ok;
        ConnectButtonLabel = ok ? "ON AIR" : "RETRY";
        StatusLine = ok ? "CHANNEL UP" : "LINK FAILED";
        RefreshStatus();
        RelayCommand.Refresh();
    }

    public void Disconnect()
    {
        if (_service is null) return;
        try
        {
            _service.Logged -= OnLogged;
            _service.MessageReceived -= OnMessageReceived;
            _service.Dispose();
        }
        catch { /* tearing down */ }
        _service = null;
        _sim = null;
        Connected = false;
        ConnectButtonLabel = "CONNECT";
        StatusLine = "OFFLINE";
        LinkBrush = SimBrush = NetBrush = Dim;
        LinkLabel = "LINK · OFF";
        SimLabel = "SIM · ?";
        NetLabel = "NET · ?";
        OperatorReadout = "NO OPERATOR";
        RssiReadout = "—";
        SignalBars = 0;
        RelayCommand.Refresh();
    }

    private void RefreshStatus()
    {
        if (_service is null) return;
        var s = _service.Status;
        RefreshLinkElapsed();
        LinkBrush = s.Link switch
        {
            LinkState.Online => Green,
            LinkState.Connecting => Amber,
            _ => Dim,
        };
        SimLabel = "SIM · " + s.Sim.Describe();
        SimBrush = s.Sim switch
        {
            SimState.Ready => Green,
            SimState.Absent => Red,
            SimState.PinRequired => Amber,
            _ => Dim,
        };
        NetLabel = "NET · " + s.Net.Describe();
        NetBrush = s.Net switch
        {
            NetState.Home => Green,
            NetState.Roaming => Cyan,
            NetState.Searching => Amber,
            NetState.Denied => Red,
            _ => Dim,
        };
        OperatorReadout = string.IsNullOrEmpty(s.OperatorName)
            ? (s.ModemIdentity is null ? "NO OPERATOR" : s.ModemIdentity.ToUpperInvariant())
            : s.OperatorName.ToUpperInvariant();
        RadioReadout = string.IsNullOrEmpty(s.RadioTech)
            ? ""
            : (string.IsNullOrEmpty(s.RoamingText) ? s.RadioTech : $"{s.RadioTech} · ROAMING");
        RssiReadout = s.Rssi is >= 0 and <= 31 ? s.RssiDbm : "NO SIGNAL";
        SignalBars = s.Bars;

        // mobility supervision: the backend declared the link dead (dead COM
        // objects — WwanSvc bounce, device re-enumeration). Tear it down and
        // rebuild with fresh objects, backing off, until we're back.
        if (Connected && s.Link == LinkState.Offline && !_recovering)
            _ = AutoRecoverAsync();
    }

    /// <summary>
    /// Drop-and-rebuild recovery: disposes the dead backend entirely (fresh COM
    /// objects, fresh chat-store receiver) and reconnects with exponential-ish
    /// backoff. Ordinary radio gaps (tower handovers, tunnels) never reach here
    /// — those surface as reg=Searching on healthy polls — this handles the
    /// transport itself dying.
    /// </summary>
    private async Task AutoRecoverAsync()
    {
        if (_recovering) return;
        _recovering = true;
        _connecting = true; // block manual CONNECT/CONNECT-disconnect races during recovery
        try
        {
            UiLog("RECONNECT: link lost — rebuilding transport");
            StatusLine = "RECONNECTING";
            ConnectButtonLabel = "RECOVERY";

            int attempt = 0;
            while (true)
            {
                attempt++;
                await Task.Run(Disconnect);
                await Task.Delay(attempt <= 2 ? 2000 : Math.Min(15000, 3000 * attempt));
                try
                {
                    _service = CreateBackend();
                    _service.Logged += OnLogged;
                    _service.MessageReceived += OnMessageReceived;
                    _service.StatusChanged += () => _dispatcher.BeginInvoke(RefreshStatus);
                    var ok = await Task.Run(_service.ConnectAsync);
                    if (ok)
                    {
                        Connected = true;
                        ConnectButtonLabel = "ON AIR";
                        StatusLine = $"RECOVERED (attempt {attempt})";
                        UiLog($"RECONNECT: back online after {attempt} attempt(s)");
                        RefreshStatus();
                        break;
                    }
                    UiLog($"RECONNECT: attempt {attempt} failed");
                }
                catch (Exception ex)
                {
                    UiLog($"RECONNECT: attempt {attempt} error — {ex.Message}");
                }
            }
        }
        finally
        {
            _recovering = false;
            _connecting = false;
            RelayCommand.Refresh();
        }
    }

    // ---- messaging ---------------------------------------------------------

    public async Task SendAsync()
    {
        var target = SelectedConversation;
        var text = ComposeText.TrimEnd();
        if (target is null || _service is null || text.Length == 0) return;

        ComposeText = "";

        var model = _store.AddOutbound(target.Number, target.Model.DisplayName, text);
        var vm = new MessageViewModel(model);
        _ = _dispatcher.BeginInvoke(() =>
        {
            target.Append(vm);
            SortConversations();
        });

        var result = await Task.Run(() => _service.SendAsync(target.Number, text));
        _store.UpdateMessage(model, result.Success ? MessageStatus.Sent : MessageStatus.Failed, result.ModemRef);
        _ = _dispatcher.BeginInvoke(() =>
        {
            vm.Refresh();
            StatusLine = result.Success ? "FRAME SENT" : $"SEND FAILED — {result.Detail}";
        });
    }

    private void OnMessageReceived(SmsIncomingMessage msg)
    {
        var name = GuessName(msg.Sender);
        var model = _store.AddInbound(msg.Sender, name, msg.Text, msg.ReceivedLocal.ToUniversalTime());
        _ = _dispatcher.BeginInvoke(() =>
        {
            var conv = Conversations.FirstOrDefault(c => c.Number == msg.Sender);
            if (conv is null)
            {
                conv = new ConversationViewModel(_store.GetOrAddConversation(msg.Sender, name));
                Conversations.Insert(0, conv);
            }
            else
            {
                conv.Append(new MessageViewModel(model));
                if (conv != SelectedConversation) conv.NotifyIncoming();
            }
            SortConversations();
            StatusLine = "INBOUND FRAME";
        });
    }

    private static string? GuessName(string number) => number switch
    {
        "+447700900123" => "Dispatch Bot",
        "+447700900456" => "Site Alpha",
        _ => null,
    };

    /// <summary>
    /// Channel ordering, phone-style: pinned channels stay on top (ordered by
    /// latest activity), everything else follows by latest activity. Uses Move
    /// operations so selection and containers survive the reorder.
    /// </summary>
    public void SortConversations()
    {
        var ordered = Conversations
            .OrderByDescending(c => c.Model.Pinned)
            .ThenByDescending(c => c.Model.LastActivityUtc)
            .ToList();
        for (int target = 0; target < ordered.Count; target++)
        {
            var current = Conversations.IndexOf(ordered[target]);
            if (current >= 0 && current != target)
                Conversations.Move(current, target);
        }
    }

    public void TogglePin(ConversationViewModel conv)
    {
        conv.SetPinned(!conv.Model.Pinned);
        _store.SetPinned(conv.Model, conv.Model.Pinned);
        UiLog($"{(conv.Model.Pinned ? "PINNED" : "UNPINNED")}: {conv.Title}");
        SortConversations();
    }

    public void AddChannel(string number, string name)
    {
        var normalized = PhoneNumbers.Normalize(number);
        if (normalized.Length < 6) return;
        var existing = Conversations.FirstOrDefault(c => c.Number == normalized);
        if (existing is not null) { SelectedConversation = existing; return; }
        var model = _store.GetOrAddConversation(normalized, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        var vm = new ConversationViewModel(model);
        Conversations.Insert(0, vm);
        SelectedConversation = vm;
    }

    private void UpdateSegmentCounter()
    {
        if (ComposeText.Trim().Length == 0) { SegmentCounter = ""; return; }
        var plan = Segmenter.Plan(ComposeText);
        SegmentCounter = plan.Total == 0 ? "" : plan.Describe(ComposeText);
    }

    // ---- console -----------------------------------------------------------

    private void OnLogged(AtLineKind kind, string text) => _dispatcher.BeginInvoke(() =>
    {
        var (prefix, color) = kind switch
        {
            AtLineKind.Tx => ("TX» ", "Accent"),
            AtLineKind.Urc => ("URC‡ ", "CyanInfo"),
            AtLineKind.Info => ("SYS· ", "FgMid"),
            _ => ("RX« ", "GreenOk"),
        };
        ConsoleLines.Add(new ConsoleLine(prefix + text, color));
        while (ConsoleLines.Count > 400)
            ConsoleLines.RemoveAt(0);
    });

    public void Dispose()
    {
        _service?.Dispose();
        _store.Dispose();
        _settingsStore.Save(_settings);
    }
}

public sealed record PortOption(string Name, string? FriendlyName)
{
    public override string ToString()
    {
        if (Name == "SIMULATOR") return "▪ SIMULATOR";
        if (Name == "MBIM MODEM") return "◆ MBIM MODEM";
        return FriendlyName is null ? Name : $"{Name} — {FriendlyName}";
    }
}
